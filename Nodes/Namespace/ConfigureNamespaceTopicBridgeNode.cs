using System;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;
using MAS_BT.Core;
using MAS_BT.Services.TopicBridge;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Namespace;

/// <summary>
/// Configures the namespace-level topic bridge so all external MQTT topics are mirrored
/// to internal sub-holon topics (and vice versa).
/// </summary>
public class ConfigureNamespaceTopicBridgeNode : BTNode
{
    public ConfigureNamespaceTopicBridgeNode() : base("ConfigureNamespaceTopicBridge") { }

    public string ManufacturingSuffix { get; set; } = "Manufacturing";
    public string TransportSuffix { get; set; } = "Transport";

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var transport = Context.Get<IMessagingTransport>("MessagingTransport");
        if (client == null || transport == null)
        {
            Logger.LogError("ConfigureNamespaceTopicBridge: missing MessagingClient or MessagingTransport");
            return NodeStatus.Failure;
        }

        if (Context.Has("Namespace.TopicBridge"))
        {
            return NodeStatus.Success;
        }

        var externalNamespace = Context.Get<string>("config.ExternalNamespace")
                                ?? Context.Get<string>("ExternalNamespace")
                                ?? Context.Get<string>("config.Namespace")
                                ?? Context.Get<string>("Namespace")
                                ?? "_PHUKET";
        var parentAgentId = Context.Get<string>("config.Agent.AgentId")
                             ?? Context.AgentId
                             ?? "NamespaceHolon";

        var externalBase = externalNamespace.Trim('/');

        var manufacturingAgentId = Context.Get<string>("config.NamespaceHolon.ManufacturingAgentId");
        if (string.IsNullOrWhiteSpace(manufacturingAgentId))
        {
            manufacturingAgentId = $"{parentAgentId}_{ManufacturingSuffix}";
        }

        var transportAgentId = Context.Get<string>("config.NamespaceHolon.TransportAgentId");
        if (string.IsNullOrWhiteSpace(transportAgentId))
        {
            transportAgentId = $"{parentAgentId}_{TransportSuffix}";
        }

        var manufacturingNamespace = $"{externalBase}/{manufacturingAgentId}".Trim('/');
        var transportNamespace = $"{externalBase}/{transportAgentId}".Trim('/');

        var service = new TopicBridgeService(client, transport);
        var ruleCount = 0;

        // Manufacturing requests/responses.
        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "ProcessChain/Request"),
            BuildTopic(manufacturingNamespace, "ProcessChain/Request"));

        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "ProcessChain/Response"),
            BuildTopic(manufacturingNamespace, "ProcessChain/Response"));

        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "ManufacturingSequence/Request"),
            BuildTopic(manufacturingNamespace, "ManufacturingSequence/Request"));

        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "ManufacturingSequence/Response"),
            BuildTopic(manufacturingNamespace, "ManufacturingSequence/Response"));

        // ManufacturingSequence responses fan out to products and the transport manager.
        service.AddRule(
            BuildTopic(manufacturingNamespace, "ManufacturingSequence/Response"),
            BuildTopic(externalNamespace, "ManufacturingSequence/Response"),
            BuildTopic(transportNamespace, "ManufacturingSequence/Response"));
        ruleCount++;

        service.AddRule(
            BuildTopic(externalNamespace, "ManufacturingSequence/Response"),
            BuildTopic(manufacturingNamespace, "ManufacturingSequence/Response"));
        ruleCount++;

        // Transport plan loop (Request/Response).
        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "TransportPlan/Request"),
            BuildTopic(transportNamespace, "TransportPlan/Request"));

        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "TransportPlan/Response"),
            BuildTopic(transportNamespace, "TransportPlan/Response"));

        // Book step flow (requests + responses use absolute topics).
        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "request/BookStep"),
            BuildTopic(manufacturingNamespace, "request/BookStep"));

        AddBidirectional(service, ref ruleCount,
            "/response/BookStep",
            BuildTopic(manufacturingNamespace, "response/BookStep"));

        // Registration and inventory (bidirectional).
        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "register"),
            BuildTopic(manufacturingNamespace, "register"));

        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "Register"),
            BuildTopic(manufacturingNamespace, "Register"));

        AddBidirectional(service, ref ruleCount,
            BuildTopic(externalNamespace, "ModuleRegistration"),
            BuildTopic(manufacturingNamespace, "ModuleRegistration"));

        service.AddRule(
            $"/{externalBase}/+/Inventory",
            $"/{manufacturingNamespace}/{{0}}/Inventory");
        ruleCount++;

        service.AddRule(
            $"/{manufacturingNamespace}/+/Inventory",
            $"/{externalBase}/{{0}}/Inventory");
        ruleCount++;

        await service.InitializeAsync().ConfigureAwait(false);
        Context.Set("Namespace.TopicBridge", service);
        Logger.LogInformation("ConfigureNamespaceTopicBridge: registered {Count} topic bridge routes", ruleCount);
        return NodeStatus.Success;
    }

    private static void AddBidirectional(TopicBridgeService service, ref int ruleCount, string externalTopic, string internalTopic)
    {
        service.AddRule(externalTopic, internalTopic);
        service.AddRule(internalTopic, externalTopic);
        ruleCount += 2;
    }

    private static string BuildTopic(string ns, string suffix)
    {
        var sanitizedNs = string.IsNullOrWhiteSpace(ns) ? "_PHUKET" : ns.Trim('/');
        var sanitizedSuffix = string.IsNullOrWhiteSpace(suffix) ? string.Empty : suffix.Trim('/');
        return "/" + string.Join('/', new[] { sanitizedNs, sanitizedSuffix }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }
}
