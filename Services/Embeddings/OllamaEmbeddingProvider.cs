using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MAS_BT.Tools;

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

        var json = JsonFacade.Serialize(new System.Collections.Generic.Dictionary<string, object?>
        {
            ["model"] = Model,
            ["prompt"] = text
        });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await HttpClient.PostAsync(
                new Uri(new Uri(Endpoint), "/api/embeddings"),
                content,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonFacade.Parse(responseJson);
            var embeddingNode = JsonFacade.GetPath(parsed, new[] { "embedding" });
            if (embeddingNode is not System.Collections.Generic.IList<object?> list)
            {
                return null;
            }

            var embedding = new double[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                if (!JsonFacade.TryToDouble(list[i], out var d))
                {
                    return null;
                }

                embedding[i] = d;
            }

            return embedding;
        }
        catch
        {
            return null;
        }
    }
}
