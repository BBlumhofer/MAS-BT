using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Messages;
using AasSharpClient.Models.Helpers;
using BaSyx.Models.AdminShell;
using BaSyx.Models.Extensions;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// Sends a product summary log message over MQTT using ProductIdentification and BillOfMaterial submodels.
/// </summary>
public class SendProductSummaryLogNode : BTNode
{
    public string Topic { get; set; } = string.Empty;
    public string LogLevel { get; set; } = "INFO";

    public SendProductSummaryLogNode() : base("SendProductSummaryLog")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogError("SendProductSummaryLog: MessagingClient not available in context");
            return NodeStatus.Failure;
        }

        var shell = Context.Get<IAssetAdministrationShell>("AAS.Shell");
        if (shell == null)
        {
            Logger.LogError("SendProductSummaryLog: AssetAdministrationShell missing from context");
            return NodeStatus.Failure;
        }

        var productIdentification = Context.Get<ProductIdentificationSubmodel>("ProductIdentificationSubmodel")
            ?? Context.Get<ProductIdentificationSubmodel>("AAS.Submodel.ProductIdentification");
        if (productIdentification == null)
        {
            Logger.LogError("SendProductSummaryLog: ProductIdentification submodel not loaded");
            return NodeStatus.Failure;
        }

        var billOfMaterial = Context.Get<BillOfMaterialSubmodel>("BillOfMaterialSubmodel")
            ?? Context.Get<BillOfMaterialSubmodel>("AAS.Submodel.BillOfMaterial");
        var capabilityDescription = Context.Get<CapabilityDescriptionSubmodel>("CapabilityDescriptionSubmodel")
            ?? Context.Get<CapabilityDescriptionSubmodel>("AAS.Submodel.CapabilityDescription");

        var shellId = shell.Id?.Id ?? Context.AgentId ?? "UnknownShell";
        var agentType = Context.Get<string>("config.Agent.AgentType") ?? "ProductHolon";
        var senderId = NormalizeIdentifier(shellId, agentType);
        var agentRole = string.IsNullOrWhiteSpace(Context.AgentRole) ? agentType : Context.AgentRole;
        var agentState = Context.Get<string>("ProductState") ?? string.Empty;

        var productFamily = productIdentification.GetProductFamilyName() ?? string.Empty;
        var orderNumber = productIdentification.GetOrderNumber() ?? string.Empty;
        var rootOrderNumber = GetSubmodelString(productIdentification, "RootOrderNumber") ?? orderNumber;
        var orderTimeIso = GetOrderTimeIso(productIdentification) ?? DateTime.UtcNow.ToString("o");
        var childIds = billOfMaterial != null
            ? CollectChildProductIds(billOfMaterial)
                .Where(id => !string.Equals(id, shellId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
        var childCount = childIds.Count;
        var requiredCapabilities = ExtractCapabilityNames(capabilityDescription)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requiredCapabilityCount = requiredCapabilities.Count;

        if (billOfMaterial == null)
        {
            Logger.LogWarning("SendProductSummaryLog: BillOfMaterial submodel missing. Child product list will be empty.");
        }
        if (capabilityDescription == null)
        {
            Logger.LogWarning("SendProductSummaryLog: CapabilityDescription submodel missing. Required capabilities will be empty.");
        }

        var summary = BuildSummary(orderNumber, productFamily, shellId, orderTimeIso, childCount, childIds);

        var logElement = new LogMessage(ParseLogLevel(LogLevel), summary, agentRole, agentState, shellId);
        logElement.Add(new Property<string>("ProductFamilyName", productFamily ?? string.Empty));
        logElement.Add(new Property<string>("OrderTimeIso", orderTimeIso));
        logElement.Add(new Property<string>("RootOrderNumber", rootOrderNumber));
        logElement.Add(new Property<string>("OrderNumber", orderNumber));
        logElement.Add(new Property<string>("ShellId", shellId));
        logElement.Add(new Property<int>("ChildProductCount", childCount));
        logElement.Add(new Property<int>("RequiredCapabilityCount", requiredCapabilityCount));

        if (childIds.Count > 0)
        {
            var childCollection = new SubmodelElementCollection("ChildProducts");
            for (var i = 0; i < childIds.Count; i++)
            {
                childCollection.Add(new Property<string>($"ChildProductId_{i + 1}", childIds[i]));
            }

            logElement.Add(childCollection);
        }

        if (requiredCapabilityCount > 0)
        {
            var requiredCapabilitiesCollection = new SubmodelElementCollection("RequiredCapabilities");
            for (var i = 0; i < requiredCapabilities.Count; i++)
            {
                requiredCapabilitiesCollection.Add(new Property<string>($"Capability_{i + 1}", requiredCapabilities[i]));
            }
            logElement.Add(requiredCapabilitiesCollection);
        }

        var message = new I40MessageBuilder()
            .From($"{senderId}_Product_Agent", agentRole)
            .To("Broadcast", "System")
            .WithType(I40MessageTypes.INFORM)
            .AddElement(logElement)
            .Build();

        // Testweise die zu sendende Nachricht als JSON loggen
        var serializedMessage = new MessageSerializer().Serialize(message);

        var topic = string.IsNullOrWhiteSpace(Topic)
            ? $"{senderId}/config"
            : ResolvePlaceholders(Topic);

        try
        {
            await client.PublishAsync(message, topic);
            Logger.LogInformation("SendProductSummaryLog: Published product log to topic {Topic} Payload={Payload}", topic, serializedMessage);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendProductSummaryLog: Failed to publish product log");
            return NodeStatus.Failure;
        }
    }

    private static string BuildSummary(string orderNumber, string productFamily, string shellId, string orderTimeIso, int childCount, IReadOnlyCollection<string> childIds)
    {
        var identifier = string.IsNullOrWhiteSpace(orderNumber) ? shellId : orderNumber;
        var summary = $"Product {identifier} ({productFamily}) captured at {orderTimeIso} has {childCount} child products.";
        if (childCount > 0)
        {
            summary += $" Child IDs: {string.Join(", ", childIds)}.";
        }

        summary += $" ShellId: {shellId}.";
        return summary;
    }

    private static LogMessage.LogLevel ParseLogLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return LogMessage.LogLevel.Info;
        }

        if (Enum.TryParse(level, true, out LogMessage.LogLevel parsed))
        {
            return parsed;
        }

        return LogMessage.LogLevel.Info;
    }

    private static string? GetOrderTimeIso(Submodel submodel)
    {
        var isoValue = GetSubmodelString(submodel, "OrderTimeIso");
        if (!string.IsNullOrWhiteSpace(isoValue))
        {
            return isoValue;
        }

        var timestamp = GetSubmodelString(submodel, "OrderTimestamp");
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return null;
        }

        if (long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.ToString("o");
        }

        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime().ToString("o");
        }

        return null;
    }

    private static string? GetSubmodelString(Submodel submodel, string idShort)
    {
        if (submodel?.SubmodelElements?.Values == null)
        {
            return null;
        }

        var property = submodel.SubmodelElements.Values
            .OfType<IProperty>()
            .FirstOrDefault(p => string.Equals(p.IdShort, idShort, StringComparison.OrdinalIgnoreCase));

        var raw = AasValueUnwrap.Unwrap(property?.Value);
        if (raw is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => je.ToString()
            };
        }

        return raw?.ToString();
    }

    private static IEnumerable<string> CollectChildProductIds(Submodel submodel)
    {
        if (submodel?.SubmodelElements?.Values == null)
        {
            yield break;
        }

        foreach (var element in submodel.SubmodelElements.Values)
        {
            foreach (var id in CollectEntityIds(element))
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    yield return id;
                }
            }
        }
    }

    private static IEnumerable<string?> CollectEntityIds(ISubmodelElement element)
    {
        if (element is not Entity entity)
        {
            yield break;
        }

        yield return GetEntityStatementValue(entity, "Id");

        foreach (var child in entity.Values)
        {
            foreach (var nested in CollectEntityIds(child))
            {
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string? GetEntityStatementValue(Entity entity, string idShort)
    {
        var property = entity.Values
            .OfType<IProperty>()
            .FirstOrDefault(p => string.Equals(p.IdShort, idShort, StringComparison.OrdinalIgnoreCase));

        var raw = AasValueUnwrap.Unwrap(property?.Value);
        if (raw is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => je.ToString()
            };
        }

        return raw?.ToString();
    }

    private static IEnumerable<string> ExtractCapabilityNames(CapabilityDescriptionSubmodel? capabilityDescription)
    {
        if (capabilityDescription == null)
            return Array.Empty<string>();

        return capabilityDescription.GetCapabilityNames();
    }

    private static IEnumerable<ISubmodelElement> EnumerateCollection(SubmodelElementCollection collection)
    {
        if (collection?.Value == null)
            return Array.Empty<ISubmodelElement>();

        if (collection.Value is IEnumerable<ISubmodelElement> enumerable)
            return enumerable;

        var valuesProp = collection.Value.GetType().GetProperty("Values");
        if (valuesProp?.GetValue(collection.Value) is IEnumerable<ISubmodelElement> values)
            return values;

        var itemsProp = collection.Value.GetType().GetProperty("Items");
        if (itemsProp?.GetValue(collection.Value) is IEnumerable<ISubmodelElement> items)
            return items;

        return Array.Empty<ISubmodelElement>();
    }

    private static string NormalizeIdentifier(string raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var normalized = Regex.Replace(raw, "[^A-Za-z0-9_-]", "_");
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
