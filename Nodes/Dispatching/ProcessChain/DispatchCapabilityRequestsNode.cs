using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using MAS_BT.Tools;
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
    private SemaphoreSlim? _createDescriptionLimiter;
    private SemaphoreSlim? _calcSimilarityLimiter;
    private int _createDescriptionLimiterSize;
    private int _calcSimilarityLimiterSize;
    private readonly object _limiterLock = new();
    private bool _similarityTemporarilyDisabled = false;
    private DateTime _similarityRetryUtc = DateTime.MinValue;
    private readonly TimeSpan _similarityDisableWindow = TimeSpan.FromSeconds(60);

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

        var ns = Context.Get<string>("config.Namespace");
        if (string.IsNullOrWhiteSpace(ns))
        {
            Logger.LogError("DispatchCapabilityRequests: missing config.Namespace");
            return NodeStatus.Failure;
        }

        var topic = TopicHelper.BuildNamespaceTopic(Context, "Offer");
        var requestMode = GetRequestMode();
        var subtype = string.Equals(requestMode, "ManufacturingSequence", StringComparison.OrdinalIgnoreCase)
            ? I40MessageTypeSubtypes.ManufacturingSequence
            : I40MessageTypeSubtypes.ProcessChain;
        var createDescriptionConcurrency = ClampInt(GetConfigInt("config.DispatchingAgent.CreateDescriptionConcurrency", defaultValue: 3), 1, 20);
        var calcSimilarityConcurrency = ClampInt(GetConfigInt("config.DispatchingAgent.CalcSimilarityConcurrency", defaultValue: 3), 1, 20);
        EnsureLimitersInitialized(createDescriptionConcurrency, calcSimilarityConcurrency);

        // Cache emitted CfPs so the collector can re-issue them when a target module registers late.
        // MQTT does not deliver messages that were published before a subscriber connected.
        var cfpsByTarget = new Dictionary<string, List<I40Message>>(StringComparer.OrdinalIgnoreCase);
        var cfpDispatchUtc = DateTime.UtcNow;

        var similarityAgentId = Context.Get<string>("config.DispatchingAgent.SimilarityAgentId");
        if (string.IsNullOrWhiteSpace(similarityAgentId))
        {
            Logger.LogError("DispatchCapabilityRequests: missing config.DispatchingAgent.SimilarityAgentId");
            return NodeStatus.Failure;
        }

        var useSimilarityFiltering = GetConfigBool("config.DispatchingAgent.UseSimilarityFiltering", defaultValue: true);

        // Do not clamp 30s configured timeouts down to 5s; LLM-backed description generation often needs >5s.
        var descriptionTimeoutMs = ClampInt(GetConfigInt("config.DispatchingAgent.DescriptionTimeoutMs", defaultValue: 30000), 500, 300000);
        var similarityTimeoutMs = ClampInt(GetConfigInt("config.DispatchingAgent.SimilarityTimeoutMs", defaultValue: 30000), 500, 300000);
        var threshold = GetConfigDouble("config.DispatchingAgent.CapabilitySimilarityThreshold", fallbackKey: "config.SimilarityAnalysis.MinSimilarityThreshold", defaultValue: 0.75);

        var logSimilarityMatrix = GetConfigInt("config.DispatchingAgent.LogSimilarityMatrix", defaultValue: 0) != 0;

        // Diagnostics: remember best match per requirement so we can print what failed later in BuildProcessChainResponse.
        var bestMatchByRequirementId = Context.Get<Dictionary<string, string>>("ProcessChain.SimilarityBestMatch")
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Context.Set("ProcessChain.SimilarityBestMatch", bestMatchByRequirementId);

        // Ensure we have response handlers/subscriptions for CreateDescription/CalcSimilarity
        await EnsureResponseHandlerRegisteredAsync(client).ConfigureAwait(false);
        await EnsureCreateDescriptionResponseListenerAsync(client, similarityAgentId).ConfigureAwait(false);
        await EnsureCalcSimilarityResponseListenerAsync(client, similarityAgentId).ConfigureAwait(false);

        // Ensure descriptions for all registered + requested capabilities are present (cache them)
        var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
        // Store once; state is mutated in-place and uses concurrent dictionaries internally.
        Context.Set("DispatchingState", state);

        // Similarity agent must be available.
        var similarityAgentAvailable = state.Modules.Any(m =>
            !string.IsNullOrWhiteSpace(m.ModuleId)
            && string.Equals(m.ModuleId, similarityAgentId, StringComparison.OrdinalIgnoreCase));

        var canUseSimilarity = useSimilarityFiltering && similarityAgentAvailable && IsSimilarityEnabled();
        if (!useSimilarityFiltering)
        {
            Logger.LogInformation("DispatchCapabilityRequests: similarity filtering disabled by config; falling back to direct matching");
        }
        else if (!similarityAgentAvailable)
        {
            Logger.LogWarning("DispatchCapabilityRequests: Similarity agent not registered: {SimilarityAgentId}. Falling back to direct matching.", similarityAgentId);
        }
        else if (!IsSimilarityEnabled())
        {
            Logger.LogWarning("DispatchCapabilityRequests: Similarity temporarily disabled. Falling back to direct matching.");
        }

        var capabilitiesToDescribe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cap in state.Modules.SelectMany(m => m.Capabilities))
        {
            if (!string.IsNullOrWhiteSpace(cap)) capabilitiesToDescribe.Add(cap);
        }
        foreach (var req in ctx.Requirements)
        {
            if (!string.IsNullOrWhiteSpace(req.Capability)) capabilitiesToDescribe.Add(req.Capability);
        }

        if (canUseSimilarity)
        {
            var descTasks = new List<Task>();
            foreach (var cap in capabilitiesToDescribe)
            {
                if (!state.TryGetCapabilityDescription(cap, out var _))
                {
                    descTasks.Add(RunWithLimiterAsync(_createDescriptionLimiter!, async () =>
                    {
                        var description = await RequestCapabilityDescriptionAsync(client, similarityAgentId, cap, descriptionTimeoutMs).ConfigureAwait(false);
                        if (description == null)
                        {
                            DisableSimilarityTemporarily($"no description response for capability '{cap}'");
                        }
                    }));
                }
            }
            if (descTasks.Count > 0)
            {
                try { await Task.WhenAll(descTasks).ConfigureAwait(false); }
                catch { /* individual failures are logged inside RequestCapabilityDescriptionAsync */ }
            }

            // If similarity was disabled mid-flight, do NOT abort the whole negotiation; fall back.
            if (!IsSimilarityEnabled())
            {
                Logger.LogWarning("DispatchCapabilityRequests: Similarity disabled after description prefetch. Falling back to direct matching.");
                canUseSimilarity = false;
            }
        }

        // For each requirement, compute candidate modules by similarity and send CfP only to those modules
        var expectedOfferResponders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in ctx.Requirements)
        {
            var allModules = state.Modules
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.ModuleId))
                .Where(m => !string.Equals(m.ModuleId, similarityAgentId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var descReq = state.TryGetCapabilityDescription(requirement.Capability, out var descA) ? descA : null;

            List<string> candidateModules;
            List<(string ModuleId, double Best, string? BestCap)> moduleResults;
            Dictionary<string, List<(string Capability, double? Score)>>? similarityMatrix = null;

            if (canUseSimilarity)
            {
                if (string.IsNullOrWhiteSpace(descReq))
                {
                    Logger.LogWarning(
                        "DispatchCapabilityRequests: missing capability description for requirement {Capability}; falling back to direct matching",
                        requirement.Capability);
                    canUseSimilarity = false;
                }
            }

            if (canUseSimilarity)
            {
                // Optional detailed matrix logging; keep per module a list of cap->score.
                similarityMatrix = new Dictionary<string, List<(string Capability, double? Score)>>(StringComparer.OrdinalIgnoreCase);

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
                        simTasks.Add(RunWithLimiterAsync(_calcSimilarityLimiter!, async () =>
                        {
                            var sim = await GetOrRequestCapabilitySimilarityAsync(
                                    state,
                                    client,
                                    ns,
                                    similarityAgentId,
                                    requirement.Capability,
                                    capLocal,
                                    descReq!,
                                    descMod!,
                                    similarityTimeoutMs)
                                .ConfigureAwait(false);
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

                var results = await Task.WhenAll(moduleTasks).ConfigureAwait(false);
                moduleResults = results.Select(r => (r.ModuleId, r.Best, r.BestCap)).ToList();

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

                candidateModules = moduleResults
                    .Where(r => r.Best >= threshold && !string.IsNullOrWhiteSpace(r.ModuleId))
                    .Select(r => r.ModuleId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

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

                    if (logSimilarityMatrix && similarityMatrix != null)
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
            }
            else
            {
                // Fallback: dispatch by direct capability name match.
                moduleResults = allModules
                    .Select(m => (ModuleId: m.ModuleId, Best: m.Capabilities.Any(c => string.Equals(c, requirement.Capability, StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.0, BestCap: (string?)requirement.Capability))
                    .ToList();

                candidateModules = allModules
                    .Where(m => m.Capabilities.Any(c => string.Equals(c, requirement.Capability, StringComparison.OrdinalIgnoreCase)))
                    .Select(m => m.ModuleId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (candidateModules.Count == 0)
                {
                    // As a last resort, broadcast to all modules with any capabilities so they can self-filter.
                    candidateModules = allModules
                        .Where(m => m.Capabilities.Count > 0)
                        .Select(m => m.ModuleId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    Logger.LogWarning(
                        "DispatchCapabilityRequests: no exact-match modules for capability {Capability}; broadcasting CfP to {Count} modules",
                        requirement.Capability,
                        candidateModules.Count);
                }
            }

            foreach (var m in candidateModules)
            {
                expectedOfferResponders.Add(m);
            }

            // send CfP individually to candidate modules
            foreach (var tgtModule in candidateModules.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var cfpMessage = new CapabilityCallForProposalMessage(
                    Context.AgentId,
                    Context.AgentRole,
                    tgtModule,
                    "ModuleHolon",
                    ctx.ConversationId,
                    subtype,
                    requirement.Capability,
                    requirement.RequirementId,
                    ctx.ProductId,
                    capabilityDescription: descReq,
                    capabilityContainer: requirement.CapabilityContainer);

                var cfp = cfpMessage.ToI40Message();
                if (!cfpsByTarget.TryGetValue(tgtModule, out var list))
                {
                    list = new List<I40Message>();
                    cfpsByTarget[tgtModule] = list;
                }
                list.Add(cfp);

                var publishedAt = DateTimeOffset.UtcNow;
                await cfpMessage.PublishAsync(client, topic).ConfigureAwait(false);
                Logger.LogInformation(
                    "DispatchCapabilityRequests: CfP published at {Timestamp:o} to {Module} Capability={Capability} Requirement={RequirementId} Topic={Topic}",
                    publishedAt,
                    tgtModule,
                    requirement.Capability,
                    requirement.RequirementId,
                    topic);
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

    private bool GetConfigBool(string key, bool defaultValue)
    {
        try
        {
            var v = Context.Get<object>(key);
            if (JsonFacade.TryToBool(v, out var parsed)) return parsed;
        }
        catch
        {
            // ignore
        }

        return defaultValue;
    }

    private string GetRequestMode()
    {
        var mode = Context.Get<string>("ProcessChain.RequestType");
        return string.IsNullOrWhiteSpace(mode) ? "ProcessChain" : mode;
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

    private async Task EnsureCreateDescriptionResponseListenerAsync(MessagingClient client, string similarityAgentId)
    {
        if (!_subscribedCreateDescriptionResponse)
        {
            // SimilarityAnalysisAgent BT subscribes to /{ns}/{AgentId}/CreateDescription (no role segment).
            var responseTopicOnRequester = TopicHelper.BuildAgentTopic(Context, similarityAgentId, "CreateDescription");
            await client.SubscribeAsync(responseTopicOnRequester).ConfigureAwait(false);
            _subscribedCreateDescriptionResponse = true;
        }
    }

    private async Task<string?> RequestCapabilityDescriptionAsync(MessagingClient client, string similarityAgentId, string capability, int timeoutMs)
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
            var request = new SimilarityCreateDescriptionRequestMessage(
                Context.AgentId,
                Context.AgentRole,
                similarityAgentId,
                "AIAgent",
                convId,
                capability);

            // SimilarityAnalysisAgent listens on /{ns}/{AgentId}/CreateDescription (no role segment).
            var requestTopic = TopicHelper.BuildAgentTopic(Context, similarityAgentId, "CreateDescription");
            Logger.LogInformation(
                "DispatchCapabilityRequests: publishing createDescription request to Topic={Topic} Receiver={ReceiverId} ReceiverRole={ReceiverRole} Capability={Capability}",
                requestTopic,
                similarityAgentId,
                "AIAgent",
                capability);
            await request.PublishAsync(client, requestTopic).ConfigureAwait(false);

            var response = await WaitForResponseAsync(tcs, timeoutMs).ConfigureAwait(false);
            if (response == null)
            {
                DisableSimilarityTemporarily($"description timeout for capability '{capability}'");
                return null;
            }

            if (response.InteractionElements != null)
            {
                foreach (var el in response.InteractionElements)
                {
                    if (el is Property p && string.Equals(p.IdShort, "Description_Result", StringComparison.OrdinalIgnoreCase))
                    {
                        var str = (p as IProperty).GetText();
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

    private async Task<double?> RequestCapabilitySimilarityAsync(
        MessagingClient client,
        string similarityAgentId,
        string capabilityA,
        string capabilityB,
        string descA,
        string descB,
        int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(descA) || string.IsNullOrWhiteSpace(descB)) return null;
        var convId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<I40Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalcSimilarity[convId] = tcs;

        try
        {
            var request = new SimilarityCalcSimilarityRequestMessage(
                Context.AgentId,
                Context.AgentRole,
                similarityAgentId,
                "AIAgent",
                convId,
                descA,
                descB);

            // SimilarityAnalysisAgent listens on /{ns}/{AgentId}/CalcSimilarity (no role segment).
            var requestTopic = TopicHelper.BuildAgentTopic(Context, similarityAgentId, "CalcSimilarity");
            Logger.LogInformation(
                "DispatchCapabilityRequests: publishing calcSimilarity request to Topic={Topic} Receiver={ReceiverId} ReceiverRole={ReceiverRole} CapabilityA={CapabilityA} CapabilityB={CapabilityB}",
                requestTopic,
                similarityAgentId,
                "AIAgent",
                capabilityA,
                capabilityB);
            await request.PublishAsync(client, requestTopic).ConfigureAwait(false);

            var response = await WaitForResponseAsync(tcs, timeoutMs).ConfigureAwait(false);
            if (response == null)
            {
                DisableSimilarityTemporarily($"similarity timeout ({capabilityA} vs {capabilityB})");
                return null;
            }
            if (response.InteractionElements != null)
            {
                foreach (var el in response.InteractionElements)
                {
                    if (el is Property p && string.Equals(p.IdShort, "CosineSimilarity", StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = AasValueUnwrap.Unwrap(p.Value);
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
        DispatchingState state,
        MessagingClient client,
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

        var sim = await RequestCapabilitySimilarityAsync(
            client,
            similarityAgentId,
            capabilityA,
            capabilityB,
            descA,
            descB,
            timeoutMs).ConfigureAwait(false);
        if (sim.HasValue)
        {
            state.SetCapabilitySimilarity(capabilityA, capabilityB, sim.Value);
        }
        return sim;
    }

    private async Task EnsureCalcSimilarityResponseListenerAsync(MessagingClient client, string similarityAgentId)
    {
            if (!_subscribedCalcSimilarityResponse)
            {
                var responseTopicOnRequesterPairwise = TopicHelper.BuildAgentTopic(Context, similarityAgentId, "CalcPairwiseSimilarity");
                await client.SubscribeAsync(responseTopicOnRequesterPairwise).ConfigureAwait(false);
                _subscribedCalcSimilarityResponse = true;
            }
    }

    private int GetConfigInt(string key, int defaultValue)
    {
        try
        {
            var v = Context.Get<object>(key);
            if (JsonFacade.TryToInt(v, out var parsed)) return parsed;
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
            if (JsonFacade.TryToDouble(v, out var parsed)) return parsed;
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private bool IsSimilarityEnabled()
    {
        if (!_similarityTemporarilyDisabled)
        {
            return true;
        }

        if (DateTime.UtcNow >= _similarityRetryUtc)
        {
            _similarityTemporarilyDisabled = false;
            Logger.LogInformation("DispatchCapabilityRequests: re-enabled similarity matching after cooldown");
            return true;
        }

        return false;
    }

    private void DisableSimilarityTemporarily(string reason)
    {
        if (_similarityTemporarilyDisabled && DateTime.UtcNow < _similarityRetryUtc)
        {
            return;
        }

        _similarityTemporarilyDisabled = true;
        _similarityRetryUtc = DateTime.UtcNow.Add(_similarityDisableWindow);
        Logger.LogWarning(
            "DispatchCapabilityRequests: disabling similarity matching for {WindowSeconds}s due to {Reason}",
            (int)_similarityDisableWindow.TotalSeconds,
            reason);
    }

    private void EnsureLimitersInitialized(int createDescriptionConcurrency, int calcSimilarityConcurrency)
    {
        lock (_limiterLock)
        {
            if (_createDescriptionLimiter == null || _createDescriptionLimiterSize != createDescriptionConcurrency)
            {
                _createDescriptionLimiter?.Dispose();
                _createDescriptionLimiter = new SemaphoreSlim(createDescriptionConcurrency, createDescriptionConcurrency);
                _createDescriptionLimiterSize = createDescriptionConcurrency;
            }

            if (_calcSimilarityLimiter == null || _calcSimilarityLimiterSize != calcSimilarityConcurrency)
            {
                _calcSimilarityLimiter?.Dispose();
                _calcSimilarityLimiter = new SemaphoreSlim(calcSimilarityConcurrency, calcSimilarityConcurrency);
                _calcSimilarityLimiterSize = calcSimilarityConcurrency;
            }
        }
    }

    private Task RunWithLimiterAsync(SemaphoreSlim limiter, Func<Task> action)
    {
        return RunWithLimiterAsync(limiter, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        });
    }

    private async Task<T> RunWithLimiterAsync<T>(SemaphoreSlim limiter, Func<Task<T>> action)
    {
        if (limiter == null) throw new ArgumentNullException(nameof(limiter));
        await limiter.WaitAsync().ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            limiter.Release();
        }
    }
}
