using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Core;

/// <summary>
/// SetBlackboardValue - Setzt einen Wert im Blackboard/Context
/// Einfache Utility-Node zum Manipulieren des Contexts
/// </summary>
public class SetBlackboardValueNode : BTNode
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string ValueType { get; set; } = "string"; // string, bool, int

    public SetBlackboardValueNode() : base("SetBlackboardValue")
    {
    }

    public override Task<NodeStatus> Execute()
    {
        if (string.IsNullOrEmpty(Key))
        {
            Logger.LogError("SetBlackboardValue: Key is empty");
            return Task.FromResult(NodeStatus.Failure);
        }

        try
        {
            var resolvedValue = ResolvePlaceholders(Value);
            object typedValue = ValueType.ToLower() switch
            {
                "bool" => bool.Parse(resolvedValue),
                "int" => int.Parse(resolvedValue),
                "double" => double.Parse(resolvedValue),
                _ => resolvedValue
            };

            Context.Set(Key, typedValue);
            Logger.LogDebug("SetBlackboardValue: Set {Key} = {Value} ({Type})", Key, typedValue, ValueType);
            
            return Task.FromResult(NodeStatus.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SetBlackboardValue: Error setting value");
            return Task.FromResult(NodeStatus.Failure);
        }
    }
}
