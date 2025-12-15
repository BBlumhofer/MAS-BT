using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Nodes.Configuration;
using MAS_BT.Nodes.Locking;
using MAS_BT.Nodes.Messaging;
using MAS_BT.Nodes.Monitoring;
using MAS_BT.Nodes.SkillControl;
using MAS_BT.Nodes.Core;
using MAS_BT.Nodes.Constraints;
using MAS_BT.Nodes.Recovery;
using MAS_BT.Nodes; // Neue Monitoring Nodes
using MAS_BT.Nodes.Planning;
using MAS_BT.Nodes.Planning.ProcessChain;
using MAS_BT.Nodes.Dispatching;
using MAS_BT.Nodes.Dispatching.ProcessChain;
using MAS_BT.Nodes.ModuleHolon;
using MAS_BT.Nodes.Common;

namespace MAS_BT.Serialization;

/// <summary>
/// Node Registry - Verwaltet alle verfügbaren BT Node Typen
/// </summary>
public class NodeRegistry
{
    private readonly Dictionary<string, Type> _nodeTypes = new();
    private readonly ILogger _logger;
    
    public NodeRegistry(ILogger logger)
    {
        _logger = logger;
        RegisterDefaultNodes();
    }
    
    /// <summary>
    /// Registriert einen Node-Typ unter einem Namen
    /// </summary>
    public void Register<T>(string name) where T : BTNode
    {
        _nodeTypes[name] = typeof(T);
    }
    
    /// <summary>
    /// Registriert einen Node-Typ (Name = Klassenname ohne "Node" Suffix)
    /// </summary>
    public void Register<T>() where T : BTNode
    {
        var name = typeof(T).Name;
        if (name.EndsWith("Node"))
            name = name.Substring(0, name.Length - 4);
        
        Register<T>(name);
    }
    
    /// <summary>
    /// Erstellt eine Node-Instanz anhand des Namens
    /// </summary>
    public BTNode CreateNode(string name)
    {
        if (!_nodeTypes.TryGetValue(name, out var type))
        {
            throw new InvalidOperationException($"Unbekannter Node-Typ: {name}. Verfügbare Typen: {string.Join(", ", _nodeTypes.Keys)}");
        }
        
        // Versuche Constructor mit ILogger Parameter
        var ctorWithLogger = type.GetConstructor(new[] { typeof(ILogger) });
        if (ctorWithLogger != null)
        {
            var node = ctorWithLogger.Invoke(new object[] { _logger }) as BTNode;
            if (node == null)
            {
                throw new InvalidOperationException($"Fehler beim Erstellen von Node mit Logger: {name}");
            }
            return node;
        }
        
        // Fallback: Parameter-loser Constructor
        var node2 = Activator.CreateInstance(type) as BTNode;
        if (node2 == null)
        {
            throw new InvalidOperationException($"Fehler beim Erstellen von Node: {name}");
        }
        
        // Setze Logger via Property falls vorhanden
        var loggerProp = type.GetProperty("Logger");
        if (loggerProp != null && loggerProp.CanWrite)
        {
            loggerProp.SetValue(node2, _logger);
        }
        
        return node2;
    }
    
    /// <summary>
    /// Prüft ob ein Node-Typ registriert ist
    /// </summary>
    public bool IsRegistered(string name) => _nodeTypes.ContainsKey(name);
    
    /// <summary>
    /// Gibt alle registrierten Node-Namen zurück
    /// </summary>
    public IEnumerable<string> GetRegisteredNames() => _nodeTypes.Keys;
    
    /// <summary>
    /// Registriert alle Standard-Nodes (Composite + Decorator)
    /// </summary>
    private void RegisterDefaultNodes()
    {
        // Core Composite Nodes
        Register<SequenceNode>();
        Register<SelectorNode>();
        Register<SelectorNode>("Fallback"); // Alias für Selector
        Register<ParallelNode>();
        
        // Core Decorator Nodes
        Register<RetryNode>();
        Register<TimeoutNode>();
        Register<RepeatNode>();
        Register<InverterNode>();
        Register<SucceederNode>();
        Register<RetryUntilSuccessNode>();
        
        // Core Utility Nodes
        Register<WaitNode>();
        Register<SetBlackboardValueNode>();
        Register<ForceFailureNode>();
        Register<AlwaysSuccessNode>();
        
        // Condition Nodes
        Register<ConditionNode>();
        Register<BlackboardConditionNode>();
        Register<CompareConditionNode>();
        
        // Configuration Nodes
        Register<ReadConfigNode>();
        Register<ConnectToMessagingBrokerNode>();
        Register<ReadShellNode>();
        Register<ReadCapabilityDescriptionNode>("ReadCapabilityDescription");
        Register<ReadSkillsNode>("ReadSkills");
        Register<ReadMachineScheduleNode>("ReadMachineSchedule");
        Register<ReadNameplateNode>("ReadNameplate");
        Register<ConnectToModuleNode>();
        Register<CoupleModuleNode>();
        Register<EnsurePortsCoupledNode>();
        Register<LoadProductIdentificationSubmodelNode>();
        Register<LoadBillOfMaterialSubmodelNode>();
        Register<LoadProcessChainFromShellNode>("LoadProcessChainFromShell");
        Register<LoadCapabilityDescriptionSubmodelNode>();
        Register<CheckConfigFlagNode>("CheckConfigFlag");
        Register<CheckProcessChainRequestPolicyNode>("CheckProcessChainRequestPolicy");
        Register<ExtractCapabilityNamesNode>("ExtractCapabilityNames");
        Register<UploadSubmodelNode>("UploadSubmodel");
        
        // Locking Nodes
        Register<LockResourceNode>("LockResource");
        Register<UnlockResourceNode>("UnlockResource");
        Register<CheckLockStatusNode>();
        
        // Messaging Nodes
        Register<SendMessageNode>();
        Register<SendLogMessageNode>();
        Register<SendConfigAsLogNode>();
        Register<SendProductSummaryLogNode>();
        Register<WaitForMessageNode>();
        Register<SendResponseMessageNode>();
        Register<SubscribeToTopicNode>();
        
        // Messaging Integration Nodes (Phase 3 - NEW)
        Register<ReadMqttSkillRequestNode>();
        Register<SendSkillResponseNode>();
        Register<UpdateInventoryNode>("UpdateInventory");
        Register<PublishNeighborsNode>("PublishNeighbors");
        Register<PublishCapabilitiesNode>("PublishCapabilities");
        Register<MAS_BT.Nodes.Dispatching.HandleInventoryUpdateNode>("HandleInventoryUpdate");
        Register<ReadNeighborsFromRemoteNode>("ReadNeighborsFromRemote");
        Register<SendStateMessageNode>();
        Register<EnableStorageChangeMqttNode>();
        Register<SendProcessChainRequestNode>();
        Register<SendManufacturingRequestNode>("SendManufacturingRequest");

        // Generic agent lifecycle + registration (real generic nodes)
        Register<InitializeAgentStateNode>("InitializeAgentState");
        Register<SubscribeAgentTopicsNode>("SubscribeAgentTopics");
        Register<RegisterAgentNode>("RegisterAgent");
        Register<HandleRegistrationNode>("HandleRegistration");
        Register<WaitForRegistrationNode>("WaitForRegistration");

        // AI Agent Nodes (Similarity Analysis)
        Register<CalcEmbeddingNode>("CalcEmbedding");
        Register<CalcCosineSimilarityNode>("CalcCosineSimilarity");
        Register<CalcPairwiseSimilarityNode>("CalcPairwiseSimilarity");
        Register<CalcDescribedSimilarityNode>("CalcDescribedSimilarity");
        Register<BuildDescribedSimilarityResponseNode>("BuildDescribedSimilarityResponse");
        Register<BuildPairwiseSimilarityResponseNode>("BuildPairwiseSimilarityResponse");
        Register<CreateDescriptionNode>("CreateDescription");
        Register<BuildCreateDescriptionResponseNode>("BuildCreateDescriptionResponse");

        // Dispatching / process-chain flow
        Register<ParseProcessChainRequestNode>("ParseProcessChainRequest");
        Register<CheckForCapabilitiesInNamespaceNode>("CheckForCapabilitiesInNamespace");
        Register<DispatchCapabilityRequestsNode>("DispatchCapabilityRequests");
        Register<CollectCapabilityOfferNode>("CollectCapabilityOffer");
        Register<DispatchTransportRequestsNode>("DispatchTransportRequests");
        Register<BuildProcessChainResponseNode>("BuildProcessChainResponse");
        Register<SendProcessChainResponseNode>("SendProcessChainResponse");
        Register<PublishAgentStateNode>("PublishAgentState");

        // Planning / offer flow
        Register<ParseCapabilityRequestNode>("ParseCapabilityRequest");
        Register<PlanCapabilityOfferNode>("PlanCapabilityOffer");
        Register<SendCapabilityOfferNode>("SendCapabilityOffer");
        Register<ReceiveOfferMessageNode>("ReceiveOfferMessage");
        Register<AwaitOfferDecisionNode>("AwaitOfferDecision");
        Register<ApplyOfferDecisionNode>("ApplyOfferDecision");
        Register<SelectSchedulableActionNode>("SelectSchedulableAction");
        Register<RequestTransportNode>("RequestTransport");
        Register<EvaluateRequestTransportResponseNode>("EvaluateRequestTransportResponse");
        Register<CheckScheduleFeasibilityNode>("CheckScheduleFeasibility");
        Register<DispatchScheduledStepsNode>("DispatchScheduledSteps");

        // ModuleHolon internal forwarding
        Register<SubscribeModuleHolonTopicsNode>("SubscribeModuleHolonTopics");
        Register<ReadCachedSnapshotsNode>("ReadCachedSnapshots");
        Register<ForwardCapabilityRequestsNode>("ForwardCapabilityRequests");
        Register<ForwardToInternalNode>("ForwardToInternal");
        Register<WaitForInternalResponseNode>("WaitForInternalResponse");
        Register<ReplyToDispatcherNode>("ReplyToDispatcher");
        Register<SpawnSubHolonsNode>("SpawnSubHolons");

        // Direct request handlers
        Register<HandleManufacturingSequenceRequestNode>("HandleManufacturingSequenceRequest");
        Register<HandleBookStepRequestNode>("HandleBookStepRequest");
        Register<HandleTransportPlanRequestNode>("HandleTransportPlanRequest");
        
        // Monitoring Nodes (bestehende)
        Register<ReadStorageNode>();
        Register<CheckStartupSkillStatusNode>();
        // Monitoring Nodes (Phase 1 - Core Monitoring)
        Register<CheckReadyStateNode>();
        Register<CheckLockedStateNode>();
        Register<MonitoringSkillNode>();
        
        // Constraint Nodes (Module State Management)
        Register<CheckModuleStateNode>();
        Register<SetModuleStateNode>();
        
        // Skill Control Nodes (Phase 2)
        Register<WaitForSkillStateNode>();
        Register<AbortSkillNode>();
        Register<PauseSkillNode>();
        Register<ResumeSkillNode>();
        Register<RetrySkillNode>();
        Register<ResetSkillNode>();
        
        // Skill Nodes
        Register<ExecuteSkillNode>();
        
        // Planning Nodes
        Register<LoadProductionPlanNode>("LoadProductionPlan");
        Register<SelectNextActionNode>("SelectNextAction");
        Register<SendSkillRequestNode>("SendSkillRequest");
        Register<AwaitSkillResponseNode>("AwaitSkillResponse");
        Register<ApplySkillResponseNode>("ApplySkillResponse");
        Register<CapabilityMatchmakingNode>("CapabilityMatchmaking");
        Register<SchedulingExecuteNode>("SchedulingExecute");
        Register<CalculateOfferNode>("CalculateOffer");
        Register<SendOfferNode>("SendOffer");
        Register<UpdateMachineScheduleNode>("UpdateMachineSchedule");
        Register<RequestTransportNode>("RequestTransport");
        Register<DeriveStepFromCapabilityNode>("DeriveStepFromCapability");
        Register<CheckConstraintsNode>("CheckConstraints");
        Register<FeasibilityCheckNode>("FeasibilityCheck");
        Register<SendPlanningRefusalNode>("SendPlanningRefusal");
        Register<AwaitOfferDecisionNode>("AwaitOfferDecision");
        Register<ApplyOfferDecisionNode>("ApplyOfferDecision");
        Register<DispatchScheduledStepsNode>("DispatchScheduledSteps");
        Register<ReceiverRequestForOfferNode>("ReceiverRequestForOffer");
        Register<FeedbackCapabilityMatchmakingNode>("FeedbackCapabilityMatchmaking");
        Register<SchedulingFeedbackNode>("SchedulingFeedback");
        Register<EvaluateRequestTransportResponseNode>("EvaluateRequestTransportResponse");
        Register<CheckScheduleFeasibilityNode>("CheckScheduleFeasibility");
        Register<ReceiveOfferMessageNode>("ReceiveOfferMessage");
        Register<SelectSchedulableActionNode>("SelectSchedulableAction");
        Register<CheckSkillPreconditionsNode>();
        // Neo4j nodes
        Register<InitNeo4jNode>("InitNeo4j");
        Register<RunNeo4jTestNode>("RunNeo4jTest");

        // Recovery Nodes
        Register<RecoverySequenceNode>("RecoverySequence");
        Register<EnsureModuleLockedNode>("EnsureModuleLocked");
        Register<EnsureStartupRunningNode>("EnsureStartupRunning");
        Register<HaltAllSkillsNode>("HaltAllSkills");
        
        _logger.LogDebug("Standard Nodes registriert: {Count}", _nodeTypes.Count);
    }
}
