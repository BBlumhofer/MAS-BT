using System;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

var cap1 = args.Length > 0 ? args[0] : "Assemble";
var cap2 = args.Length > 1 ? args[1] : "Screw";

Console.WriteLine("Sending similarity request: {0} vs {1}", cap1, cap2);

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
var logger = loggerFactory.CreateLogger<Program>();

var client = new MessagingClient(new MqttTransport("localhost", 1883), logger: logger);
await client.ConnectAsync();

// Derive namespace from environment variable `NAMESPACE` or use default 'phuket'
var nsEnv = Environment.GetEnvironmentVariable("NAMESPACE") ?? "phuket";
var ns = nsEnv.TrimStart('_').ToLower();

var builder = new I40MessageBuilder()
    .From($"DispatchingAgent_{ns}", "DispatchingAgent")
    .To($"SimilarityAnalysisAgent_{ns}", "AIAgent")
    .WithType("calcSimilarity")
    .WithConversationId(Guid.NewGuid().ToString());

builder.AddElement(new Property<string>("Capability_0") 
{ 
    Value = new PropertyValue<string>(cap1), 
    Kind = ModelingKind.Instance 
});

builder.AddElement(new Property<string>("Capability_1") 
{ 
    Value = new PropertyValue<string>(cap2), 
    Kind = ModelingKind.Instance 
});

await client.PublishAsync(builder.Build(), $"/{ns}/SimilarityAnalysisAgent_{ns}/CalcSimilarity");

Console.WriteLine("Message sent! Check agent for response.");
await Task.Delay(2000);
await client.DisconnectAsync();
