using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ManufacturingSequence;
using AasSharpClient.Models.Messages;
using AasSharpClient.Models.ProcessChain;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using MAS_BT.Services.Graph;
using MAS_BT.Core;
using MAS_BT.Nodes.Dispatching;
using MAS_BT.Nodes.Planning;
using MAS_BT.Nodes.Planning.ProcessChain;
using MAS_BT.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using I40Sharp.Messaging.Core;
using Xunit;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Tests.Integration;

public class TransportFlowIntegrationTests
{
    [Fact]
    public async Task TransportRequest_Response_PreservedProductIdAndTopics()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var transport = MAS_BT.Tests.TestHelpers.TestTransportFactory.CreateTransport("transportflow");

        // Setup dispatch client which will handle TransportPlan requests and reply with a transport offer
        var dispatchClient = new MessagingClient(transport, "dispatch/logs");
        await dispatchClient.ConnectAsync();
        await dispatchClient.SubscribeAsync($"/{ns}/TransportPlan/Request");
        var responded = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        dispatchClient.OnMessage(async msg =>
        {
            if (msg == null) return;
            var conv = msg.Frame?.ConversationId ?? "";
            if (!responded.TryAdd(conv, true))
            {
                // already responded to this conversation
                return;
            }
            var handler = new HandleTransportPlanRequestNode();
            handler.Initialize(new BTContext(NullLogger<BTContext>.Instance), NullLogger<HandleTransportPlanRequestNode>.Instance);
            handler.Context.Set("config.Namespace", ns);
            handler.Context.Set("MessagingClient", dispatchClient);
            handler.Context.Set("LastReceivedMessage", msg);
            var productId = ResolveTransportProductId(msg);
            if (!string.IsNullOrWhiteSpace(productId))
            {
                handler.Context.Set("ManufacturingSequence.ByProduct", BuildManufacturingSequenceIndex(productId));
            }
            await handler.Execute().ConfigureAwait(false);
        });

        // Setup planning client and context
        var planningClient = new MessagingClient(transport, "planning/logs");
        await planningClient.ConnectAsync();

        var planningContext = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "P100_Planning",
            AgentRole = "PlanningHolon"
        };
        planningContext.Set("config.Namespace", ns);
        planningContext.Set("Namespace", ns);
        planningContext.Set("ModuleId", "P100");
        planningContext.Set("config.Agent.ModuleId", "P100");
        planningContext.Set("MessagingClient", planningClient);
        // Force transport requests in tests
        planningContext.Set("RequiresTransport", true);
        // simple passthrough capability reference query for tests
        planningContext.Set("CapabilityReferenceQuery", new TestPassthroughCapabilityReferenceQuery());

        // Compose a CallForProposal message and parse into Planning.CapabilityRequest
        var productId = $"product-{Guid.NewGuid():N}";
        var conversationId = productId;
        var cfp = new I40MessageBuilder()
            .From("DispatchingAgent", "DispatchingAgent")
            .To("Broadcast", "ModuleHolon")
            .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
            .WithConversationId(conversationId)
            .AddElement(new Property<string>("Capability") { Value = new PropertyValue<string>("TransportTestCapability") })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>("req-42") })
            .Build();

        planningContext.Set("LastReceivedMessage", cfp);

        // Run parse node to establish Planning.CapabilityRequest
        var parseNode = new MAS_BT.Nodes.Planning.ProcessChain.ParseCapabilityRequestNode { Context = planningContext };
        parseNode.SetLogger(NullLogger<MAS_BT.Nodes.Planning.ProcessChain.ParseCapabilityRequestNode>.Instance);
        Assert.Equal(NodeStatus.Success, await parseNode.Execute());

        // Force manufacturing-subtype so RequestTransportNode issues transport requests in this test
        var parsed = planningContext.Get<MAS_BT.Nodes.Planning.ProcessChain.CapabilityRequestContext>("Planning.CapabilityRequest");
        if (parsed != null)
        {
            parsed.SubType = I40Sharp.Messaging.Models.I40MessageTypeSubtypes.ManufacturingSequence;
            planningContext.Set("Planning.CapabilityRequest", parsed);
        }

        // Run RequestTransportNode which will publish to /{ns}/TransportPlan/Request and wait for the /Response reply
        var requestTransportNode = new RequestTransportNode { Context = planningContext };
        requestTransportNode.SetLogger(NullLogger<RequestTransportNode>.Instance);
        var rtStatus = await requestTransportNode.Execute();
        Assert.Equal(NodeStatus.Success, rtStatus);

        // TransportOffers should now be present in planningContext
        var transportOffers = planningContext.Get<System.Collections.Generic.List<OfferedCapability>>("Planning.TransportOffers");
        Assert.NotNull(transportOffers);
        Assert.NotEmpty(transportOffers!);

        // Now plan and send capability offer using existing plan node and send node
        var planNode = new PlanCapabilityOfferNode { Context = planningContext };
        planNode.SetLogger(NullLogger<PlanCapabilityOfferNode>.Instance);
        Assert.Equal(NodeStatus.Success, await planNode.Execute());

        var requestAfterTransport = planningContext.Get<MAS_BT.Nodes.Planning.ProcessChain.CapabilityRequestContext>("Planning.CapabilityRequest");
        var expectedConversationId = requestAfterTransport?.ConversationId ?? conversationId;
        Assert.Equal(productId, expectedConversationId);

        // Subscribe to proposal topic to capture outgoing proposal
        var proposalTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proposalTopic = $"/{ns}/ManufacturingSequence/Response";
        await planningClient.SubscribeAsync(proposalTopic);
        planningClient.OnMessage(msg =>
        {
            if (proposalTcs.Task.IsCompleted)
            {
                return;
            }

            if (!string.Equals(msg?.Frame?.Type, I40MessageTypes.PROPOSAL, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(expectedConversationId) &&
                !string.Equals(msg?.Frame?.ConversationId, expectedConversationId, StringComparison.Ordinal))
            {
                return;
            }

            proposalTcs.TrySetResult(msg);
        });

        var sendNode = new SendCapabilityOfferNode { Context = planningContext };
        sendNode.SetLogger(NullLogger<SendCapabilityOfferNode>.Instance);
        Assert.Equal(NodeStatus.Success, await sendNode.Execute());

        var completed = await Task.WhenAny(proposalTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(proposalTcs.Task, completed);
        var proposal = await proposalTcs.Task;
        Assert.NotNull(proposal);
        Assert.Equal(expectedConversationId, proposal!.Frame?.ConversationId);

        // Inspect proposal for OfferedCapability and its Action.InputParameters
        var offeredSequence = proposal.InteractionElements?.OfType<SubmodelElementList>()
            .FirstOrDefault(list => string.Equals(list.IdShort, "OfferedCapabilitySequence", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(offeredSequence);
        var firstOffered = offeredSequence!.OfType<SubmodelElementCollection>().FirstOrDefault();
        Assert.NotNull(firstOffered);
        var actions = firstOffered!.Values?.OfType<SubmodelElementList>()
            .FirstOrDefault(list => string.Equals(list.IdShort, "Actions", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(actions);
        var firstAction = actions!.OfType<SubmodelElementCollection>().FirstOrDefault();
        Assert.NotNull(firstAction);
        var inputParameters = firstAction!.Values?.OfType<SubmodelElementCollection>()
            .FirstOrDefault(smc => string.Equals(smc.IdShort, "InputParameters", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(inputParameters);

    }

    private static string? ResolveTransportProductId(I40Message? message)
    {
        var collection = message?.InteractionElements?.OfType<SubmodelElementCollection>().FirstOrDefault();
        if (collection == null)
        {
            return message?.Frame?.ConversationId;
        }

        var parsed = new TransportRequestMessage(collection);
        return parsed.IdentifierValueText ?? message?.Frame?.ConversationId;
    }

    private static Dictionary<string, ManufacturingSequence> BuildManufacturingSequenceIndex(string productId)
    {
        var sequence = new ManufacturingSequence();
        var requiredCapability = new ManufacturingRequiredCapability("RequiredCapability_TransportSource");
        requiredCapability.SetInstanceIdentifier("req-transport-source");

        var offeredSequence = new ManufacturingOfferedCapabilitySequence();
        var capability = new OfferedCapability("OfferedCapability");
        capability.InstanceIdentifier.Value = new PropertyValue<string>($"cap-storage-{Guid.NewGuid():N}");
        capability.Station.Value = new PropertyValue<string>("SourceStation");

        var action = new ActionModel(
            idShort: "Action_Storage",
            actionTitle: "Storage",
            status: ActionStatusEnum.PLANNED,
            inputParameters: null,
            finalResultData: null,
            preconditions: null,
            skillReference: null,
            machineName: "SourceStation");
        action.Postconditions.AddPostcondition(new StoragePostcondition("Condition_Storage"));
        capability.AddAction(action);

        offeredSequence.AddCapability(capability);
        requiredCapability.AddSequence(offeredSequence);
        sequence.AddRequiredCapability(requiredCapability);

        return new Dictionary<string, ManufacturingSequence>(StringComparer.OrdinalIgnoreCase)
        {
            [productId] = sequence
        };
    }

    private sealed class TestPassthroughCapabilityReferenceQuery : ICapabilityReferenceQuery
    {
        public Task<string?> GetCapabilityReferenceJsonAsync(string moduleShellId, string capabilityIdShort, System.Threading.CancellationToken cancellationToken = default)
        {
            var json = $"[{{\"type\":\"GlobalReference\",\"value\":\"capability:{capabilityIdShort}\"}}]";
            return Task.FromResult<string?>(json);
        }
    }
}
