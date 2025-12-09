using System;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

// Simple demo: show request/reply using ConversationId via MessagingClient
// Demo helper: call RequestReplyDemo.RunDemoAsync() from a separate test runner if needed.
public static class RequestReplyDemo
{
    public static async Task RunDemoAsync()
    {
        var broker = "localhost";
        var port = 1883;
        var topic = "demo/requestreply";

        var senderTransport = new MqttTransport(broker, port, "demo-sender");
        var receiverTransport = new MqttTransport(broker, port, "demo-receiver");

        var sender = new MessagingClient(senderTransport, topic);
        var receiver = new MessagingClient(receiverTransport, topic);

        Console.WriteLine("Connecting receiver...");
        await receiver.ConnectAsync();

        Console.WriteLine("Connecting sender...");
        await sender.ConnectAsync();

        // Create conversation on sender
        var conv = sender.CreateConversation(TimeSpan.FromSeconds(30));
        Console.WriteLine($"Sender created conversation: {conv}");

        // Receiver listens for messages belonging to this conversation and replies
        receiver.OnConversation(conv, msg =>
        {
            Console.WriteLine($"Receiver got request (conversation={conv}). Preparing reply...");
            // Build reply message
            var reply = new I40MessageBuilder()
                .From("demo-receiver", "ExecutionAgent")
                .To("demo-sender", "PlanningAgent")
                .WithType("inform")
                .WithConversationId(conv)
                .Build();

            // Fire-and-forget publish
            _ = Task.Run(async () =>
            {
                try
                {
                    await receiver.PublishAsync(reply, topic);
                    Console.WriteLine("Receiver published reply");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Receiver failed to publish reply: {ex.Message}");
                }
            });
        });

        // Sender subscribes for conversation replies
        var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.OnConversation(conv, m => tcs.TrySetResult(m));

        // Build request message
        var request = new I40MessageBuilder()
            .From("demo-sender", "PlanningAgent")
            .To("demo-receiver", "ExecutionAgent")
            .WithType("request")
            .WithConversationId(conv)
            .Build();

        Console.WriteLine("Sender publishing request...");
        await sender.PublishAsync(request, topic);

        Console.WriteLine("Sender waiting for reply...");
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed == tcs.Task)
        {
            var reply = await tcs.Task;
            Console.WriteLine("Sender received reply. Demo complete.");
        }
        else
        {
            Console.WriteLine("Sender timed out waiting for reply.");
        }

        // cleanup
        sender.Dispose();
        receiver.Dispose();
    }
}
