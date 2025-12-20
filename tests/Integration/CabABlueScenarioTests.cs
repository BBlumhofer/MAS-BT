using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AasSharpClient.Models.ManufacturingSequence;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Transport;
using MAS_BT.Core;
using MAS_BT.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MAS_BT.Serialization;
using Xunit;

namespace MAS_BT.Tests.Integration;

public class CabABlueScenarioTests
{
    private readonly MessageSerializer _serializer = new();

    [Fact]
    public async Task CabABlue_EndToEnd_FlowsThroughDispatcherPlanningAndTransport()
    {
        var envPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../tests/TestFiles/Cab_A_Blue-environment.json"));
        var productId = ExtractProductId(envPath) ?? $"product-{Guid.NewGuid():N}";
        var ns = $"test{Guid.NewGuid():N}";
        var conversationId = productId;

        InMemoryTransport.ResetAll();
        var sentLog = new ConcurrentBag<object>();

        // Create an in-memory MessagingClient for in-process agents
        async Task<MessagingClient> CreateClientAsync(string name)
        {
            var clientTransport = new InMemoryTransport();
            var client = new MessagingClient(clientTransport, $"{name}/logs");
            await client.ConnectAsync();
            return client;
        }

        // Helper: spawn a BehaviorTree agent in-process from a .bt.xml file
        Task StartAgentFromTreeAsync(string btRelativePath, string agentId, string agentRole, string defaultTopicPrefix, MessagingClient messagingClient)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
                    var btPath = Path.Combine(repoRoot, btRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));
                    var registry = new NodeRegistry(loggerFactory.CreateLogger<NodeRegistry>());
                    var deserializer = new XmlTreeDeserializer(registry, loggerFactory);

                    var context = new BTContext(loggerFactory.CreateLogger<BTContext>()) { AgentId = agentId, AgentRole = agentRole };
                    context.Set("config.Agent.AgentId", agentId);
                    context.Set("config.Agent.Role", agentRole);
                    context.Set("MessagingClient", messagingClient);
                    context.Set("config.MQTT.Broker", "inmemory");

                    var root = deserializer.Deserialize(btPath, context);
                    // Simple tick loop
                    while (true)
                    {
                        var status = await root.Execute();
                        if (status != NodeStatus.Running) break;
                        await Task.Delay(10);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Agent {agentId} failed: {ex}");
                }
            });
        }

        void LogEvent(string direction, string actor, string topic, I40Message msg)
        {
            object payload = null;
            try
            {
                if (msg != null)
                {
                    var json = _serializer.Serialize(msg);
                    payload = JsonSerializer.Deserialize<object>(json);
                }
            }
            catch
            {
                // fallback: keep original serialized string if parsing fails
                payload = _serializer.Serialize(msg);
            }

            sentLog.Add(new
            {
                direction,
                actor,
                topic,
                type = msg?.Frame?.Type,
                conversation = msg?.Frame?.ConversationId,
                sender = msg?.Frame?.Sender?.Identification?.Id,
                receiver = msg?.Frame?.Receiver?.Identification?.Id,
                payload
            });
        }

        var product = await CreateClientAsync("product");
        var dispatcher = await CreateClientAsync("dispatcher");
        var s1 = await CreateClientAsync("s1");
        var transportManager = await CreateClientAsync("transportManager");

        var planningModules = new[]
        {
            (moduleId: "P101", capability: "Drill"),
            (moduleId: "P100", capability: "Screw"),
            (moduleId: "P102", capability: "Assemble")
        };

        var planningClients = new Dictionary<string, MessagingClient>(StringComparer.OrdinalIgnoreCase);
        var planningSeenSequenceSnapshot = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var planningHandled = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (moduleId, _) in planningModules)
        {
            var client = await CreateClientAsync($"planning-{moduleId}");
            planningClients[moduleId] = client;
            await client.SubscribeAsync($"/{ns}/Planning/OfferedCapability/Request");
            client.OnMessage(async msg =>
            {
                try
                {
                    Console.WriteLine($"planning-{moduleId} received message type={msg?.Frame?.Type} conv={msg?.Frame?.ConversationId}");
                    LogEvent("in", moduleId, "n/a", msg);
                    if (!string.Equals(msg?.Frame?.ConversationId, conversationId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (!planningHandled.TryAdd(moduleId, true))
                    {
                        return; // already responded for this module in this test run
                    }

                    var hasSnapshot = msg?.InteractionElements?.Any(el => string.Equals(el.IdShort, "ManufacturingSequenceSnapshot", StringComparison.OrdinalIgnoreCase)) ?? false;
                    if (hasSnapshot)
                    {
                        planningSeenSequenceSnapshot[conversationId] = true;
                    }

                    var (mid, cap) = planningModules.First(p => string.Equals(p.moduleId, moduleId, StringComparison.OrdinalIgnoreCase));
                    var offer = BuildOfferedCapability(cap, mid);
                    var proposal = new I40MessageBuilder()
                        .From(mid, "PlanningHolon")
                        .To("ProductAgent", "ProductAgent")
                        .WithType(I40MessageTypes.PROPOSAL)
                        .WithConversationId(conversationId)
                        .AddElement(offer)
                        .Build();

                    // Send proposal to planning response topic (for dispatcher), not directly to ProductAgent
                    var responseTopic = $"/{ns}/Planning/OfferedCapability/Response";
                    // addressed to DispatchingAgent so dispatcher can collect and forward
                    proposal = new I40MessageBuilder()
                        .From(mid, "PlanningHolon")
                        .To("DispatchingAgent", "DispatchingAgent")
                        .WithType($"{I40MessageTypes.PROPOSAL}/OfferedCapability")
                        .WithConversationId(conversationId)
                        .AddElement(offer)
                        .Build();
                    LogEvent("out", moduleId, responseTopic, proposal);
                    await client.PublishAsync(proposal, responseTopic);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"planning-{moduleId} handler failed: {ex}");
                    sentLog.Add(new { topic = "planning-error", moduleId, error = ex.ToString() });
                }
            });
        }

        // Dispatcher: receive CfP, forward to S1 and re-broadcast to planning topics
        var dispatcherForwarding = 0;
        await dispatcher.SubscribeAsync($"/{ns}/ManufacturingSequence/Request");
        // also listen for planning responses (OfferedCapability proposals)
        await dispatcher.SubscribeAsync($"/{ns}/Planning/OfferedCapability/Response");
        dispatcher.OnMessage(async msg =>
        {
            try
            {
                if (msg != null)
                {
                    LogEvent("in", "DispatchingAgent", "n/a", msg);
                }
                if (msg == null || !string.Equals(msg.Frame?.ConversationId, conversationId, StringComparison.Ordinal))
                {
                    return;
                }

                // If this is a proposal from a planning holon (received on Planning/OfferedCapability/Response),
                // forward it to the ProductAgent as a ManufacturingSequence/proposal.
                if (msg.Frame?.Type != null && msg.Frame.Type.StartsWith(I40MessageTypes.PROPOSAL, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var forwardBuilder = new I40MessageBuilder()
                            .From("DispatchingAgent", "DispatchingAgent")
                            .To("ProductAgent", "ProductAgent")
                            .WithType($"{I40MessageTypes.PROPOSAL}/{I40MessageTypeSubtypes.ManufacturingSequence.ToProtocolString()}")
                            .WithConversationId(conversationId);
                        if (msg.InteractionElements != null)
                        {
                            foreach (var el in msg.InteractionElements)
                            {
                                if (el is SubmodelElement se)
                                {
                                    forwardBuilder.AddElement(se);
                                }
                            }
                        }
                        var forward = forwardBuilder.Build();
                        var forwardTopic = $"/{ns}/ManufacturingSequence/Response";
                        LogEvent("out", "DispatchingAgent", forwardTopic, forward);
                        await dispatcher.PublishAsync(forward, forwardTopic);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"dispatcher forward-proposal failed: {ex}");
                    }

                    return; // don't treat proposals as CfP
                }

                // Prevent re-entrancy because dispatcher publishes to the same topic it subscribes to.
                if (Interlocked.Exchange(ref dispatcherForwarding, 1) == 1)
                {
                    return;
                }

                try
                {
                    // Forward to S1 (AI agent) as calcSimilarity request
                    var s1Request = new I40MessageBuilder()
                        .From("DispatchingAgent", "DispatchingAgent")
                        .To("S1", "AIAgent")
                        .WithType("calcSimilarity")
                        .WithConversationId(conversationId)
                        .Build();
                    var s1Topic = $"/{ns}/S1/CalcSimilarity";
                    LogEvent("out", "DispatchingAgent", s1Topic, s1Request);
                    await dispatcher.PublishAsync(s1Request, s1Topic);

                    // Forward CfP to planning modules (broadcast) using OfferedCapability request topic
                    var planningTopic = $"/{ns}/Planning/OfferedCapability/Request";
                    // build a CfP specifically targeting OfferedCapability (type/subtype)
                    var offeredCfpBuilder = new I40MessageBuilder()
                        .From("DispatchingAgent", "DispatchingAgent")
                        .To("Broadcast", "ModuleHolon")
                        .WithType($"{I40MessageTypes.CALL_FOR_PROPOSAL}/OfferedCapability")
                        .WithConversationId(conversationId);
                    if (msg.InteractionElements != null)
                    {
                        foreach (var el in msg.InteractionElements)
                        {
                            if (el is SubmodelElement se)
                            {
                                offeredCfpBuilder.AddElement(se);
                            }
                        }
                    }
                    var offeredCfp = offeredCfpBuilder.Build();
                    LogEvent("out", "DispatchingAgent", planningTopic, offeredCfp);
                    await dispatcher.PublishAsync(offeredCfp, planningTopic);
                }
                finally
                {
                    Interlocked.Exchange(ref dispatcherForwarding, 0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"dispatcher handler failed: {ex}");
            }
        });

        // S1 answers with dummy similarity result
        var s1Handled = 0;
        await s1.SubscribeAsync($"/{ns}/S1/CalcSimilarity");
        s1.OnMessage(async msg =>
        {
            try
            {
                if (msg != null)
                {
                    LogEvent("in", "S1", "n/a", msg);
                }
                if (msg == null || !string.Equals(msg.Frame?.ConversationId, conversationId, StringComparison.Ordinal))
                {
                    return;
                }

                if (Interlocked.Exchange(ref s1Handled, 1) == 1)
                {
                    return;
                }

                var resp = new I40MessageBuilder()
                    .From("S1", "AIAgent")
                    .To("DispatchingAgent", "DispatchingAgent")
                    .WithType("inform")
                    .WithConversationId(conversationId)
                    .AddElement(new Property<string>("Similarity") { Value = new PropertyValue<string>("1.0") })
                    .Build();
                var respTopic = $"/{ns}/Dispatching/Similarity";
                LogEvent("out", "S1", respTopic, resp);
                await s1.PublishAsync(resp, respTopic);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"s1 handler failed: {ex}");
            }
        });

        // Transport manager dummy response
        var transportHandled = 0;
        await transportManager.SubscribeAsync($"/{ns}/TransportPlan/Request");
        var transportResponseTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        transportManager.OnMessage(async msg =>
        {
            try
            {
                if (msg != null)
                {
                    LogEvent("in", "TransportManager", "n/a", msg);
                }
                if (msg == null)
                {
                    return;
                }

                if (Interlocked.Exchange(ref transportHandled, 1) == 1)
                {
                    return;
                }
                var resp = new I40MessageBuilder()
                    .From("TransportManager", "TransportManager")
                    .To(msg.Frame?.Sender?.Identification?.Id ?? "DispatchingAgent", msg.Frame?.Sender?.Role?.Name)
                    .WithType($"{I40MessageTypes.CONSENT}/{I40MessageTypeSubtypes.TransportRequest.ToProtocolString()}")
                    .WithConversationId(msg.Frame?.ConversationId ?? conversationId)
                    .AddElement(new Property<string>("OfferedCapability") { Value = new PropertyValue<string>("Transport") })
                    .Build();
                var respTopic = $"/{ns}/TransportPlan/Response";
                LogEvent("out", "TransportManager", respTopic, resp);
                await transportManager.PublishAsync(resp, respTopic);
                transportResponseTcs.TrySetResult(resp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"transport handler failed: {ex}");
            }
        });

        // Product listens for offers and transport responses
        var offers = new ConcurrentBag<I40Message>();
        var offersTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await product.SubscribeAsync($"/{ns}/ManufacturingSequence/Response");
        await product.SubscribeAsync($"/{ns}/TransportPlan/Response");
        product.OnMessage(msg =>
        {
            if (msg == null) return;
            LogEvent("in", "ProductAgent", "n/a", msg);
            if (string.Equals(msg.Frame?.Type?.Split('/')[0], I40MessageTypes.PROPOSAL, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(msg.Frame?.ConversationId, conversationId, StringComparison.Ordinal))
            {
                offers.Add(msg);
                if (offers.Count >= planningModules.Length)
                {
                    offersTcs.TrySetResult(true);
                }
            }

            if (msg.Frame?.Type?.StartsWith(I40MessageTypes.CONSENT, StringComparison.OrdinalIgnoreCase) == true)
            {
                transportResponseTcs.TrySetResult(msg);
            }
        });

        // Send initial CfP from product, including manufacturing sequence snapshot and transport request
        var manufacturingSnapshot = BuildSequenceSnapshot(productId);
        var cfp = new I40MessageBuilder()
            .From("ProductAgent", "ProductAgent")
            .To("DispatchingAgent", "DispatchingAgent")
            .WithType($"{I40MessageTypes.CALL_FOR_PROPOSAL}/{I40MessageTypeSubtypes.ManufacturingSequence.ToProtocolString()}")
            .WithConversationId(conversationId)
            .AddElement(new Property<string>("ProductId") { Value = new PropertyValue<string>(productId) })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>("req-root") })
            .AddElement(manufacturingSnapshot)
            .Build();

        var cfpTopic = $"/{ns}/ManufacturingSequence/Request";
        LogEvent("out", "ProductAgent", cfpTopic, cfp);
        await product.PublishAsync(cfp, cfpTopic);

        // Also send a transport request so the dummy transport manager responds
        var transportReq = new I40MessageBuilder()
            .From("DispatchingAgent", "DispatchingAgent")
            .To("TransportManager", "TransportManager")
            .WithType($"{I40MessageTypes.CALL_FOR_PROPOSAL}/{I40MessageTypeSubtypes.TransportRequest.ToProtocolString()}")
            .WithConversationId(conversationId)
            .AddElement(new Property<string>("ProductId") { Value = new PropertyValue<string>(productId) })
            .Build();
        var transportReqTopic = $"/{ns}/TransportPlan/Request";
        LogEvent("out", "DispatchingAgent", transportReqTopic, transportReq);
        await product.PublishAsync(transportReq, transportReqTopic);

        // Await offers and transport reply
        var offersAndTransport = Task.WhenAll(offersTcs.Task, transportResponseTcs.Task);
        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(offersAndTransport, timeout);
        if (ReferenceEquals(timeout, completed))
        {
            Console.WriteLine($"Timeout waiting for offers/transport. Offers={offers.Count}, transportDone={transportResponseTcs.Task.IsCompleted}, snapshotsSeen={planningSeenSequenceSnapshot.Count}");
            var logDirTimeout = Path.Combine(Path.GetDirectoryName(typeof(CabABlueScenarioTests).Assembly.Location) ?? AppContext.BaseDirectory, "../../../tests/TestResults");
            Directory.CreateDirectory(logDirTimeout);
            var logPathTimeout = Path.Combine(logDirTimeout, $"CabABlue-timeout-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            var jsonTimeout = JsonSerializer.Serialize(sentLog, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(logPathTimeout, jsonTimeout);
            throw new TimeoutException($"Did not receive expected offers/transport response in time. Offers={offers.Count}, snapshotsSeen={planningSeenSequenceSnapshot.Count}, transportDone={transportResponseTcs.Task.IsCompleted}");
        }

        Assert.Equal(planningModules.Length, offers.Count);
        var capabilities = offers.SelectMany(o => o.InteractionElements.OfType<SubmodelElementCollection>())
            .Select(c => c.IdShort)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Subset(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Drill", "Screw", "Assemble" }, capabilities);

        Assert.True(planningSeenSequenceSnapshot.ContainsKey(conversationId), "Planning holons did not see ManufacturingSequence snapshot in CfP");

        var transportResp = await transportResponseTcs.Task;
        Assert.NotNull(transportResp);

        // Persist logs for debugging
        var logDir = Path.Combine(Path.GetDirectoryName(typeof(CabABlueScenarioTests).Assembly.Location) ?? AppContext.BaseDirectory, "../../../tests/TestResults");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"CabABlue-logs-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
        var json = JsonSerializer.Serialize(sentLog, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(logPath, json);
        Console.WriteLine($"Saved Cab_A_Blue scenario log to {logPath}");
    }

    private static SubmodelElementCollection BuildOfferedCapability(string capabilityIdShort, string moduleId)
    {
        var capability = new SubmodelElementCollection(capabilityIdShort);
        capability.Add(new Property<string>("InstanceIdentifier") { Value = new PropertyValue<string>($"{capabilityIdShort}-{moduleId}") });
        capability.Add(new Property<string>("Station") { Value = new PropertyValue<string>(moduleId) });
        var actions = new SubmodelElementCollection("Actions");
        var action = new SubmodelElementCollection($"Action-{capabilityIdShort}");
        action.Add(new Property<string>("ActionTitle") { Value = new PropertyValue<string>($"{capabilityIdShort}_Action") });
        action.Add(new Property<string>("InputParameters") { Value = new PropertyValue<string>("[]") });
        actions.Add(action);
        capability.Add(actions);
        return capability;
    }

    private static SubmodelElementCollection BuildSequenceSnapshot(string productId)
    {
        var snapshot = new SubmodelElementCollection("ManufacturingSequenceSnapshot");
        snapshot.Add(new Property<string>("ProductId") { Value = new PropertyValue<string>(productId) });
        snapshot.Add(new Property<string>("CompletedSteps") { Value = new PropertyValue<string>("[\"Load\",\"Unload\"]") });
        snapshot.Add(new Property<string>("CurrentStep") { Value = new PropertyValue<string>("Assemble") });
        return snapshot;
    }

    private static string? ExtractProductId(string envPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(envPath));
            var aas = doc.RootElement.GetProperty("assetAdministrationShells")[0];
            return aas.GetProperty("id").GetString();
        }
        catch
        {
            return null;
        }
    }
}
