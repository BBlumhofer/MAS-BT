using System;
using System.Threading.Tasks;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class DispatchCapabilityRequestsNode : BTNode
{
    public DispatchCapabilityRequestsNode() : base("DispatchCapabilityRequests") { }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<I40Message>> _pendingCreateDescription = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<I40Message>> _pendingCalcSimilarity = new();
    private bool _responseHandlerRegistered = false;
    private bool _subscribedCreateDescriptionResponse = false;
    private bool _subscribedCalcSimilarityResponse = false;

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("DispatchCapabilityRequests: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ctx = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        if (ctx == null)
        {
            Logger.LogError("DispatchCapabilityRequests: negotiation context missing");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var topic = $"/{ns}/DispatchingAgent/Offer";

        // Cache emitted CfPs so the collector can re-issue them when a target module registers late.
        // MQTT does not deliver messages that were published before a subscriber connected.
        var cfpsByTarget = new Dictionary<string, List<I40Message>>(StringComparer.OrdinalIgnoreCase);
        var cfpDispatchUtc = DateTime.UtcNow;

        var similarityAgentId = Context.Get<string>("config.DispatchingAgent.SimilarityAgentId") ?? $"SimilarityAnalysisAgent_{ns}";
        var descriptionTimeoutMs = GetConfigInt("config.DispatchingAgent.DescriptionTimeoutMs", defaultValue: 30000);
        var similarityTimeoutMs = GetConfigInt("config.DispatchingAgent.SimilarityTimeoutMs", defaultValue: 30000);
        var threshold = GetConfigDouble("config.DispatchingAgent.CapabilitySimilarityThreshold", fallbackKey: "config.SimilarityAnalysis.MinSimilarityThreshold", defaultValue: 0.75);

        var logSimilarityMatrix = GetConfigInt("config.DispatchingAgent.LogSimilarityMatrix", defaultValue: 0) != 0;

        // Diagnostics: remember best match per requirement so we can print what failed later in BuildProcessChainResponse.
        var bestMatchByRequirementId = Context.Get<Dictionary<string, string>>("ProcessChain.SimilarityBestMatch")
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Context.Set("ProcessChain.SimilarityBestMatch", bestMatchByRequirementId);

        // Ensure we have response handlers/subscriptions for CreateDescription/CalcSimilarity
        await EnsureResponseHandlerRegisteredAsync(client).ConfigureAwait(false);
        await EnsureCreateDescriptionResponseListenerAsync(client, ns, similarityAgentId).ConfigureAwait(false);
        await EnsureCalcSimilarityResponseListenerAsync(client, ns, similarityAgentId).ConfigureAwait(false);

        // Ensure descriptions for all registered + requested capabilities are present (cache them)
        var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
        // Store once; state is mutated in-place and uses concurrent dictionaries internally.
        Context.Set("DispatchingState", state);

        // Similarity agent may be optional or might register late. If it's not available, fall back to exact name matching.
        var similarityAgentAvailable = state.Modules.Any(m =>
            !string.IsNullOrWhiteSpace(m.ModuleId)
            && string.Equals(m.ModuleId, similarityAgentId, StringComparison.OrdinalIgnoreCase));

        var capabilitiesToDescribe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cap in state.Modules.SelectMany(m => m.Capabilities))
        {
            if (!string.IsNullOrWhiteSpace(cap)) capabilitiesToDescribe.Add(cap);
        }
        foreach (var req in ctx.Requirements)
        {
            if (!string.IsNullOrWhiteSpace(req.Capability)) capabilitiesToDescribe.Add(req.Capability);
        }

        if (similarityAgentAvailable)
        {
            var descTasks = new List<Task>();
            foreach (var cap in capabilitiesToDescribe)
            {
                if (!state.TryGetCapabilityDescription(cap, out var _))
                {
                    descTasks.Add(RequestCapabilityDescriptionAsync(client, ns, similarityAgentId, cap, descriptionTimeoutMs));
                }
            }
            if (descTasks.Count > 0)
            {
                try { await Task.WhenAll(descTasks).ConfigureAwait(false); }
                catch { /* individual failures are logged inside RequestCapabilityDescriptionAsync */ }
            }
        }

        // For each requirement, compute candidate modules by similarity and send CfP only to those modules
        var expectedOfferResponders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in ctx.Requirements)
        {
            // evaluate modules (pipeline similarity requests; limiter controls in-flight concurrency)
            var allModules = state.Modules.ToList();
            var descReq = state.TryGetCapabilityDescription(requirement.Capability, out var descA) ? descA : null;
            if (string.IsNullOrWhiteSpace(descReq) || !similarityAgentAvailable)
            {
                // Fallback: exact capability name matching if similarity infra is unavailable.
                var candidateModulesExact = allModules
                    .Where(m => !string.IsNullOrWhiteSpace(m.ModuleId)
                                && m.Capabilities.Any(c => string.Equals(c, requirement.Capability, StringComparison.OrdinalIgnoreCase)))
                    .Select(m => m.ModuleId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (candidateModulesExact.Count == 0)
                {
                    Logger.LogWarning(
                        "DispatchCapabilityRequests: no candidate modules found for capability {Capability} (similarity unavailable)",
                        requirement.Capability);
                    continue;
                }

                foreach (var m in candidateModulesExact)
                {
                    expectedOfferResponders.Add(m);
                }

                foreach (var tgtModule in candidateModulesExact)
                {
                    var builder = new I40MessageBuilder()
                        .From(Context.AgentId, Context.AgentRole)
                        .To(tgtModule, "ModuleHolon")
                        .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
                        .WithConversationId(ctx.ConversationId)
                        .AddElement(CreateStringProperty("Capability", requirement.Capability))
                        .AddElement(CreateStringProperty("RequirementId", requirement.RequirementId));

                    if (!string.IsNullOrWhiteSpace(ctx.ProductId))
                    {
                        builder.AddElement(CreateStringProperty("ProductId", ctx.ProductId));
                    }

                    if (requirement.CapabilityContainer != null)
                    {
                        builder.AddElement(requirement.CapabilityContainer);
                    }

                    var cfp = builder.Build();
                    if (!cfpsByTarget.TryGetValue(tgtModule, out var list))
                    {
                        list = new List<I40Message>();
                        cfpsByTarget[tgtModule] = list;
                    }
                    list.Add(cfp);

                    await client.PublishAsync(cfp, topic);
                }

                continue;
            }

            // Optional detailed matrix logging; keep per module a list of cap->score.
            var similarityMatrix = new Dictionary<string, List<(string Capability, double? Score)>>(StringComparer.OrdinalIgnoreCase);

            var moduleTasks = allModules.Select(async module =>
            {
                if (module == null || string.IsNullOrWhiteSpace(module.ModuleId) || module.Capabilities.Count == 0)
                {
                    return (ModuleId: module?.ModuleId ?? string.Empty, Best: double.NegativeInfinity, BestCap: (string?)null);
                }

                var simTasks = new List<Task<(string Cap, double? Sim)>>();
                foreach (var modCap in module.Capabilities)
                {
                    if (string.IsNullOrWhiteSpace(modCap)) continue;
                    var descMod = state.TryGetCapabilityDescription(modCap, out var descB) ? descB : null;
                    if (string.IsNullOrWhiteSpace(descMod)) continue;

                    var capLocal = modCap;
                    simTasks.Add(Task.Run(async () =>
                    {
                        var sim = await GetOrRequestCapabilitySimilarityAsync(
                            client,
                            state,
                            ns,
                            similarityAgentId,
                            capabilityA: requirement.Capability,
                            capabilityB: capLocal,
                            descA: descReq!,
                            descB: descMod!,
                            timeoutMs: similarityTimeoutMs).ConfigureAwait(false);
                        return (Cap: capLocal, Sim: sim);
                    }));
                }

                if (simTasks.Count == 0)
                {
                    return (ModuleId: module.ModuleId, Best: double.NegativeInfinity, BestCap: (string?)null);
                }

                var sims = await Task.WhenAll(simTasks).ConfigureAwait(false);
                double best = double.NegativeInfinity;
                string? bestCap = null;
                var rows = new List<(string Capability, double? Score)>(sims.Length);
                foreach (var (cap, sim) in sims)
                {
                    rows.Add((cap, sim));
                    if (sim.HasValue && sim.Value >= best)
                    {
                        best = sim.Value;
                        bestCap = cap;
                    }
                }

                lock (similarityMatrix)
                {
                    similarityMatrix[module.ModuleId] = rows;
                }

                return (ModuleId: module.ModuleId, Best: best, BestCap: bestCap);
            }).ToList();

            var moduleResults = await Task.WhenAll(moduleTasks).ConfigureAwait(false);

            var ranked = moduleResults
                .Where(r => !string.IsNullOrWhiteSpace(r.ModuleId))
                .OrderByDescending(r => r.Best)
                .ToList();

            var bestOverall = ranked.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(requirement.RequirementId))
            {
                var bestText = bestOverall.Best > double.NegativeInfinity
                    ? $"best={bestOverall.Best:F3} module={bestOverall.ModuleId} via={bestOverall.BestCap ?? "<unknown>"} threshold={threshold:F2}"
                    : $"best=<none> threshold={threshold:F2}";
                bestMatchByRequirementId[requirement.RequirementId] = bestText;
            }

            var candidateModules = moduleResults
                .Where(r => r.Best >= threshold && !string.IsNullOrWhiteSpace(r.ModuleId))
                .Select(r => r.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var m in candidateModules)
            {
                expectedOfferResponders.Add(m);
            }

            if (candidateModules.Count == 0)
            {
                var top = ranked.Take(5)
                    .Select(r => $"{r.ModuleId}:{(r.Best > double.NegativeInfinity ? r.Best.ToString("F3") : "n/a")}{(string.IsNullOrWhiteSpace(r.BestCap) ? "" : $" via {r.BestCap}")}")
                    .ToList();

                Logger.LogWarning(
                    "DispatchCapabilityRequests: no candidate modules passed similarity threshold for capability {Capability} (threshold={Threshold:F2}). TopMatches=[{Top}]",
                    requirement.Capability,
                    threshold,
                    string.Join("; ", top));

                if (logSimilarityMatrix)
                {
                    foreach (var kvp in similarityMatrix.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var line = string.Join(", ", kvp.Value
                            .OrderByDescending(v => v.Score ?? double.NegativeInfinity)
                            .Select(v => $"{v.Capability}={(v.Score.HasValue ? v.Score.Value.ToString("F3") : "n/a")}"));
                        Logger.LogInformation(
                            "SimilarityMatrix: requirement {Req} vs module {Module}: {Line}",
                            requirement.Capability,
                            kvp.Key,
                            line);
                    }
                }
                continue;
            }

            Logger.LogInformation(
                "DispatchCapabilityRequests: similarity kept {Kept}/{Total} modules for capability {Capability} (threshold={Threshold:F2})",
                candidateModules.Count,
                ranked.Count,
                requirement.Capability,
                threshold);

            // send CfP individually to candidate modules
            foreach (var tgtModule in candidateModules.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var builder = new I40MessageBuilder()
                    .From(Context.AgentId, Context.AgentRole)
                    .To(tgtModule, "ModuleHolon")
                    .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
                    .WithConversationId(ctx.ConversationId)
                    .AddElement(CreateStringProperty("Capability", requirement.Capability))
                    .AddElement(CreateStringProperty("RequirementId", requirement.RequirementId));

                if (!string.IsNullOrWhiteSpace(ctx.ProductId))
                {
                    builder.AddElement(CreateStringProperty("ProductId", ctx.ProductId));
                }

                if (requirement.CapabilityContainer != null)
                {
                    builder.AddElement(requirement.CapabilityContainer);
                }

                // attach cached description if available
                if (state.TryGetCapabilityDescription(requirement.Capability, out var descForReq) && !string.IsNullOrWhiteSpace(descForReq))
                {
                    builder.AddElement(CreateStringProperty("CapabilityDescription", descForReq));
                }

                var cfp = builder.Build();
                if (!cfpsByTarget.TryGetValue(tgtModule, out var list))
                {
                    list = new List<I40Message>();
                    cfpsByTarget[tgtModule] = list;
                }
                list.Add(cfp);

                await client.PublishAsync(cfp, topic);
            }
        }

        // Make CollectCapabilityOffer robust: only wait for modules we actually addressed.
        Context.Set("ProcessChain.ExpectedOfferResponders", expectedOfferResponders.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());

        // Provide CfPs for potential re-issue when a target registers after initial dispatch.
        Context.Set("ProcessChain.CfPsByTarget", cfpsByTarget);
        Context.Set("ProcessChain.CfPTopic", topic);
        Context.Set("ProcessChain.CfPDispatchUtc", cfpDispatchUtc);

        var totalCfPs = cfpsByTarget.Values.Sum(v => v?.Count ?? 0);
        Logger.LogInformation("DispatchCapabilityRequests: published {Count} CfP messages to {Topic}", totalCfPs, topic);
        return NodeStatus.Success;
    }

    private Task EnsureResponseHandlerRegisteredAsync(MessagingClient client)
    {
        if (_responseHandlerRegistered)
        {
            return Task.CompletedTask;
        }

        _responseHandlerRegistered = true;
        client.OnMessageType("informConfirm", msg =>
        {
            try
            {
                if (msg == null) return;
                var conv = msg.Frame?.ConversationId;
                if (string.IsNullOrWhiteSpace(conv)) return;

                if (_pendingCreateDescription.TryGetValue(conv, out var tcs1))
                {
                    tcs1.TrySetResult(msg);
                    return;
                }
                if (_pendingCalcSimilarity.TryGetValue(conv, out var tcs2))
                {
                    tcs2.TrySetResult(msg);
                    return;
                }
            }
            catch
            {
                // ignore
            }
        });

        return Task.CompletedTask;
    }

    private async Task EnsureCreateDescriptionResponseListenerAsync(MessagingClient client, string ns, string similarityAgentId)
    {
        if (!_subscribedCreateDescriptionResponse)
        {
            // SimilarityAnalysisAgent currently answers on its own request topics.
            // Subscribe to both (legacy receiver-topic and request-topic) to be robust.
            var responseTopicOnReceiver = $"/{ns}/{Context.AgentId}/CreateDescription";
            var responseTopicOnRequester = $"/{ns}/{similarityAgentId}/CreateDescription";
            await client.SubscribeAsync(responseTopicOnReceiver).ConfigureAwait(false);
            await client.SubscribeAsync(responseTopicOnRequester).ConfigureAwait(false);
            _subscribedCreateDescriptionResponse = true;
        }
    }

    private async Task<string?> RequestCapabilityDescriptionAsync(MessagingClient client, string ns, string similarityAgentId, string capability, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(capability)) return null;

        var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
        if (state.TryGetCapabilityDescription(capability, out var cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var convId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<I40Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCreateDescription[convId] = tcs;

        try
        {
            var builder = new I40MessageBuilder()
                .From(Context.AgentId, Context.AgentRole)
                .To(similarityAgentId, "AIAgent")
                .WithType("createDescription")
                .WithConversationId(convId)
                .AddElement(CreateStringProperty("Capability_0", capability.Trim()));

            var requestTopic = $"/{ns}/{similarityAgentId}/CreateDescription";
            await client.PublishAsync(builder.Build(), requestTopic).ConfigureAwait(false);

            var response = await WaitForResponseAsync(tcs, timeoutMs).ConfigureAwait(false);
            if (response == null) return null;

            if (response.InteractionElements != null)
            {
                foreach (var el in response.InteractionElements)
                {
                    if (el is Property p && string.Equals(p.IdShort, "Description_Result", StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = p.Value?.Value;
                        var str = raw?.ToString();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            state.SetCapabilityDescription(capability, str);
                            return str;
                        }
                    }
                }
            }

            return null;
        }
        finally
        {
            _pendingCreateDescription.TryRemove(convId, out _);
        }
    }

    private static async Task<I40Message?> WaitForResponseAsync(TaskCompletionSource<I40Message> tcs, int timeoutMs)
    {
        timeoutMs = Math.Max(50, timeoutMs);
        var delay = Task.Delay(timeoutMs);
        var completed = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
        if (completed == tcs.Task)
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        return null;
    }

    private async Task<double?> RequestCapabilitySimilarityAsync(MessagingClient client, string ns, string similarityAgentId, string descA, string descB, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(descA) || string.IsNullOrWhiteSpace(descB)) return null;
        var convId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<I40Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalcSimilarity[convId] = tcs;

        try
        {
            var builder = new I40MessageBuilder()
                .From(Context.AgentId, Context.AgentRole)
                .To(similarityAgentId, "AIAgent")
                .WithType("calcSimilarity")
                .WithConversationId(convId)
                .AddElement(CreateStringProperty("Description_1", descA))
                .AddElement(CreateStringProperty("Description_2", descB));

            var requestTopic = $"/{ns}/{similarityAgentId}/CalcSimilarity";
            await client.PublishAsync(builder.Build(), requestTopic).ConfigureAwait(false);

            var response = await WaitForResponseAsync(tcs, timeoutMs).ConfigureAwait(false);
            if (response == null) return null;
            if (response.InteractionElements != null)
            {
                foreach (var el in response.InteractionElements)
                {
                    if (el is Property p && string.Equals(p.IdShort, "CosineSimilarity", StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = p.Value?.Value;
                        if (raw != null && double.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                        {
                            return parsed;
                        }
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "RequestCapabilitySimilarityAsync: similarity request failed");
            return null;
        }
        finally
        {
            _pendingCalcSimilarity.TryRemove(convId, out _);
        }
    }

    private async Task<double?> GetOrRequestCapabilitySimilarityAsync(
        MessagingClient client,
        DispatchingState state,
        string ns,
        string similarityAgentId,
        string capabilityA,
        string capabilityB,
        string descA,
        string descB,
        int timeoutMs)
    {
        if (string.Equals(capabilityA, capabilityB, StringComparison.OrdinalIgnoreCase))
        {
            state.SetCapabilitySimilarity(capabilityA, capabilityB, 1.0);
            return 1.0;
        }

        if (state.TryGetCapabilitySimilarity(capabilityA, capabilityB, out var cached))
        {
            return cached;
        }

        var sim = await RequestCapabilitySimilarityAsync(client, ns, similarityAgentId, descA, descB, timeoutMs).ConfigureAwait(false);
        if (sim.HasValue)
        {
            state.SetCapabilitySimilarity(capabilityA, capabilityB, sim.Value);
        }
        return sim;
    }

    private async Task EnsureCalcSimilarityResponseListenerAsync(MessagingClient client, string ns, string similarityAgentId)
    {
            if (!_subscribedCalcSimilarityResponse)
            {
                // SimilarityAnalysisAgent may answer on multiple topics. Subscribe to legacy and pairwise topics.
                var responseTopicOnReceiver = $"/{ns}/{Context.AgentId}/CalcSimilarity";
                var responseTopicOnRequester = $"/{ns}/{similarityAgentId}/CalcSimilarity";
                var responseTopicOnReceiverPairwise = $"/{ns}/{Context.AgentId}/CalcPairwiseSimilarity";
                var responseTopicOnRequesterPairwise = $"/{ns}/{similarityAgentId}/CalcPairwiseSimilarity";
                await client.SubscribeAsync(responseTopicOnReceiver).ConfigureAwait(false);
                await client.SubscribeAsync(responseTopicOnRequester).ConfigureAwait(false);
                await client.SubscribeAsync(responseTopicOnReceiverPairwise).ConfigureAwait(false);
                await client.SubscribeAsync(responseTopicOnRequesterPairwise).ConfigureAwait(false);
                _subscribedCalcSimilarityResponse = true;
            }
    }

    private static Property<string> CreateStringProperty(string idShort, string value)
    {
        return new Property<string>(idShort)
        {
            Value = new PropertyValue<string>(value ?? string.Empty)
        };
    }

    private int GetConfigInt(string key, int defaultValue)
    {
        try
        {
            var v = Context.Get<object>(key);
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is string s && int.TryParse(s, out var parsed)) return parsed;
            // try JsonElement
            if (v is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out var n)) return n;
                if (je.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(je.GetString(), out var p)) return p;
            }
        }
        catch
        {
            // ignore
        }
        return defaultValue;
    }

    private static int ClampInt(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private double GetConfigDouble(string key, string? fallbackKey, double defaultValue)
    {
        var primary = TryGetConfigDouble(key);
        if (primary.HasValue) return primary.Value;
        if (!string.IsNullOrWhiteSpace(fallbackKey))
        {
            var secondary = TryGetConfigDouble(fallbackKey);
            if (secondary.HasValue) return secondary.Value;
        }
        return defaultValue;
    }

    private double? TryGetConfigDouble(string key)
    {
        try
        {
            var v = Context.Get<object>(key);
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is decimal m) return (double)m;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (v is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetDouble(out var n)) return n;
                if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var str = je.GetString();
                    if (double.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
