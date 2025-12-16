using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Core;
using AasSharpClient.Models.Helpers;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Common;

public class CalcEmbeddingNode : BTNode
{
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "nomic-embed-text";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public CalcEmbeddingNode() : base("CalcEmbedding") { }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            Logger.LogInformation("CalcEmbedding: Starting embedding calculation");

            // Always capture the *current* request message at the start of this node.
            // Do NOT prefer an existing CalcSimilarityTargetMessage here, otherwise we'd keep using
            // the first request forever and conversationId would stop updating.
            var message = Context.Get<I40Message>("CurrentMessage")
                          ?? Context.Get<I40Message>("LastReceivedMessage");
            if (message == null)
            {
                Logger.LogError("CalcEmbedding: CurrentMessage not found in context");
                return NodeStatus.Failure;
            }

            // Capture the request message for downstream response-building nodes.
            // This prevents parallel BT branches from overwriting CurrentMessage while we await external calls.
            Context.Set("CalcSimilarityTargetMessage", message);
            // Backward compatibility: other nodes may still read this key.
            Context.Set("ResponseTargetMessage", message);

            var interactionElements = message.InteractionElements;
            if (interactionElements == null || interactionElements.Count == 0)
            {
                Logger.LogError("CalcEmbedding: No InteractionElements found in message");
                return NodeStatus.Failure;
            }

            var maxElements = ReadMaxInteractionElements();
            if (interactionElements.Count < 2)
            {
                Logger.LogWarning("CalcEmbedding: Need at least 2 InteractionElements, got {Count}", interactionElements.Count);
                return NodeStatus.Failure;
            }
            if (maxElements.HasValue && interactionElements.Count > maxElements.Value)
            {
                Logger.LogWarning("CalcEmbedding: Too many InteractionElements (max={Max}), got {Count}", maxElements.Value, interactionElements.Count);
                return NodeStatus.Failure;
            }

            var endpoint = ResolvePlaceholders(OllamaEndpoint);
            var model = ResolvePlaceholders(Model);

            var embeddings = new List<double[]>();
            var capabilityNames = new List<string>();

            foreach (var element in interactionElements)
            {
                Logger.LogDebug("CalcEmbedding: Processing element {IdShort}, Type: {Type}", 
                    element.IdShort, element.GetType().Name);
                    
                var extracted = ExtractTextFromElement(element);
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    Logger.LogWarning("CalcEmbedding: Could not extract text from InteractionElement {IdShort}", element.IdShort);
                    return NodeStatus.Failure;
                }

                // Use format "IdShort: <value>" for embeddings as requested
                var idShort = element.IdShort ?? "<unknown>";
                var text = $"{idShort}: {extracted}";
                Logger.LogDebug("CalcEmbedding: Extracted text for embedding: {Text}", text);

                var embedding = await GetEmbeddingFromOllama(endpoint, model, text);
                if (embedding == null || embedding.Length == 0)
                {
                    Logger.LogError("CalcEmbedding: Failed to get embedding from Ollama for text: {Text}", text);
                    return NodeStatus.Failure;
                }

                embeddings.Add(embedding);
                capabilityNames.Add(idShort);
                var previewText = text.Length > 50 ? text.Substring(0, 50) : text;
                Logger.LogInformation("CalcEmbedding: Generated embedding with dimension {Dim} for text: {Text}", 
                    embedding.Length, previewText);
            }

            Context.Set("Embeddings", embeddings);
            Context.Set("CapabilityNames", capabilityNames);
            Logger.LogInformation("CalcEmbedding: Successfully calculated {Count} embeddings", embeddings.Count);

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CalcEmbedding: Exception occurred");
            return NodeStatus.Failure;
        }
    }

    private string ExtractTextFromElement(ISubmodelElement element)
    {
        if (element is IProperty property)
        {
            return property.GetText() ?? string.Empty;
        }

        if (element is SubmodelElementCollection collection)
        {
            var texts = new List<string>();
            foreach (var child in collection.Values ?? Array.Empty<ISubmodelElement>())
            {
                texts.Add(ExtractTextFromElement(child));
            }
            return string.Join(" ", texts);
        }

        // Fallback: use IdShort
        Logger.LogWarning("CalcEmbedding: Could not extract value from element type {Type}, using IdShort", element.GetType().Name);
        return element.IdShort ?? string.Empty;
    }

    private int? ReadMaxInteractionElements()
    {
        const string key = "config.SimilarityAnalysis.MaxInteractionElements";

        try
        {
            // Fast path: already stored as int
            if (Context.Has(key))
            {
                var raw = Context.Get<object>(key);
                if (raw is int i) return i;
                if (raw is long l) return (int)l;

                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n)) return n;
                    if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var parsed)) return parsed;
                }
            }
        }
        catch
        {
            // best-effort
        }

        try
        {
            // Fallback: string conversion
            var s = Context.Get<string>(key);
            if (int.TryParse(s, out var parsed)) return parsed;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private async Task<double[]?> GetEmbeddingFromOllama(string endpoint, string model, string text)
    {
        try
        {
            var requestBody = new
            {
                model = model,
                prompt = text
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Increase timeout for Ollama
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            
            Logger.LogDebug("CalcEmbedding: Calling Ollama at {Endpoint} with model {Model}", endpoint, model);
            
            var response = await _httpClient.PostAsync($"{endpoint}/api/embeddings", content, cts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.LogError("CalcEmbedding: Ollama API returned error {Status}: {Error}", 
                    response.StatusCode, errorContent);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var responseObj = JsonSerializer.Deserialize<OllamaEmbeddingResponse>(responseJson);

            if (responseObj?.Embedding == null || responseObj.Embedding.Length == 0)
            {
                Logger.LogError("CalcEmbedding: Ollama returned empty embedding");
                return null;
            }

            Logger.LogDebug("CalcEmbedding: Successfully got embedding with {Dim} dimensions", responseObj.Embedding.Length);
            return responseObj.Embedding;
        }
        catch (TaskCanceledException ex)
        {
            Logger.LogError(ex, "CalcEmbedding: Timeout calling Ollama API");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "CalcEmbedding: HTTP error calling Ollama API - Is Ollama running?");
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CalcEmbedding: Unexpected error calling Ollama API");
            return null;
        }
    }

    private class OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public double[]? Embedding { get; set; }
    }
}
