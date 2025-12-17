using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Messages;
using AasSharpClient.Models.Helpers;
using MAS_BT.Core;
using MAS_BT.Services.Graph;
using MAS_BT.Tools;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Dispatching
{
    public class HandleProcessChainRequestNode : BTNode
    {
        public HandleProcessChainRequestNode() : base("HandleProcessChainRequest") { }

        private readonly ConcurrentDictionary<string, TaskCompletionSource<I40Message>> _pendingCreateDescription = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<I40Message>> _pendingCalcSimilarity = new();

        private bool _responseHandlersRegistered;
        private bool _subscribedCreateDescriptionResponse;
        private bool _subscribedCalcSimilarityResponse;
        // Limit the number of in-flight requests to the similarity agent (configurable).
        private System.Threading.SemaphoreSlim? _createDescriptionLimiter;
        private System.Threading.SemaphoreSlim? _calcSimilarityLimiter;
        private int _createDescriptionLimiterSize;
        private int _calcSimilarityLimiterSize;

        public override async Task<NodeStatus> Execute()
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("HandleProcessChainRequest: MessagingClient unavailable");
                return NodeStatus.Failure;
            }

            var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
            Context.Set("DispatchingState", state);

            var incoming = Context.Get<I40Message>("LastReceivedMessage");
            if (incoming == null)
            {
                Logger.LogWarning("HandleProcessChainRequest: no incoming message");
                return NodeStatus.Failure;
            }

            // Persist original request for downstream consumers / debugging.
            Context.Set("ProcessChain.RequestMessage", incoming);

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var conversationId = incoming.Frame?.ConversationId ?? Guid.NewGuid().ToString();
            var requesterId = incoming.Frame?.Sender?.Identification?.Id ?? "Unknown";
            Context.Set("ConversationId", conversationId);

            var requestedCaps = ExtractRequestedCapabilities(incoming).ToList();
            if (requestedCaps.Count == 0)
            {
                requestedCaps = state.Modules.SelectMany(m => m.Capabilities).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (requestedCaps.Count == 0)
            {
                requestedCaps.Add("GenericCapability");
            }

            var steps = new List<ProcessChainStep>();
            var hasCandidates = true;

            var useSimilarityFiltering = GetConfigBool("config.DispatchingAgent.UseSimilarityFiltering", defaultValue: false);
            var threshold = GetConfigDouble(
                "config.DispatchingAgent.CapabilitySimilarityThreshold",
                fallbackKey: "config.SimilarityAnalysis.MinSimilarityThreshold",
                defaultValue: 0.75);

            var similarityAgentId = Context.Get<string>("config.DispatchingAgent.SimilarityAgentId")
                                   ?? $"SimilarityAnalysisAgent_{ns}";

            // Concurrency (in-flight) for AI requests. Defaults intentionally >1 to reduce perceived queue latency.
            var createDescriptionConcurrency = ClampInt(GetConfigInt("config.DispatchingAgent.CreateDescriptionConcurrency", defaultValue: 3), 1, 20);
            var calcSimilarityConcurrency = ClampInt(GetConfigInt("config.DispatchingAgent.CalcSimilarityConcurrency", defaultValue: 3), 1, 20);
            EnsureLimitersInitialized(createDescriptionConcurrency, calcSimilarityConcurrency);

            // Optional gating using a graph query: only used for strict capability matching mode.
            if (!useSimilarityFiltering)
            {
                var registeredAgents = state.Modules
                    .Select(m => m.ModuleId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var graphQuery = Context.Get<IGraphCapabilityQuery>("GraphCapabilityQuery") ?? new DummyNeo4jCapabilityQuery();
                var graphOk = await graphQuery.AnyRegisteredAgentImplementsAllAsync(ns, requestedCaps, registeredAgents);
                if (!graphOk)
                {
                    hasCandidates = false;
                    Context.Set("ProcessChain.RefusalReason", "No registered agent implements the required capabilities");
                }
            }

            foreach (var cap in requestedCaps)
            {
                var candidates = hasCandidates
                    ? (useSimilarityFiltering ? state.AllModuleIds().ToList() : state.FindModulesForCapability(cap).ToList())
                    : new List<string>();

                if (hasCandidates && useSimilarityFiltering)
                {
                    candidates = await FilterCandidatesBySimilarityAsync(
                        client,
                        state,
                        ns,
                        similarityAgentId,
                        requestedCapability: cap,
                        candidateModuleIds: candidates,
                        threshold: threshold).ConfigureAwait(false);
                }

                if (hasCandidates && candidates.Count == 0)
                {
                    hasCandidates = false;
                    Context.Set("ProcessChain.RefusalReason", $"No candidates for capability '{cap}'");
                }
                steps.Add(new ProcessChainStep
                {
                    Capability = cap,
                    CandidateModules = candidates
                });
            }

            var responseDto = new ProcessChainProposal
            {
                ProcessChainId = conversationId,
                Steps = steps
            };

            var messageType = hasCandidates ? I40MessageTypes.PROPOSAL : I40MessageTypes.REFUSE_PROPOSAL;
            var responseTopic = $"/{ns}/ProcessChain";

            try
            {
                var builder = new I40MessageBuilder()
                    .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
                    .To(requesterId, null)
                    .WithType(messageType)
                    .WithConversationId(conversationId);

                var serialized = JsonFacade.Serialize(responseDto);
                var payload = new Property<string>("ProcessChain")
                {
                    Value = new PropertyValue<string>(serialized)
                };
                builder.AddElement(payload);

                if (!hasCandidates)
                {
                    var reason = Context.Get<string>("ProcessChain.RefusalReason") ?? "No capability match";
                    builder.AddElement(new Property<string>("Reason") { Value = new PropertyValue<string>(reason) });
                }

                var response = builder.Build();
                await client.PublishAsync(response, responseTopic);
                Logger.LogInformation("HandleProcessChainRequest: sent {Type} with {StepCount} steps to {Topic}", messageType, steps.Count, responseTopic);
                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "HandleProcessChainRequest: failed to publish response");
                return NodeStatus.Failure;
            }
        }

        private async Task<List<string>> FilterCandidatesBySimilarityAsync(
            MessagingClient client,
            DispatchingState state,
            string ns,
            string similarityAgentId,
            string requestedCapability,
            List<string> candidateModuleIds,
            double threshold)
        {
            if (candidateModuleIds == null || candidateModuleIds.Count == 0)
            {
                return new List<string>();
            }

            // Make sure response listeners are ready before sending any requests.
            await EnsureSimilarityResponseListenersAsync(client, ns, similarityAgentId).ConfigureAwait(false);

            var filtered = new List<string>();

            foreach (var moduleId in candidateModuleIds)
            {
                var module = state.Modules.FirstOrDefault(m => string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
                if (module == null || module.Capabilities.Count == 0)
                {
                    continue;
                }

                var best = double.NegativeInfinity;
                foreach (var moduleCapability in module.Capabilities)
                {
                    if (string.IsNullOrWhiteSpace(moduleCapability))
                    {
                        continue;
                    }

                    var similarity = await GetOrCalculateCapabilitySimilarityAsync(
                        client,
                        state,
                        ns,
                        similarityAgentId,
                        requestedCapability,
                        moduleCapability).ConfigureAwait(false);

                    if (similarity.HasValue)
                    {
                        best = Math.Max(best, similarity.Value);
                    }
                }

                if (best >= threshold)
                {
                    filtered.Add(moduleId);
                }
            }

            Logger.LogInformation(
                "HandleProcessChainRequest: Similarity filtering kept {Kept}/{Total} candidates for '{Capability}' (threshold={Threshold:F2})",
                filtered.Count,
                candidateModuleIds.Count,
                requestedCapability,
                threshold);

            return filtered;
        }

        private async Task<double?> GetOrCalculateCapabilitySimilarityAsync(
            MessagingClient client,
            DispatchingState state,
            string ns,
            string similarityAgentId,
            string capabilityA,
            string capabilityB)
        {
            if (string.IsNullOrWhiteSpace(capabilityA) || string.IsNullOrWhiteSpace(capabilityB))
            {
                return null;
            }

            // Fast-path for identical capability names.
            if (string.Equals(capabilityA, capabilityB, StringComparison.OrdinalIgnoreCase))
            {
                state.SetCapabilitySimilarity(capabilityA, capabilityB, 1.0);
                return 1.0;
            }

            if (state.TryGetCapabilitySimilarity(capabilityA, capabilityB, out var cached))
            {
                return cached;
            }

            // If we cannot reach the similarity agent in time, fall back to a deterministic value (0 for non-equal).
            var descriptionA = await GetOrCreateCapabilityDescriptionAsync(client, state, ns, similarityAgentId, capabilityA).ConfigureAwait(false);
            var descriptionB = await GetOrCreateCapabilityDescriptionAsync(client, state, ns, similarityAgentId, capabilityB).ConfigureAwait(false);

            if (descriptionA == null || descriptionB == null)
            {
                state.SetCapabilitySimilarity(capabilityA, capabilityB, 0.0);
                return 0.0;
            }

            var timeoutMs = GetConfigInt("config.DispatchingAgent.SimilarityTimeoutMs", defaultValue: 30000);

            var convId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<I40Message>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCalcSimilarity[convId] = tcs;

            var requestTopic = $"/{ns}/{similarityAgentId}/CalcSimilarity";

            try
            {
                var builder = new I40MessageBuilder()
                    .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
                    .To(similarityAgentId, "AIAgent")
                    .WithType("calcSimilarity")
                    .WithConversationId(convId);

                builder.AddElement(new Property<string>("Description_1") { Value = new PropertyValue<string>(descriptionA) });
                builder.AddElement(new Property<string>("Description_2") { Value = new PropertyValue<string>(descriptionB) });

                var limiter = _calcSimilarityLimiter ?? new System.Threading.SemaphoreSlim(1, 1);
                // Throttle only the publish burst; do not hold the limiter while waiting for the response.
                await limiter.WaitAsync().ConfigureAwait(false);
                try
                {
                    await client.PublishAsync(builder.Build(), requestTopic).ConfigureAwait(false);
                }
                finally
                {
                    limiter.Release();
                }

                var response = await WaitForResponseAsync(tcs, timeoutMs).ConfigureAwait(false);
                if (response == null)
                {
                    state.SetCapabilitySimilarity(capabilityA, capabilityB, 0.0);
                    return 0.0;
                }

                if (TryGetDoubleProperty(response, "CosineSimilarity", out var sim))
                {
                    state.SetCapabilitySimilarity(capabilityA, capabilityB, sim);
                    return sim;
                }

                state.SetCapabilitySimilarity(capabilityA, capabilityB, 0.0);
                return 0.0;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "HandleProcessChainRequest: Similarity request failed, falling back to 0.0");
                state.SetCapabilitySimilarity(capabilityA, capabilityB, 0.0);
                return 0.0;
            }
            finally
            {
                _pendingCalcSimilarity.TryRemove(convId, out _);
            }
        }

        private async Task<string?> GetOrCreateCapabilityDescriptionAsync(
            MessagingClient client,
            DispatchingState state,
            string ns,
            string similarityAgentId,
            string capability)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                return null;
            }

            if (state.TryGetCapabilityDescription(capability, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            var timeoutMs = GetConfigInt("config.DispatchingAgent.DescriptionTimeoutMs", defaultValue: 30000);

            var convId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<I40Message>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCreateDescription[convId] = tcs;

            var requestTopic = $"/{ns}/{similarityAgentId}/CreateDescription";

            try
            {
                var builder = new I40MessageBuilder()
                    .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
                    .To(similarityAgentId, "AIAgent")
                    .WithType("createDescription")
                    .WithConversationId(convId);

                builder.AddElement(new Property<string>("Capability_0") { Value = new PropertyValue<string>(capability.Trim()) });

                var limiter = _createDescriptionLimiter ?? new System.Threading.SemaphoreSlim(1, 1);
                // Throttle only the publish burst; do not hold the limiter while waiting for the response.
                await limiter.WaitAsync().ConfigureAwait(false);
                try
                {
                    await client.PublishAsync(builder.Build(), requestTopic).ConfigureAwait(false);
                }
                finally
                {
                    limiter.Release();
                }

                var response = await WaitForResponseAsync(tcs, timeoutMs).ConfigureAwait(false);
                if (response == null)
                {
                    return null;
                }

                if (TryGetStringProperty(response, "Description_Result", out var description))
                {
                    state.SetCapabilityDescription(capability, description);
                    return description;
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "HandleProcessChainRequest: CreateDescription request failed");
                return null;
            }
            finally
            {
                _pendingCreateDescription.TryRemove(convId, out _);
            }
        }

        private async Task EnsureSimilarityResponseListenersAsync(MessagingClient client, string ns, string similarityAgentId)
        {
            if (!_responseHandlersRegistered)
            {
                _responseHandlersRegistered = true;
                client.OnMessageType("informConfirm", msg =>
                {
                    try
                    {
                        var safeMsg = msg;
                        if (safeMsg == null)
                        {
                            return;
                        }
                        var conv = safeMsg.Frame?.ConversationId;
                        if (string.IsNullOrWhiteSpace(conv))
                        {
                            return;
                        }

                        if (_pendingCreateDescription.TryGetValue(conv, out var tcs1))
                        {
                            tcs1.TrySetResult(safeMsg);
                            return;
                        }

                        if (_pendingCalcSimilarity.TryGetValue(conv, out var tcs2))
                        {
                            tcs2.TrySetResult(safeMsg);
                            return;
                        }
                    }
                    catch
                    {
                        // ignore handler exceptions
                    }
                });
            }

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

            if (!_subscribedCalcSimilarityResponse)
            {
                // SimilarityAnalysisAgent currently answers on its own request topics.
                // Subscribe to both (legacy receiver-topic and request-topic) to be robust.
                var responseTopicOnReceiver = $"/{ns}/{Context.AgentId}/CalcSimilarity";
                var responseTopicOnRequester = $"/{ns}/{similarityAgentId}/CalcSimilarity";
                await client.SubscribeAsync(responseTopicOnReceiver).ConfigureAwait(false);
                await client.SubscribeAsync(responseTopicOnRequester).ConfigureAwait(false);
                _subscribedCalcSimilarityResponse = true;
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

        private void EnsureLimitersInitialized(int createDescriptionConcurrency, int calcSimilarityConcurrency)
        {
            if (_createDescriptionLimiter == null || _createDescriptionLimiterSize != createDescriptionConcurrency)
            {
                _createDescriptionLimiter = new System.Threading.SemaphoreSlim(createDescriptionConcurrency, createDescriptionConcurrency);
                _createDescriptionLimiterSize = createDescriptionConcurrency;
            }

            if (_calcSimilarityLimiter == null || _calcSimilarityLimiterSize != calcSimilarityConcurrency)
            {
                _calcSimilarityLimiter = new System.Threading.SemaphoreSlim(calcSimilarityConcurrency, calcSimilarityConcurrency);
                _calcSimilarityLimiterSize = calcSimilarityConcurrency;
            }
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
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

        private static bool TryGetStringProperty(I40Message message, string idShort, out string value)
        {
            value = string.Empty;
            if (message?.InteractionElements == null) return false;

            foreach (var element in message.InteractionElements)
            {
                if (element is Property p && string.Equals(p.IdShort, idShort, StringComparison.OrdinalIgnoreCase))
                {
                    var raw = AasValueUnwrap.Unwrap(p.Value);
                    if (raw is string s)
                    {
                        value = s;
                        return true;
                    }
                    if (raw != null)
                    {
                        value = raw.ToString() ?? string.Empty;
                        return !string.IsNullOrWhiteSpace(value);
                    }
                }
            }
            return false;
        }

        private static bool TryGetDoubleProperty(I40Message message, string idShort, out double value)
        {
            value = 0.0;
            if (message?.InteractionElements == null) return false;

            foreach (var element in message.InteractionElements)
            {
                if (element is Property p && string.Equals(p.IdShort, idShort, StringComparison.OrdinalIgnoreCase))
                {
                    var raw = AasValueUnwrap.Unwrap(p.Value);
                    if (raw is double d)
                    {
                        value = d;
                        return true;
                    }
                    if (raw is float f)
                    {
                        value = f;
                        return true;
                    }
                    if (raw is int i)
                    {
                        value = i;
                        return true;
                    }
                    if (raw is long l)
                    {
                        value = l;
                        return true;
                    }
                    if (raw is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        value = parsed;
                        return true;
                    }
                    if (raw != null && double.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed2))
                    {
                        value = parsed2;
                        return true;
                    }
                }
            }
            return false;
        }

        private IEnumerable<string> ExtractRequestedCapabilities(I40Message message)
        {
            if (message?.InteractionElements == null)
                yield break;

            foreach (var element in message.InteractionElements)
            {
                if (element is IProperty prop)
                {
                    var text = prop.GetText();
                    if (!string.IsNullOrWhiteSpace(text)) yield return text!;
                }
                else if (element is SubmodelElementCollection coll)
                {
                    foreach (var child in coll.Values?.OfType<IProperty>() ?? Enumerable.Empty<IProperty>())
                    {
                        var text = child.GetText();
                        if (!string.IsNullOrWhiteSpace(text)) yield return text!;
                    }
                }
            }
        }

        private static string? TryExtractString(object? value)
        {
            if (value is string s) return s;
            return value?.ToString();
        }

    }
}
