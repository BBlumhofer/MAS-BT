using AasSharpClient.Models;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// Loads the AssetLocation submodel for the current shell and stores the typed representation in the blackboard.
/// </summary>
public class LoadAssetLocationSubmodelNode : LoadSubmodelNodeBase<AssetLocationSubmodel>
{
    public LoadAssetLocationSubmodelNode() : base("LoadAssetLocationSubmodel")
    {
    }

    protected override string DefaultIdShort => AssetLocationSubmodel.DefaultIdShort;
    protected override string BlackboardKey => "AssetLocationSubmodel";

    protected override AssetLocationSubmodel CreateTypedInstance(string identifier)
    {
        return new AssetLocationSubmodel(identifier);
    }
}
