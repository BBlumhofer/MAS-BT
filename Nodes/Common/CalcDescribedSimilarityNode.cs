using Microsoft.Extensions.Logging;
using System.Text;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using AasSharpClient.Models.Helpers;
using BaSyx.Models.AdminShell;
using MAS_BT.Tools;

namespace MAS_BT.Nodes.Common;

/// <summary>
/// Receives a request on CalcDescribedSimilarity and uses an LLM (Ollama) to generate
/// technical descriptions for each incoming capability property. Then publishes a new
/// calcSimilarity message to the CalcSimilarity topic so the existing similarity pipeline can process it.
/// </summary>
public class CalcDescribedSimilarityNode : BTNode
{
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public CalcDescribedSimilarityNode() : base("CalcDescribedSimilarity") { }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var requestMessage = Context.Get<I40Message>("CurrentMessage")
                               ?? Context.Get<I40Message>("LastReceivedMessage");
            if (requestMessage == null)
            {
                Logger.LogError("CalcDescribedSimilarity: CurrentMessage not found in context");
                return NodeStatus.Failure;
            }

            var elements = requestMessage.InteractionElements;
            if (elements == null || elements.Count == 0)
            {
                Logger.LogError("CalcDescribedSimilarity: No InteractionElements found in request");
                return NodeStatus.Failure;
            }

            // Most flows expect exactly two capabilities.
            if (elements.Count != 2)
            {
                Logger.LogWarning("CalcDescribedSimilarity: Expected exactly 2 InteractionElements, got {Count}", elements.Count);
                return NodeStatus.Failure;
            }

            var endpoint = ResolvePlaceholders(OllamaEndpoint);
            var model = ResolvePlaceholders(Model);

            var desc1 = await GenerateDescription(endpoint, model, elements[0]);
            var desc2 = await GenerateDescription(endpoint, model, elements[1]);

            if (string.IsNullOrWhiteSpace(desc1) || string.IsNullOrWhiteSpace(desc2))
            {
                Logger.LogError("CalcDescribedSimilarity: LLM returned empty descriptions");
                return NodeStatus.Failure;
            }

            Context.Set("Description1_Result", desc1);
            Context.Set("Description2_Result", desc2);

            // Keep the original request for response routing (sender/conv).
            Context.Set("ResponseTargetMessage", requestMessage);

            // Build a new calcSimilarity message that targets our own CalcSimilarity flow.
            var ns = Context.Get<string>("Namespace") ?? Context.Get<string>("config.Namespace") ?? "phuket";
            var agentId = Context.Get<string>("AgentId") ?? Context.AgentId;
            if (string.IsNullOrWhiteSpace(agentId))
            {
                Logger.LogError("CalcDescribedSimilarity: AgentId is not set");
                return NodeStatus.Failure;
            }

            var conv = requestMessage.Frame?.ConversationId;
            if (string.IsNullOrWhiteSpace(conv))
            {
                conv = requestMessage.Frame?.ConversationId ?? Guid.NewGuid().ToString();
            }

            // Prepare a synthetic calcSimilarity message that contains the descriptions as values.
            // This message is NOT published; it is used as input for CalcEmbedding/CalcCosineSimilarity.
            var builder = new I40MessageBuilder()
                .From(agentId, "AIAgent")
                .To(agentId, "AIAgent")
                .WithType("calcSimilarity")
                .WithConversationId(conv);

            builder.AddElement(BuildDescriptionProperty("Capability_0", desc1));
            builder.AddElement(BuildDescriptionProperty("Capability_1", desc2));

            var describedSimilarityMessage = builder.Build();
            Context.Set("CurrentMessage", describedSimilarityMessage);
            Context.Set("LastReceivedMessage", describedSimilarityMessage);

            Logger.LogInformation(
                "CalcDescribedSimilarity: Prepared described calcSimilarity input (Conv={Conv}) for in-process similarity calculation",
                describedSimilarityMessage.Frame?.ConversationId);

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CalcDescribedSimilarity: Exception occurred");
            return NodeStatus.Failure;
        }
    }

    private static Property<string> BuildDescriptionProperty(string idShort, string description)
    {
        return new Property<string>(idShort)
        {
            Kind = ModelingKind.Instance,
            Value = new PropertyValue<string>(description)
        };
    }

    private async Task<string> GenerateDescription(string endpoint, string model, ISubmodelElement element)
    {
        var valueOnly = ExtractTextFromElement(element);
        var elementJson = JsonFacade.Serialize(new { value = valueOnly });
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

        var json = JsonFacade.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Logger.LogInformation("CalcDescribedSimilarity: Calling Ollama generate (model={Model})", model);

        var response = await _httpClient.PostAsync($"{endpoint}/api/generate", content, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cts.Token);
            Logger.LogError("CalcDescribedSimilarity: Ollama API returned error {Status}: {Error}", response.StatusCode, errorContent);
            return string.Empty;
        }

        var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
        var root = JsonFacade.Parse(responseJson);
        var responseText = JsonFacade.GetPathAsString(root, new[] { "response" }) ?? string.Empty;
        return responseText.Trim();
    }

    private string BuildSimplePropertyJson(ISubmodelElement element)
    {
        var value = ExtractTextFromElement(element);
        var idShort = element.IdShort ?? "Capability";

        var kind = "Instance";
        try
        {
            // Most BaSyx SubmodelElements carry Kind. Keep best-effort.
            if (element is SubmodelElement sme)
            {
                kind = sme.Kind == ModelingKind.Template ? "Template" : "Instance";
            }
        }
        catch
        {
            // ignore
        }

        var payload = new
        {
            idShort,
            kind,
            modelType = "Property",
            valueType = "string",
            value
        };

        return JsonFacade.Serialize(payload);
    }

    private string ExtractTextFromElement(ISubmodelElement element)
    {
        if (element is IProperty property)
        {
            return property.GetText() ?? string.Empty;
        }

        return element.IdShort ?? string.Empty;
    }

}
