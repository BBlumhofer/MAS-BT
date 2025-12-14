using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MAS_BT.Services.Embeddings;

public sealed class OllamaEmbeddingProvider : ITextEmbeddingProvider
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public string Endpoint { get; }
    public string Model { get; }

    public OllamaEmbeddingProvider(string endpoint, string model)
    {
        Endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint;
        Model = string.IsNullOrWhiteSpace(model) ? "nomic-embed-text" : model;
    }

    public async Task<double[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<double>();
        }

        var request = new OllamaEmbeddingRequest
        {
            Model = Model,
            Prompt = text
        };

        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await HttpClient.PostAsync(
                new Uri(new Uri(Endpoint), "/api/embeddings"),
                content,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<OllamaEmbeddingResponse>(stream, cancellationToken: cancellationToken);
            return payload?.Embedding;
        }
        catch
        {
            return null;
        }
    }

    private sealed record OllamaEmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; init; } = string.Empty;
    }

    private sealed record OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public double[]? Embedding { get; init; }
    }
}
