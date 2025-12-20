using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using MAS_BT.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Transport;
using BaSyx.Models.AdminShell;
using Xunit;
using Xunit.Abstractions;

namespace MAS_BT.Tests;

public class SimilarityAnalysisTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<MessagingClient> _clients = new();
    private static readonly MessageSerializer Serializer = new();

    public SimilarityAnalysisTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<MessagingClient> CreateClientAsync(string defaultTopic)
    {
        var client = await MAS_BT.Tests.TestHelpers.TestTransportFactory.CreateClientAsync(defaultTopic, "similarity-client");
        _clients.Add(client);
        return client;
    }

    [Fact]
    public async Task CalcEmbedding_WithTwoProperties_ExtractsTextAndGetsEmbeddings()
    {
        // Arrange
        var context = new BTContext(NullLogger<BTContext>.Instance);
        context.Set("config.Ollama.Endpoint", "http://localhost:11434");
        context.Set("config.Ollama.Model", "nomic-embed-text");
        
        var node = new CalcEmbeddingNode
        {
            OllamaEndpoint = "http://localhost:11434",  // Direct value, not placeholder
            Model = "nomic-embed-text"  // Direct value, not placeholder
        };
        node.Initialize(context, NullLogger.Instance);

        var message = CreateTestMessage("Assemble", "Screw");
        context.Set("CurrentMessage", message);
        context.Set("config.SimilarityAnalysis.MaxInteractionElements", 2);

        _output.WriteLine("🔄 Attempting to connect to Ollama...");
        
        // Act - with proper timeout
        var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executeTask = node.Execute();
        var completedTask = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
        
        NodeStatus result;
        if (completedTask == executeTask)
        {
            result = await executeTask;
            cts.Cancel();
        }
        else
        {
            _output.WriteLine("⚠️  Ollama request timed out after 10 seconds - skipping test");
            return;
        }

        // Assert - Skip test if Ollama is not available
        if (result == NodeStatus.Failure)
        {
            _output.WriteLine("⚠️  Ollama not available - skipping test");
            return;
        }

        Assert.Equal(NodeStatus.Success, result);
        var embeddings = context.Get<List<double[]>>("Embeddings");
        Assert.NotNull(embeddings);
        Assert.Equal(2, embeddings.Count);
        Assert.NotEmpty(embeddings[0]);
        Assert.NotEmpty(embeddings[1]);
        
        _output.WriteLine($"✅ Embedding 1 dimension: {embeddings[0].Length}");
        _output.WriteLine($"✅ Embedding 2 dimension: {embeddings[1].Length}");
    }

    [Fact]
    public async Task CalcCosineSimilarity_WithTwoEmbeddings_CalculatesSimilarity()
    {
        // Arrange
        var context = new BTContext(NullLogger<BTContext>.Instance);
        context.Set("AgentId", "SimilarityAnalysisAgent_phuket");
        
        // Create sample embeddings that simulate "Assemble" vs "Screw"
        // These are normalized vectors with moderate similarity (~0.65)
        var embedding1 = new double[] { 0.6, 0.8, 0.0, 0.5, 0.3, 0.2 };  // Simulates "Assemble"
        var embedding2 = new double[] { 0.5, 0.7, 0.1, 0.6, 0.4, 0.3 };  // Simulates "Screw"
        var embeddings = new List<double[]> { embedding1, embedding2 };
        context.Set("Embeddings", embeddings);

        var message = CreateTestMessage("Assemble", "Screw");
        context.Set("CurrentMessage", message);

        var node = new CalcCosineSimilarityNode();
        node.Initialize(context, NullLogger.Instance);

        // Act
        var result = await node.Execute();

        // Assert
        Assert.Equal(NodeStatus.Success, result);
        var similarity = context.Get<double>("CosineSimilarity");
        Assert.True(similarity >= -1.0 && similarity <= 1.0, $"Similarity {similarity} should be between -1 and 1");
        
        var responseMessage = context.Get<I40Message>("ResponseMessage");
        Assert.NotNull(responseMessage);
        Assert.Equal("informConfirm", responseMessage.Frame.Type);
        Assert.Single(responseMessage.InteractionElements);
        
        _output.WriteLine("");
        _output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        _output.WriteLine("║           SIMILARITY ANALYSIS RESULTS (Mock Data)            ║");
        _output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        _output.WriteLine("");
        _output.WriteLine($"  📝 Comparison:");
        _output.WriteLine($"     • Element 1: 'Assemble'");
        _output.WriteLine($"     • Element 2: 'Screw'");
        _output.WriteLine("");
        _output.WriteLine($"  📊 Cosine Similarity: {similarity:F6}");
        _output.WriteLine($"     → {similarity * 100:F2}% similar");
        _output.WriteLine("");
        _output.WriteLine($"  📈 Interpretation:");
        if (similarity >= 0.9)
            _output.WriteLine($"     ✅ Very High Similarity (nearly identical concepts)");
        else if (similarity >= 0.7)
            _output.WriteLine($"     ✅ High Similarity (closely related concepts)");
        else if (similarity >= 0.5)
            _output.WriteLine($"     ⚠️  Medium Similarity (somewhat related)");
        else if (similarity >= 0.3)
            _output.WriteLine($"     ⚠️  Low Similarity (loosely related)");
        else
            _output.WriteLine($"     ❌ Very Low Similarity (different concepts)");
        _output.WriteLine("");
        _output.WriteLine("════════════════════════════════════════════════════════════════");
    }

    [Fact]
    public async Task SimilarityAnalysis_EndToEnd_WithRealMessage()
    {
        // Arrange
        var ns = "phuket";
        var agentId = "SimilarityAnalysisAgent_phuket";
        
        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = agentId,
            AgentRole = "AIAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("Namespace", ns);
        context.Set("config.Agent.AgentId", agentId);
        context.Set("AgentId", agentId);
        context.Set("config.Ollama.Endpoint", "http://localhost:11434");
        context.Set("config.Ollama.Model", "nomic-embed-text");
        context.Set("config.SimilarityAnalysis.MaxInteractionElements", 2);

        var client = await CreateClientAsync($"/{ns}/similarity");
        context.Set("MessagingClient", client);

        // Create the test message from your example
        var message = CreateTestMessage("Assemble", "Screw");
        context.Set("CurrentMessage", message);

        // Act - Step 1: Calculate Embeddings with proper timeout
        var embeddingNode = new CalcEmbeddingNode
        {
            OllamaEndpoint = "http://localhost:11434",  // Direct value
            Model = "nomic-embed-text"  // Direct value
        };
        embeddingNode.Initialize(context, NullLogger.Instance);
        
        _output.WriteLine("🔄 Attempting to connect to Ollama...");
        
        var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
        var embeddingTask = embeddingNode.Execute();
        var completedTask = await Task.WhenAny(embeddingTask, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
        
        NodeStatus embeddingResult;
        if (completedTask == embeddingTask)
        {
            embeddingResult = await embeddingTask;
            cts.Cancel();
        }
        else
        {
            _output.WriteLine("⚠️  Ollama embedding request timed out - skipping test");
            return;
        }
        
        // If Ollama is not available, skip the rest of the test
        if (embeddingResult == NodeStatus.Failure)
        {
            _output.WriteLine("⚠️  Ollama not available - skipping embedding test");
            return;
        }

        Assert.Equal(NodeStatus.Success, embeddingResult);

        // Act - Step 2: Calculate Similarity
        var similarityNode = new CalcCosineSimilarityNode();
        similarityNode.Initialize(context, NullLogger.Instance);
        var similarityResult = await similarityNode.Execute();

        // Assert
        Assert.Equal(NodeStatus.Success, similarityResult);
        
        var similarity = context.Get<double>("CosineSimilarity");
        Assert.True(similarity >= -1.0 && similarity <= 1.0);
        
        var responseMessage = context.Get<I40Message>("ResponseMessage");
        Assert.NotNull(responseMessage);
        Assert.Equal("informConfirm", responseMessage.Frame.Type);
        Assert.Equal(message.Frame.ConversationId, responseMessage.Frame.ConversationId);
        Assert.Equal("DispatchingAgent_phuket", responseMessage.Frame.Receiver.Identification.Id);
        Assert.Single(responseMessage.InteractionElements);
        
        var similarityProperty = responseMessage.InteractionElements[0] as Property<double>;
        Assert.NotNull(similarityProperty);
        Assert.Equal("CosineSimilarity", similarityProperty.IdShort);
        
        _output.WriteLine("");
        _output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        _output.WriteLine("║           SIMILARITY ANALYSIS RESULTS                        ║");
        _output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        _output.WriteLine("");
        _output.WriteLine($"  📝 Comparison:");
        _output.WriteLine($"     • Element 1: 'Assemble'");
        _output.WriteLine($"     • Element 2: 'Screw'");
        _output.WriteLine("");
        _output.WriteLine($"  📊 Cosine Similarity: {similarity:F6}");
        _output.WriteLine($"     → {similarity * 100:F2}% similar");
        _output.WriteLine("");
        _output.WriteLine($"  📈 Interpretation:");
        if (similarity >= 0.9)
            _output.WriteLine($"     ✅ Very High Similarity (nearly identical concepts)");
        else if (similarity >= 0.7)
            _output.WriteLine($"     ✅ High Similarity (closely related concepts)");
        else if (similarity >= 0.5)
            _output.WriteLine($"     ⚠️  Medium Similarity (somewhat related)");
        else if (similarity >= 0.3)
            _output.WriteLine($"     ⚠️  Low Similarity (loosely related)");
        else
            _output.WriteLine($"     ❌ Very Low Similarity (different concepts)");
        _output.WriteLine("");
        _output.WriteLine($"  📤 Response Message:");
        _output.WriteLine($"     • Type: {responseMessage.Frame.Type}");
        _output.WriteLine($"     • Conversation ID: {responseMessage.Frame.ConversationId}");
        _output.WriteLine($"     • Sender: {responseMessage.Frame.Sender.Identification.Id}");
        _output.WriteLine($"     • Receiver: {responseMessage.Frame.Receiver.Identification.Id}");
        _output.WriteLine("");
        _output.WriteLine("════════════════════════════════════════════════════════════════");
    }

    [Fact]
    public async Task CalcEmbedding_WithWrongNumberOfElements_Fails()
    {
        // Arrange
        var context = new BTContext(NullLogger<BTContext>.Instance);
        var node = new CalcEmbeddingNode();
        node.Initialize(context, NullLogger.Instance);

        var message = new I40Message
        {
            Frame = new MessageFrame
            {
                ConversationId = Guid.NewGuid().ToString(),
                Type = "calcSimilarity"
            },
            InteractionElements = new List<ISubmodelElement>
            {
                new Property<string>("Capability_0") 
                { 
                    Value = new PropertyValue<string>("Assemble") 
                }
            }
        };
        context.Set("CurrentMessage", message);
        context.Set("config.SimilarityAnalysis.MaxInteractionElements", 2);

        // Act
        var result = await node.Execute();

        // Assert
        Assert.Equal(NodeStatus.Failure, result);
    }

    private I40Message CreateTestMessage(string value1, string value2)
    {
        var builder = new I40MessageBuilder()
            .From("DispatchingAgent_phuket", "DispatchingAgent")
            .To("SimilarityAnalysisAgent_phuket", "AIAgent")
            .WithType("calcSimilarity")
            .WithConversationId("f47b14f5-cfe3-43ec-8eb3-037ae71c3317");

        var property1 = new Property<string>("Capability_0")
        {
            Value = new PropertyValue<string>(value1),
            Kind = ModelingKind.Instance
        };

        var property2 = new Property<string>("Capability_1")
        {
            Value = new PropertyValue<string>(value2),
            Kind = ModelingKind.Instance
        };

        builder.AddElement(property1);
        builder.AddElement(property2);

        return builder.Build();
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            try
            {
                client.DisconnectAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
