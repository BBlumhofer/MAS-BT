using System.Collections.Generic;
using System.Linq;
using AasSharpClient.Models;
using ActionModel = AasSharpClient.Models.Action;
using MAS_BT.Services;
using Xunit;

namespace MAS_BT.Tests;

public class SkillRequestQueueTests
{
    [Fact]
    public void TryEnqueueStoresEnvelopeData()
    {
        var queue = new SkillRequestQueue();
        var envelope = CreateSampleEnvelope("conv_enq");

        var success = queue.TryEnqueue(envelope, out var length);

        Assert.True(success);
        Assert.Equal(1, length);
        Assert.Equal(1, queue.Count);

        var snapshot = queue.Snapshot().Single();
        Assert.Equal("conv_enq", snapshot.ConversationId);
        Assert.Equal("Retrieve", snapshot.ActionTitle);
        Assert.Equal("CA-Module", snapshot.MachineName);
        Assert.Equal("Module2_Planning_Agent", snapshot.SenderId);
        Assert.True(snapshot.InputParameters.ContainsKey("ProductId"));
        Assert.Equal("https://smartfactory.de/shells/test_product", snapshot.InputParameters["ProductId"].ToString());
        Assert.Equal(SkillRequestQueueState.Pending, snapshot.QueueState);
    }

    [Fact]
    public void TryEnqueueHonorsCapacity()
    {
        var queue = new SkillRequestQueue(capacity: 1);
        var first = CreateSampleEnvelope("conv_first");
        var second = CreateSampleEnvelope("conv_second", actionTitle: "Store");

        Assert.True(queue.TryEnqueue(first, out var lengthAfterFirst));
        Assert.Equal(1, lengthAfterFirst);

        Assert.False(queue.TryEnqueue(second, out var lengthAfterSecond));
        Assert.Equal(1, lengthAfterSecond);
        Assert.Equal(1, queue.Count);

        var snapshot = queue.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("conv_first", snapshot.First().ConversationId);
    }

    [Fact]
    public void TryStartNextMarksItemsRunningWithoutRemoving()
    {
        var queue = new SkillRequestQueue();
        var first = CreateSampleEnvelope("conv_a", actionTitle: "Retrieve");
        var second = CreateSampleEnvelope("conv_b", actionTitle: "Store");

        queue.TryEnqueue(first, out _);
        queue.TryEnqueue(second, out _);

        Assert.True(queue.TryStartNext(out var runningFirst));
        Assert.Equal("conv_a", runningFirst!.ConversationId);
        Assert.Equal(SkillRequestQueueState.Running, runningFirst.QueueState);

        Assert.True(queue.TryStartNext(out var runningSecond));
        Assert.Equal("conv_b", runningSecond!.ConversationId);
        Assert.Equal(SkillRequestQueueState.Running, runningSecond.QueueState);

        Assert.False(queue.TryStartNext(out _));

        var snapshot = queue.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.All(snapshot, item => Assert.Equal(SkillRequestQueueState.Running, item.QueueState));
    }

    [Fact]
    public void TryRemoveByConversationIdRemovesEntries()
    {
        var queue = new SkillRequestQueue();
        var first = CreateSampleEnvelope("conv_remove");
        var second = CreateSampleEnvelope("conv_keep", actionTitle: "Store");

        queue.TryEnqueue(first, out _);
        queue.TryEnqueue(second, out _);

        Assert.True(queue.TryRemoveByConversationId("conv_remove", out var removed));
        Assert.Equal("conv_remove", removed!.ConversationId);
        Assert.Equal(1, queue.Count);

        Assert.False(queue.TryRemoveByConversationId("does_not_exist", out _));
    }

    private static SkillRequestEnvelope CreateSampleEnvelope(string conversationId, string actionTitle = "Retrieve")
    {
        var inputParameters = new Dictionary<string, object>
        {
            { "ProductId", "https://smartfactory.de/shells/test_product" },
            { "RetrieveByProductID", true }
        };

        var actionModel = new ActionModel(
            idShort: "Action001",
            actionTitle: actionTitle,
            status: ActionStatusEnum.PLANNED,
            inputParameters: InputParameters.FromTypedValues(inputParameters),
            finalResultData: new FinalResultData(),
            preconditions: new Preconditions(),
            skillReference: new SkillReference(System.Array.Empty<(object Key, string Value)>()),
            machineName: "CA-Module");

        return new SkillRequestEnvelope(
            rawMessage: "{}",
            conversationId: conversationId,
            senderId: "Module2_Planning_Agent",
            receiverId: "Module2_Execution_Agent",
            actionId: "Action001",
            actionTitle: actionTitle,
            machineName: "CA-Module",
            actionStatus: "planned",
            inputParameters: inputParameters,
            actionModel: actionModel);
    }
}
