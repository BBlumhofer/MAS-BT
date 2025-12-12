using AasSharpClient.Models;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// Loads the CapabilityDescription submodel (semantic id https://smartfactory.de/semantics/submodel/CapabilityDescription#1/0)
/// and stores the typed representation in the blackboard.
/// </summary>
public class LoadCapabilityDescriptionSubmodelNode : LoadSubmodelNodeBase<CapabilityDescriptionSubmodel>
{
    public LoadCapabilityDescriptionSubmodelNode() : base("LoadCapabilityDescriptionSubmodel")
    {
        SemanticIdFilter = "https://smartfactory.de/semantics/submodel/CapabilityDescription#1/0|https://admin-shell.io/idta/CapabilityDescription/1/0/Submodel";
    }

    protected override string DefaultIdShort => "CapabilityDescription";
    protected override string BlackboardKey => "CapabilityDescriptionSubmodel";

    protected override CapabilityDescriptionSubmodel CreateTypedInstance(string identifier)
    {
        return CapabilityDescriptionSubmodel.CreateWithIdentifier(identifier);
    }
}
