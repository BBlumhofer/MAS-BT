using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MAS_BT.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Common;

/// <summary>
/// Generates a single technical description for the first InteractionElement of the request
/// and stores the result in context under `Description_Result` and `ResponseTargetMessage`.
/// </summary>
public class CreateDescriptionNode : BTNode
{
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3";

    private const string DescriptionCacheKey = "Similarity.DescriptionCache";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public CreateDescriptionNode() : base("CreateDescription") { }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var requestMessage = Context.Get<I40Sharp.Messaging.Models.I40Message>("CurrentMessage")
                               ?? Context.Get<I40Sharp.Messaging.Models.I40Message>("LastReceivedMessage");
            if (requestMessage == null)
            {
                Logger.LogError("CreateDescription: CurrentMessage not found in context");
                return NodeStatus.Failure;
            }

            var elements = requestMessage.InteractionElements;
            if (elements == null || elements.Count == 0)
            {
                Logger.LogError("CreateDescription: No InteractionElements found in request");
                return NodeStatus.Failure;
            }

            var element = elements[0];
            var endpoint = ResolvePlaceholders(OllamaEndpoint);
            var model = ResolvePlaceholders(Model);

            var capabilityKey = TryExtractCapabilityKey(elements);
            if (!string.IsNullOrWhiteSpace(capabilityKey))
            {
                var cache = Context.Get<System.Collections.Concurrent.ConcurrentDictionary<string, string>>(DescriptionCacheKey);
                if (cache == null)
                {
                    cache = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    Context.Set(DescriptionCacheKey, cache);
                }

                if (cache.TryGetValue(capabilityKey, out var cachedDesc) && !string.IsNullOrWhiteSpace(cachedDesc))
                {
                    Context.Set("Description_Result", cachedDesc);
                    Context.Set("CreateDescriptionTargetMessage", requestMessage);
                    Logger.LogInformation("CreateDescription: Cache hit for capability {Capability}", capabilityKey);
                    return NodeStatus.Success;
                }
            }

            var desc = await GenerateDescription(endpoint, model, element);
            if (string.IsNullOrWhiteSpace(desc))
            {
                Logger.LogError("CreateDescription: LLM returned empty description");
                return NodeStatus.Failure;
            }

            if (!string.IsNullOrWhiteSpace(capabilityKey))
            {
                var cache = Context.Get<System.Collections.Concurrent.ConcurrentDictionary<string, string>>(DescriptionCacheKey)
                           ?? new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                cache[capabilityKey] = desc;
                Context.Set(DescriptionCacheKey, cache);
            }

            Context.Set("Description_Result", desc);
            // Use a dedicated key so parallel Similarity flows don't overwrite each other's response correlation.
            Context.Set("CreateDescriptionTargetMessage", requestMessage);

            Logger.LogInformation("CreateDescription: Generated description for element {IdShort}", element.IdShort);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CreateDescription: Exception occurred");
            return NodeStatus.Failure;
        }
    }

    private static string? TryExtractCapabilityKey(System.Collections.Generic.IList<ISubmodelElement> elements)
    {
        if (elements == null || elements.Count == 0)
        {
            return null;
        }

        foreach (var el in elements)
        {
            var idShort = el?.IdShort;
            if (string.IsNullOrWhiteSpace(idShort))
            {
                continue;
            }

            // Current contract: DispatchingAgent sends Property("Capability_0", <capabilityName>)
            // Be tolerant and accept other common variants.
            if (string.Equals(idShort, "Capability_0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(idShort, "Capability", StringComparison.OrdinalIgnoreCase)
                || idShort.StartsWith("Capability_", StringComparison.OrdinalIgnoreCase))
            {
                var value = TryExtractPropertyValue(el);
                var key = value?.Trim();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
        }

        return null;
    }

    private static string? TryExtractPropertyValue(ISubmodelElement? element)
    {
        if (element == null)
        {
            return null;
        }

        if (element is Property<string> stringProperty && stringProperty.Value != null)
        {
            return stringProperty.Value.Value?.ToString();
        }

        if (element is BaSyx.Models.AdminShell.Property property)
        {
            try
            {
                static object? GetParameterlessProperty(object obj, string name)
                {
                    var props = obj.GetType().GetProperties()
                        .Where(p => string.Equals(p.Name, name, StringComparison.Ordinal)
                                    && p.GetIndexParameters().Length == 0)
                        .ToArray();

                    return props.Length == 1 ? props[0].GetValue(obj) : props.FirstOrDefault()?.GetValue(obj);
                }

                var value = GetParameterlessProperty(property, "Value");
                if (value != null)
                {
                    var innerValue = GetParameterlessProperty(value, "Value");
                    return innerValue?.ToString() ?? value.ToString();
                }
            }
            catch
            {
                // ignore; caller will fall back
            }
        }

        return null;
    }

    private async Task<string> GenerateDescription(string endpoint, string model, ISubmodelElement element)
    {
        var valueOnly = ExtractTextFromElement(element);
        var elementJson = JsonSerializer.Serialize(new { value = valueOnly }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        });

        var prompt = $@"Generate a precise, technically neutral description 
            The description must:
            - Contain exactly 30 words
            - Use formal, standardized technical language
            - Describe, in this exact order:
            1) The element’s classification within the data model
            2) The semantic meaning of the value explicitly as a manufacturing capability
            3) The operational relevance of this capability in manufacturing or automation systems
            5) Focus on the meaning of the Value elements  and don't  mention datatypes and other metdata 
            - Explicitly relate the value to modelType and kind
            - Do not interpret the value as a physical object, part, or inventory item
            - Avoid examples, lists, introductions, conclusions, or headings
            Return only the description text without an introduction or additional comments. Start with: Meaning:
            Element:
            {elementJson}";

        var requestBody = new
        {
            model,
            prompt,
            stream = false
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Logger.LogInformation("CreateDescription: Calling Ollama generate (model={Model})", model);
        var response = await _httpClient.PostAsync($"{endpoint}/api/generate", content, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cts.Token);
            Logger.LogError("CreateDescription: Ollama API returned error {Status}: {Error}", response.StatusCode, errorContent);
            return string.Empty;
        }

        var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
        var responseObj = JsonSerializer.Deserialize<OllamaGenerateResponse>(responseJson);
        return (responseObj?.Response ?? string.Empty).Trim();
    }

    private string ExtractTextFromElement(ISubmodelElement element)
    {
        if (element is Property<string> stringProperty && stringProperty.Value != null)
        {
            return stringProperty.Value.Value?.ToString() ?? string.Empty;
        }

        if (element is BaSyx.Models.AdminShell.Property property)
        {
            try
            {
                static object? GetParameterlessProperty(object obj, string name)
                {
                    var props = obj.GetType().GetProperties()
                        .Where(p => string.Equals(p.Name, name, StringComparison.Ordinal)
                                    && p.GetIndexParameters().Length == 0)
                        .ToArray();

                    return props.Length == 1 ? props[0].GetValue(obj) : props.FirstOrDefault()?.GetValue(obj);
                }

                var value = GetParameterlessProperty(property, "Value");
                if (value != null)
                {
                    var innerValue = GetParameterlessProperty(value, "Value");
                    return innerValue?.ToString() ?? value.ToString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "CreateDescription: Failed to extract value via reflection from {Type}", property.GetType().Name);
            }
        }

        return element.IdShort ?? string.Empty;
    }

    private class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }
    }
}
