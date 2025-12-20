using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AasSharpClient.Models.Messages;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Serialization;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MAS_BT.Tests.Integration
{
    public class SimplePlanningHolonTests
    {
        [Fact]
        public async Task PlanningHolon_RespondsTo_CfP_Request()
        {
            var ns = "_PHUKET";

            var plannerClient = await MAS_BT.Tests.TestHelpers.TestTransportFactory.CreateClientAsync($"/{ns}/planning-logs", "planning-client");
            var listener = await MAS_BT.Tests.TestHelpers.TestTransportFactory.CreateClientAsync($"/{ns}/collector", "collector-client");

            var responses = new BlockingCollection<I40Message>();
            await listener.SubscribeAsync($"/{ns}/Planning/OfferedCapability/Response");
            listener.OnMessage(msg =>
            {
                try { responses.Add(msg); } catch { }
            });

            // Build BTContext for planner
            var context = new BTContext();
            context.Set("config.Namespace", ns);
            // Run the planning holon as P101 so CfP addressed to P101 is accepted
            context.Set("AgentId", "P101");
            context.Set("config.Agent.AgentId", "P101");
            context.Set("config.Agent.Role", "PlanningHolon");
            // Ensure module id is available for topic resolution
            context.Set("ModuleId", "P101");
            context.Set("config.Agent.ModuleId", "P101");
            context.Set("MessagingClient", plannerClient);

            // Prepare logger infrastructure
            using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information));

            var registry = new NodeRegistry(loggerFactory.CreateLogger<NodeRegistry>());
            var deserializer = new XmlTreeDeserializer(registry, loggerFactory);

            // Try several relative paths to locate the BT file when running under test runner
            // Walk upwards from base directory to find the Trees folder
            string? treePath = null;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir != null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "Trees", "PlanningAgent.bt.xml");
                if (File.Exists(candidate))
                {
                    treePath = candidate;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.True(!string.IsNullOrWhiteSpace(treePath), "BT file PlanningAgent.bt.xml not found in parent tree search");
            var root = deserializer.Deserialize(treePath!, context);

            // Run the BT in background
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var runTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var status = await root.Execute();
                    if (status == NodeStatus.Success || status == NodeStatus.Failure)
                    {
                        break;
                    }
                    await Task.Delay(50);
                }
            }, cts.Token);

            // Give planner a moment to subscribe and register
            await Task.Delay(1000);

            // Publish a CfP as dispatcher by loading provided JSON test file
            // Locate the test JSON by walking up from the base directory (works when tests run from bin folder)
            string? jsonPath = null;
            var searchDir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && searchDir != null; i++)
            {
                var candidate = Path.Combine(searchDir.FullName, "tests", "TestFiles", "OfferedCapabilityRequest.json");
                if (File.Exists(candidate))
                {
                    jsonPath = candidate;
                    break;
                }
                searchDir = searchDir.Parent;
            }

            if (string.IsNullOrWhiteSpace(jsonPath))
            {
                // last resort: repo-relative
                jsonPath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "TestFiles", "OfferedCapabilityRequest.json");
            }

            var json = File.ReadAllText(jsonPath);

            // Parse frame and interactionElements into concrete I40Message with BaSyx submodel elements
            using var jsonDoc = System.Text.Json.JsonDocument.Parse(json);
            var jsonRoot = jsonDoc.RootElement;
            var frameElem = jsonRoot.GetProperty("frame");
            var frame = System.Text.Json.JsonSerializer.Deserialize<I40Sharp.Messaging.Models.MessageFrame>(frameElem.GetRawText(), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var message = new I40Sharp.Messaging.Models.I40Message();
            if (frame != null) message.Frame = frame;

            if (jsonRoot.TryGetProperty("interactionElements", out var elems) && elems.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var options = BaSyx.Models.Extensions.DefaultJsonSerializerOptions.CreateDefaultJsonSerializerOptions();
                foreach (var el in elems.EnumerateArray())
                {
                    try
                    {
                        var sme = System.Text.Json.JsonSerializer.Deserialize<BaSyx.Models.AdminShell.ISubmodelElement>(el.GetRawText(), options);
                        if (sme != null) message.InteractionElements.Add(sme);
                    }
                    catch
                    {
                        // ignore individual element deserialization failures
                    }
                }
            }

            var i40msg = message;
            Assert.NotNull(i40msg.Frame);
            var conv = i40msg.Frame?.ConversationId ?? $"urn:test:{Guid.NewGuid():N}";

            var publishTopic = $"/{ns}/Planning/OfferedCapability/Request";
            await listener.PublishAsync(i40msg, publishTopic);

            // Wait for response (allow longer for registration/subscriptions)
            I40Message? resp = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (responses.TryTake(out resp, 500)) break;
            }

            cts.Cancel();
            try { await runTask; } catch { }

            Assert.NotNull(resp);
            Assert.Equal(conv, resp.Frame?.ConversationId);
        }
    }
}
