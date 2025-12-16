using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ManufacturingSequence;
using AasSharpClient.Models.ProcessChain;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using MAS_BT.Nodes.Planning.ProcessChain;
using MAS_BT.Nodes.Dispatching.ProcessChain;
using MAS_BT.Services.Graph;
using MAS_BT.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Transport;
using BaSyx.Models.AdminShell;
using Xunit;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Tests;

public class ModuleMessagingIntegrationTests : IDisposable
{
    private static readonly MessageSerializer Serializer = new();
    private static readonly bool UseRealMqtt = string.Equals(Environment.GetEnvironmentVariable("MASBT_TEST_USE_REAL_MQTT"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly string RealMqttHost = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_HOST") ?? "192.168.178.30";
    private static readonly int RealMqttPort = int.TryParse(Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PORT"), out var parsedPort) ? parsedPort : 1883;
    private static readonly string? RealMqttUsername = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_USERNAME");
    private static readonly string? RealMqttPassword = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PASSWORD");

    private readonly List<MessagingClient> _clients = new();

    private static string ResolveTestFile(string name)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../tests/TestFiles", name));
    }

    private static I40Message LoadMessage(string name)
    {
        var path = ResolveTestFile(name);
        var json = File.ReadAllText(path);
        return Serializer.Deserialize(json) ?? throw new InvalidOperationException($"Failed to load message {name}");
    }

    private async Task<MessagingClient> CreateClientAsync(string defaultTopic)
    {
        IMessagingTransport transport = UseRealMqtt
            ? new MqttTransport(
                RealMqttHost,
                RealMqttPort,
                $"masbt-tests-{Guid.NewGuid():N}",
                RealMqttUsername,
                RealMqttPassword)
            : new InMemoryTransport();

        var client = new MessagingClient(transport, defaultTopic);
        await client.ConnectAsync();
        _clients.Add(client);
        return client;
    }

    [Fact]
    public async Task ForwardCapabilityRequests_ForwardsDispatcherCfpToPlanningTopic()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var moduleId = "P102";

        var moduleContext = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = moduleId,
            AgentRole = "ModuleHolon"
        };
        moduleContext.Set("config.Namespace", ns);
        moduleContext.Set("Namespace", ns);
        moduleContext.Set("config.Agent.AgentId", moduleId);
        moduleContext.Set("ModuleId", moduleId);
        var moduleNameAlias = "AssemblyStation";
        moduleContext.Set("config.Agent.ModuleName", moduleNameAlias);

        var moduleClient = await CreateClientAsync($"{moduleId}/logs");
        await moduleClient.SubscribeAsync($"/{ns}/DispatchingAgent/Offer");
        moduleContext.Set("MessagingClient", moduleClient);

        var planningClients = new List<(MessagingClient client, TaskCompletionSource<I40Message?> tcs)>();
        var conversationId = Guid.NewGuid().ToString();

        var primaryPlanningClient = await CreateClientAsync($"{moduleId}/planning");
        await primaryPlanningClient.SubscribeAsync($"/{ns}/{moduleId}/PlanningAgent/OfferRequest");
        var primaryTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        primaryPlanningClient.OnMessage(msg =>
        {
            if (!primaryTcs.Task.IsCompleted &&
                string.Equals(msg.Frame?.ConversationId, conversationId, StringComparison.Ordinal))
            {
                primaryTcs.TrySetResult(msg);
            }
        });
        planningClients.Add((primaryPlanningClient, primaryTcs));

        if (!string.Equals(moduleId, moduleNameAlias, StringComparison.OrdinalIgnoreCase))
        {
            var aliasClient = await CreateClientAsync($"{moduleNameAlias}/planning");
            var aliasTopic = $"/{ns}/{moduleNameAlias}/PlanningAgent/OfferRequest";
            await aliasClient.SubscribeAsync(aliasTopic);
            var aliasTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
            aliasClient.OnMessage(msg =>
            {
                if (!aliasTcs.Task.IsCompleted &&
                    string.Equals(msg.Frame?.ConversationId, conversationId, StringComparison.Ordinal))
                {
                    aliasTcs.TrySetResult(msg);
                }
            });
            planningClients.Add((aliasClient, aliasTcs));
        }

        var node = new ForwardCapabilityRequestsNode { Context = moduleContext };
        node.SetLogger(NullLogger<ForwardCapabilityRequestsNode>.Instance);

        var initialStatus = await node.Execute();
        Assert.Equal(NodeStatus.Failure, initialStatus);

        var dispatcherClient = await CreateClientAsync("dispatch/logs");

        var cfpMessage = new I40MessageBuilder()
            .From("DispatchingAgent", "DispatchingAgent")
            .To("Broadcast", "ModuleHolon")
            .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
            .WithConversationId(conversationId)
            .AddElement(new Property<string>("Capability") { Value = new PropertyValue<string>("Drill") })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>("req-1") })
            .Build();

        Console.WriteLine($"ReceiverId={cfpMessage.Frame?.Receiver?.Identification?.Id}");

        await dispatcherClient.PublishAsync(cfpMessage, $"/{ns}/DispatchingAgent/Offer");

        var forwardStatus = await node.Execute();
        Assert.Equal(NodeStatus.Success, forwardStatus);

        var allTasks = Task.WhenAll(planningClients.Select(pc => pc.tcs.Task));
        var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(allTasks, completed);

        foreach (var (_, tcs) in planningClients)
        {
            var forwarded = await tcs.Task;
            Assert.NotNull(forwarded);
            Assert.Equal(conversationId, forwarded!.Frame?.ConversationId);
            Assert.True(IsCallForProposalType(forwarded.Frame?.Type),
                $"Expected callForProposal (or subtype) but got '{forwarded.Frame?.Type}'");
        }
    }

    [Fact]
    public async Task PlanningAgentNodes_SendProposalResponse()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var moduleId = "P102";
        var planningContext = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "P102_Planning",
            AgentRole = "PlanningHolon"
        };
        planningContext.Set("config.Namespace", ns);
        planningContext.Set("Namespace", ns);
        planningContext.Set("config.Agent.ModuleName", moduleId);
        planningContext.Set("ModuleId", moduleId);

        var planningClient = await CreateClientAsync("planning/logs");
        planningContext.Set("MessagingClient", planningClient);

        var moduleClient = await CreateClientAsync("module/logs");
        var responseTopic = $"/{ns}/{moduleId}/PlanningAgent/OfferResponse";
        await moduleClient.SubscribeAsync(responseTopic);

        var proposalTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        moduleClient.OnMessage(msg => proposalTcs.TrySetResult(msg));

        var conversationId = Guid.NewGuid().ToString();
        var cfp = new I40MessageBuilder()
            .From("DispatchingAgent", "DispatchingAgent")
            .To("Broadcast", "ModuleHolon")
            .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
            .WithConversationId(conversationId)
            .AddElement(new Property<string>("Capability") { Value = new PropertyValue<string>("Assemble") })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>("req-99") })
            .Build();

        planningContext.Set("LastReceivedMessage", cfp);

        var parseNode = new ParseCapabilityRequestNode { Context = planningContext };
        var planNode = new PlanCapabilityOfferNode { Context = planningContext };
        var sendNode = new SendCapabilityOfferNode { Context = planningContext };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        Assert.Equal(NodeStatus.Success, await planNode.Execute());
        Assert.Equal(NodeStatus.Success, await sendNode.Execute());

        var completed = await Task.WhenAny(proposalTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(proposalTcs.Task, completed);

        var proposal = await proposalTcs.Task;
        Assert.NotNull(proposal);
        Assert.Equal(I40MessageTypes.PROPOSAL, proposal!.Frame?.Type);
        Assert.Equal(conversationId, proposal.Frame?.ConversationId);

        var offered = proposal.InteractionElements
            .OfType<SubmodelElementCollection>()
            .FirstOrDefault(smc => string.Equals(smc.IdShort, "OfferedCapability", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(offered);

        var actions = offered!.Values?.OfType<SubmodelElementList>()
            .FirstOrDefault(list => string.Equals(list.IdShort, "Actions", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(actions);
        Assert.True(actions!.Any(), "OfferedCapability.Actions must not be empty");

        var firstAction = actions.OfType<SubmodelElementCollection>().FirstOrDefault();
        Assert.NotNull(firstAction);
        var inputParameters = firstAction!.Values?.OfType<SubmodelElementCollection>()
            .FirstOrDefault(smc => string.Equals(smc.IdShort, "InputParameters", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(inputParameters);
    }

    [Fact]
    public async Task DispatchingAgentProcessesRealProcessChainMessage()
    {
        var (offers, ctx, processChain) = await DispatchProcessChainAsync();
        Assert.NotNull(ctx);
        Assert.Equal(ctx!.Requirements.Count, offers.Count);

        foreach (var requirement in ctx.Requirements)
        {
            var match = offers.FirstOrDefault(msg => string.Equals(ExtractProperty(msg, "Capability"), requirement.Capability, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(match);
            Assert.Equal(processChain.Frame?.ConversationId, match!.Frame?.ConversationId);
            Assert.True(IsCallForProposalType(match.Frame?.Type),
                $"Expected callForProposal (or subtype) but got '{match.Frame?.Type}'");
            Assert.Equal("ModuleHolon", match.Frame?.Receiver?.Role?.Name);

            if (requirement.CapabilityContainer != null)
            {
                Assert.Contains(match.InteractionElements.OfType<SubmodelElementCollection>(),
                    smc => string.Equals(smc.IdShort, requirement.CapabilityContainer.IdShort, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task PlanningAgentRespondsToDispatcherOffer()
    {
        var (offers, _, _) = await DispatchProcessChainAsync();
        Assert.NotEmpty(offers);
        var cfp = offers.First();
        var ns = "phuket";
        var moduleId = "P102";

        var planningContext = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "P102_PlanningHolon",
            AgentRole = "PlanningHolon"
        };
        planningContext.Set("config.Namespace", ns);
        planningContext.Set("Namespace", ns);
        planningContext.Set("config.Agent.ModuleName", moduleId);
        planningContext.Set("ModuleId", moduleId);
        planningContext.Set("LastReceivedMessage", cfp);

        var planningClient = await CreateClientAsync("planning/logs");
        planningContext.Set("MessagingClient", planningClient);

        var moduleClient = await CreateClientAsync("module/logs");
        var responseTopic = $"/{ns}/{moduleId}/PlanningAgent/OfferResponse";
        await moduleClient.SubscribeAsync(responseTopic);

        var proposalTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        moduleClient.OnMessage(msg => proposalTcs.TrySetResult(msg));

        var parseNode = new ParseCapabilityRequestNode { Context = planningContext };
        var planNode = new PlanCapabilityOfferNode { Context = planningContext };
        var sendNode = new SendCapabilityOfferNode { Context = planningContext };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        Assert.Equal(NodeStatus.Success, await planNode.Execute());
        Assert.Equal(NodeStatus.Success, await sendNode.Execute());

        var completed = await Task.WhenAny(proposalTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(proposalTcs.Task, completed);
        var proposal = await proposalTcs.Task;
        Assert.NotNull(proposal);

        Assert.Equal(I40MessageTypes.PROPOSAL, proposal!.Frame?.Type);
        Assert.Equal(cfp.Frame?.ConversationId, proposal.Frame?.ConversationId);

        var offered = proposal.InteractionElements
            .OfType<SubmodelElementCollection>()
            .FirstOrDefault(smc => string.Equals(smc.IdShort, "OfferedCapability", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(offered);

        var actions = offered!.Values?.OfType<SubmodelElementList>()
            .FirstOrDefault(list => string.Equals(list.IdShort, "Actions", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(actions);
        Assert.True(actions!.Any(), "OfferedCapability.Actions must not be empty");

        var firstAction = actions.OfType<SubmodelElementCollection>().FirstOrDefault();
        Assert.NotNull(firstAction);
        var inputParameters = firstAction!.Values?.OfType<SubmodelElementCollection>()
            .FirstOrDefault(smc => string.Equals(smc.IdShort, "InputParameters", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(inputParameters);
    }

    [Fact]
    public async Task DispatcherBuildsManufacturingSequenceWithTransportSequences()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var manufacturingRequest = CreateManufacturingSequenceRequest();
        manufacturingRequest.Frame.Type = $"{I40MessageTypes.CALL_FOR_PROPOSAL}/{I40MessageTypeSubtypes.ManufacturingSequence.ToProtocolString()}";
        manufacturingRequest.Frame.Receiver ??= new Participant();
        manufacturingRequest.Frame.Receiver.Identification ??= new Identification();
        manufacturingRequest.Frame.Receiver.Identification.Id = $"{ns}/DispatchingAgent";
        manufacturingRequest.Frame.Receiver.Role ??= new Role();
        manufacturingRequest.Frame.Receiver.Role.Name = "DispatchingAgent";

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "DispatchingAgent_tests",
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("Namespace", ns);
        context.Set("LastReceivedMessage", manufacturingRequest);
        context.Set("ProcessChain.RequestType", "ManufacturingSequence");
        context.Set("CapabilityReferenceQuery", new PassthroughCapabilityReferenceQuery());

        var dispatchClient = await CreateClientAsync("dispatch/logs");
        await dispatchClient.SubscribeAsync($"/{ns}/DispatchingAgent/Offer");
        context.Set("MessagingClient", dispatchClient);

        var moduleClient = await CreateClientAsync("module/logs");
        var offerTopic = $"/{ns}/DispatchingAgent/Offer";
        await moduleClient.SubscribeAsync(offerTopic);

        var parseNode = new ParseProcessChainRequestNode { Context = context };
        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        var negotiation = context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation")
                           ?? throw new InvalidOperationException("Negotiation context missing");
        var expectedRequirements = negotiation.Requirements.Count;
        Assert.True(expectedRequirements > 0);

        var state = new DispatchingState();
        state.Upsert(new DispatchingModuleInfo { ModuleId = "P101", Capabilities = new List<string> { "Drill" } });
        state.Upsert(new DispatchingModuleInfo { ModuleId = "P100", Capabilities = new List<string> { "Screw" } });
        state.Upsert(new DispatchingModuleInfo { ModuleId = "P102", Capabilities = new List<string> { "Assemble" } });
        context.Set("DispatchingState", state);
        context.Set("ProcessChain.CfPTopic", offerTopic);

        var cfpMessages = new List<I40Message>();
        var cfpCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SubscribeToCallForProposalTypes(moduleClient, msg =>
        {
            lock (cfpMessages)
            {
                cfpMessages.Add(msg);
                if (cfpMessages.Count >= expectedRequirements)
                {
                    cfpCompletion.TrySetResult(true);
                }
            }
        });

        var dispatchNode = new DispatchCapabilityRequestsNode { Context = context };
        Assert.Equal(NodeStatus.Success, await dispatchNode.Execute());
        var completed = await Task.WhenAny(cfpCompletion.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(cfpCompletion.Task, completed);

        List<I40Message> cfps;
        lock (cfpMessages)
        {
            cfps = cfpMessages.ToList();
        }
        Assert.Equal(expectedRequirements, cfps.Count);

        var collectNode = new CollectCapabilityOfferNode { Context = context, TimeoutSeconds = 5 };
        Assert.Equal(NodeStatus.Running, await collectNode.Execute());

        foreach (var cfp in cfps)
        {
            var capability = ExtractProperty(cfp, "Capability") ?? string.Empty;
            var moduleId = capability switch
            {
                "Drill" => "P101",
                "Screw" => "P100",
                "Assemble" => "P102",
                _ => "P999"
            };

            var transports = capability.Equals("Drill", StringComparison.OrdinalIgnoreCase)
                ? new[]
                {
                    (InstanceId: "transport-pre-drill", Placement: TransportPlacement.BeforeCapability),
                    (InstanceId: "transport-post-drill", Placement: TransportPlacement.AfterCapability)
                }
                : Array.Empty<(string InstanceId, TransportPlacement Placement)>();

            var offeredCapability = CreateOfferedCapabilityWithTransports(moduleId, capability, transports);
            await PublishProposalAsync(moduleClient, cfp, moduleId, offeredCapability, ns);
        }

        NodeStatus collectStatus;
        var attempts = 0;
        do
        {
            collectStatus = await collectNode.Execute();
            if (collectStatus == NodeStatus.Running)
            {
                await Task.Delay(20);
            }
        } while (collectStatus == NodeStatus.Running && attempts++ < 100);
        Assert.Equal(NodeStatus.Success, collectStatus);

        var buildNode = new BuildProcessChainResponseNode { Context = context };
        Assert.Equal(NodeStatus.Success, await buildNode.Execute());

        var manufacturingResult = context.Get<SubmodelElement>("ManufacturingSequence.Result") as ManufacturingSequence;
        Assert.NotNull(manufacturingResult);
        var requiredCapabilities = manufacturingResult!.GetRequiredCapabilities().ToList();
        Assert.Equal(expectedRequirements, requiredCapabilities.Count);

        var drillEntry = requiredCapabilities.First(rc => string.Equals(
            rc.InstanceIdentifier.GetText(),
            "req-drill",
            StringComparison.OrdinalIgnoreCase));

        var drillSequence = drillEntry.GetSequences()
            .Select(seq => seq.GetCapabilities().ToList())
            .First(seq => seq.Any(cap => string.Equals(cap.InstanceIdentifier.GetText(), "P101-Drill", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(3, drillSequence.Count);
        Assert.Equal("transport-pre-drill", drillSequence[0].InstanceIdentifier.GetText());
        Assert.Equal("P101-Drill", drillSequence[1].InstanceIdentifier.GetText());
        Assert.Equal("transport-post-drill", drillSequence[2].InstanceIdentifier.GetText());

        var assembleEntry = requiredCapabilities.First(rc => string.Equals(
            rc.InstanceIdentifier.GetText(),
            "req-assemble",
            StringComparison.OrdinalIgnoreCase));
        var assembleSequence = assembleEntry.GetSequences().First().GetCapabilities().ToList();
        Assert.Single(assembleSequence);
        Assert.Equal("P102-Assemble", assembleSequence[0].InstanceIdentifier.GetText());

        Assert.True(context.Get<bool>("ProcessChain.Success"));
        Assert.True(context.Get<bool>("ManufacturingSequence.Success"));
    }

    private async Task<(List<I40Message> Offers, ProcessChainNegotiationContext Ctx, I40Message OriginalRequest)> DispatchProcessChainAsync()
    {
        var processChain = LoadMessage("ProcessChain.json");
        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "DispatchingAgent_phuket",
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", "phuket");
        context.Set("Namespace", "phuket");
        context.Set("LastReceivedMessage", processChain);

        var dispatchClient = await CreateClientAsync("dispatch/logs");
        context.Set("MessagingClient", dispatchClient);

        var moduleClient = await CreateClientAsync("module/logs");
        await moduleClient.SubscribeAsync("/phuket/DispatchingAgent/Offer");

        var parseNode = new ParseProcessChainRequestNode { Context = context };
        var dispatchNode = new DispatchCapabilityRequestsNode { Context = context };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        var negotiation = context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation")
                           ?? throw new InvalidOperationException("Negotiation context missing after parse");
        var expectedCount = negotiation.Requirements.Count;
        Assert.True(expectedCount > 0, "Process chain did not contain requirements");

        // Seed at least one registered module so DispatchCapabilityRequests can actually emit CfPs.
        // The dispatch node selects candidate modules from DispatchingState; without it, no CfPs are published.
        var state = context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
        var caps = negotiation.Requirements
            .Select(r => r.Capability)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.Upsert(new DispatchingModuleInfo { ModuleId = "P102", Capabilities = caps });
        context.Set("DispatchingState", state);

        var offers = new List<I40Message>();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SubscribeToCallForProposalTypes(moduleClient, msg =>
        {
            lock (offers)
            {
                offers.Add(msg);
                if (offers.Count >= expectedCount)
                {
                    completion.TrySetResult(true);
                }
            }
        });

        Assert.Equal(NodeStatus.Success, await dispatchNode.Execute());
        var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(completion.Task, completed);

        lock (offers)
        {
            return (offers.ToList(), negotiation, processChain);
        }
    }

    private static I40Message CreateManufacturingSequenceRequest()
    {
        var message = LoadMessage("ProcessChain.json");
        var processChainElement = BuildProcessChainElement();
        message.InteractionElements.Add(processChainElement);
        return message;
    }

    private static SubmodelElementCollection BuildProcessChainElement()
    {
        var processChain = new ProcessChain();
        processChain.AddRequiredCapability(CreateRequiredCapabilityEntry("Drill", "req-drill"));
        processChain.AddRequiredCapability(CreateRequiredCapabilityEntry("Screw", "req-screw"));
        processChain.AddRequiredCapability(CreateRequiredCapabilityEntry("Assemble", "req-assemble"));
        return processChain;
    }

    private static RequiredCapability CreateRequiredCapabilityEntry(string capability, string requirementId)
    {
        var required = new RequiredCapability($"RequiredCapability_{capability}");
        required.SetInstanceIdentifier(requirementId);
        required.SetRequiredCapabilityReference(CreateCapabilityReference(capability));
        return required;
    }

    private static Reference CreateCapabilityReference(string capability)
    {
        var keys = new List<IKey> { new Key(KeyType.GlobalReference, $"capability:{capability}") };
        return new Reference(keys) { Type = ReferenceType.ExternalReference };
    }

    private static OfferedCapability CreateOfferedCapabilityWithTransports(
        string moduleId,
        string capability,
        IEnumerable<(string InstanceId, TransportPlacement Placement)> transports)
    {
        var offered = new OfferedCapability("OfferedCapability");
        offered.InstanceIdentifier.Value = new PropertyValue<string>($"{moduleId}-{capability}");
        offered.Station.Value = new PropertyValue<string>(moduleId);
        offered.MatchingScore.Value = new PropertyValue<double>(1.0);
        offered.SetCost(100);
        offered.OfferedCapabilityReference.Value = new ReferenceElementValue(CreateCapabilityReference(capability));
        offered.AddAction(new ActionModel($"Action_{capability}", capability, ActionStatusEnum.PLANNED, null, null, null, null, moduleId));

        foreach (var transport in transports ?? Array.Empty<(string InstanceId, TransportPlacement Placement)>())
        {
            var nested = new OfferedCapability("OfferedCapability");
            nested.InstanceIdentifier.Value = new PropertyValue<string>(transport.InstanceId);
            nested.Station.Value = new PropertyValue<string>("Transport");
            nested.SetSequencePlacement(transport.Placement == TransportPlacement.AfterCapability ? "post" : "pre");
            nested.SetCost(0);
            nested.OfferedCapabilityReference.Value = new ReferenceElementValue(CreateCapabilityReference("Transport"));
            nested.AddAction(new ActionModel("ActionTransport", "Transport", ActionStatusEnum.PLANNED, null, null, null, null, "Transport"));
            offered.AddCapabilityToSequence(nested);
        }

        return offered;
    }

    private static async Task PublishProposalAsync(
        MessagingClient client,
        I40Message cfp,
        string moduleId,
        OfferedCapability offeredCapability,
        string ns)
    {
        var capability = ExtractProperty(cfp, "Capability") ?? offeredCapability.InstanceIdentifier.GetText() ?? "Capability";
        var requirementId = ExtractProperty(cfp, "RequirementId") ?? Guid.NewGuid().ToString();

        var message = new I40MessageBuilder()
            .From($"{moduleId}_Planning", "PlanningHolon")
            .To("DispatchingAgent", "DispatchingAgent")
            .WithType(I40MessageTypes.PROPOSAL)
            .WithConversationId(cfp.Frame?.ConversationId ?? Guid.NewGuid().ToString())
            .AddElement(new Property<string>("Capability") { Value = new PropertyValue<string>(capability) })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>(requirementId) })
            .AddElement(offeredCapability)
            .Build();

        await client.PublishAsync(message, $"/{ns}/DispatchingAgent/Offer");
    }

    private sealed class PassthroughCapabilityReferenceQuery : ICapabilityReferenceQuery
    {
        public Task<string?> GetCapabilityReferenceJsonAsync(string moduleShellId, string capabilityIdShort, System.Threading.CancellationToken cancellationToken = default)
        {
            var json = $"[{{\"type\":\"GlobalReference\",\"value\":\"capability:{capabilityIdShort}\"}}]";
            return Task.FromResult<string?>(json);
        }
    }

    private static readonly IReadOnlyList<string> CallForProposalTypeVariants = BuildCallForProposalTypeVariants();

    private static IReadOnlyList<string> BuildCallForProposalTypeVariants()
    {
        var variants = new List<string> { I40MessageTypes.CALL_FOR_PROPOSAL };
        foreach (var subtype in Enum.GetValues<I40MessageTypeSubtypes>())
        {
            if (subtype == I40MessageTypeSubtypes.None)
            {
                continue;
            }

            var token = subtype.ToProtocolString();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            variants.Add($"{I40MessageTypes.CALL_FOR_PROPOSAL}/{token}");
        }

        return variants;
    }

    private static void SubscribeToCallForProposalTypes(MessagingClient client, Action<I40Message> callback)
    {
        foreach (var variant in CallForProposalTypeVariants)
        {
            client.OnMessageType(variant, callback);
        }
    }

    private static bool IsCallForProposalType(string? messageType)
    {
        if (string.IsNullOrWhiteSpace(messageType))
        {
            return false;
        }

        foreach (var variant in CallForProposalTypeVariants)
        {
            if (string.Equals(messageType, variant, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractProperty(I40Message message, string idShort)
    {
        if (message?.InteractionElements == null)
        {
            return null;
        }

        foreach (var element in message.InteractionElements)
        {
            if (element is Property prop && string.Equals(prop.IdShort, idShort, StringComparison.OrdinalIgnoreCase))
            {
                return prop.GetText();
            }
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // ignored
            }
        }
        _clients.Clear();
    }
}
