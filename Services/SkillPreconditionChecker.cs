using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Services;

public sealed class PreconditionEvaluationResult
{
    private readonly List<string> _errors = new();

    public bool IsSatisfied => _errors.Count == 0;

    public IReadOnlyList<string> Errors => _errors;

    public void AddFailure(string category, string message)
    {
        var prefix = string.IsNullOrWhiteSpace(category) ? "Precondition" : category;
        _errors.Add($"{prefix}: {message}");
    }
}

public enum SkillPreconditionType
{
    Unknown,
    InStorage
}

public enum StorageConditionTarget
{
    Unknown,
    ProductId,
    ProductType,
    CarrierId,
    CarrierType
}

internal sealed record ActionPreconditionDescriptor(
    SkillPreconditionType Type,
    StorageConditionTarget Target,
    string? RawValue,
    string DisplayValue,
    string ConditionId);

public sealed class SkillPreconditionChecker
{
    private readonly BTContext _context;
    private readonly ILogger _logger;

    public SkillPreconditionChecker(BTContext context, ILogger logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PreconditionEvaluationResult> EvaluateAsync(RemoteModule module, SkillRequestEnvelope? envelope)
    {
        ArgumentNullException.ThrowIfNull(module);

        var result = new PreconditionEvaluationResult();

        EvaluateCoupled(module, result);
        await EvaluateStartupSkillAsync(module, result);
        EvaluateLock(module, result);

        if (!result.IsSatisfied)
        {
            return result;
        }

        var descriptors = ExtractActionPreconditions(envelope);
        foreach (var descriptor in descriptors)
        {
            switch (descriptor.Type)
            {
                case SkillPreconditionType.InStorage:
                    var satisfied = await EvaluateInStorageAsync(module, descriptor);
                    if (!satisfied)
                    {
                        result.AddFailure(
                            descriptor.ConditionId,
                            $"Storage entry '{descriptor.DisplayValue}' not found for target '{descriptor.Target}'.");
                    }
                    break;
                default:
                    _logger.LogDebug(
                        "SkillPreconditionChecker: Unsupported precondition type '{Type}' ignored",
                        descriptor.Type);
                    break;
            }
        }

        return result;
    }

    private void EvaluateCoupled(RemoteModule module, PreconditionEvaluationResult result)
    {
        if (IsCoupled(module))
        {
            return;
        }

        result.AddFailure("Coupled", $"Module '{module.Name}' is not coupled.");
    }

    private bool IsCoupled(RemoteModule module)
    {
        var key = $"Module_{module.Name}_Coupled";
        if (_context.Get<bool?>(key) == true)
        {
            return true;
        }

        var coupledModules = _context.Get<List<string>>("CoupledModules");
        if (coupledModules?.Contains(module.Name) == true)
        {
            return true;
        }

        if (_context.Get<bool?>("portsCoupled") == true)
        {
            return true;
        }

        return false;
    }

    private async Task EvaluateStartupSkillAsync(RemoteModule module, PreconditionEvaluationResult result)
    {
        if (await IsStartupSkillRunningAsync(module))
        {
            return;
        }

        result.AddFailure("StartupSkill", $"Startup skill on module '{module.Name}' is not running.");
    }

    private async Task<bool> IsStartupSkillRunningAsync(RemoteModule module)
    {
        var contextFlag = _context.Get<bool?>("startupSkillRunning");
        if (contextFlag == true)
        {
            return true;
        }

        var startupSkill = module.SkillSet.Values
            .FirstOrDefault(s => !string.IsNullOrEmpty(s.Name) && s.Name.IndexOf("Startup", StringComparison.OrdinalIgnoreCase) >= 0);

        if (startupSkill == null)
        {
            return false;
        }

        try
        {
            var state = await startupSkill.GetStateAsync();
            if (state.HasValue)
            {
                return state.Value == (int)SkillStates.Running;
            }

            return startupSkill.CurrentState == SkillStates.Running;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "SkillPreconditionChecker: Failed to read startup skill state for module {Module}",
                module.Name);
            return false;
        }
    }

    private void EvaluateLock(RemoteModule module, PreconditionEvaluationResult result)
    {
        if (module.IsLockedByUs)
        {
            return;
        }

        var key = $"State_{module.Name}_IsLocked";
        if (_context.Get<bool?>(key) == true)
        {
            return;
        }

        result.AddFailure("Lock", $"Module '{module.Name}' is not locked by this agent.");
    }

    private async Task<bool> EvaluateInStorageAsync(RemoteModule module, ActionPreconditionDescriptor descriptor)
    {
        if (module.Storages == null || module.Storages.Count == 0)
        {
            _logger.LogWarning(
                "SkillPreconditionChecker: Module {Module} has no storages when evaluating condition {Condition}",
                module.Name,
                descriptor.ConditionId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(descriptor.RawValue))
        {
            _logger.LogWarning(
                "SkillPreconditionChecker: Condition {Condition} missing ConditionValue",
                descriptor.ConditionId);
            return false;
        }

        var client = _context.Get<UaClient>("UaClient");
        var parsedNodeId = TryParseNodeId(descriptor.RawValue);

        string? productId = null;
        string? carrierId = null;
        NodeId? productType = null;
        NodeId? carrierType = null;

        switch (descriptor.Target)
        {
            case StorageConditionTarget.ProductId:
                productId = descriptor.RawValue;
                break;
            case StorageConditionTarget.CarrierId:
                carrierId = descriptor.RawValue;
                break;
            case StorageConditionTarget.ProductType:
                productType = parsedNodeId;
                break;
            case StorageConditionTarget.CarrierType:
                carrierType = parsedNodeId;
                break;
            case StorageConditionTarget.Unknown:
                productId = descriptor.RawValue;
                carrierId = descriptor.RawValue;
                productType = parsedNodeId;
                carrierType = parsedNodeId;
                break;
        }

        foreach (var storage in module.Storages.Values)
        {
            if (storage == null)
            {
                continue;
            }

            try
            {
                var (found, _) = await storage.IsInStorageAsync(
                    client,
                    productId,
                    productType,
                    carrierId,
                    carrierType);

                if (found)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "SkillPreconditionChecker: Storage {Storage} evaluation failed",
                    storage.Name);
            }
        }

        return false;
    }

    private static NodeId? TryParseNodeId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            return NodeId.Parse(candidate);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<ActionPreconditionDescriptor> ExtractActionPreconditions(SkillRequestEnvelope? envelope)
    {
        var preconditions = envelope?.ActionModel?.Preconditions;
        if (preconditions == null)
        {
            return Array.Empty<ActionPreconditionDescriptor>();
        }

        var descriptors = new List<ActionPreconditionDescriptor>();
        foreach (var element in EnumerateElements(preconditions).OfType<SubmodelElementCollection>())
        {
            var descriptor = ParseDescriptor(element);
            if (descriptor != null)
            {
                descriptors.Add(descriptor);
            }
        }

        return descriptors;
    }

    private static ActionPreconditionDescriptor? ParseDescriptor(SubmodelElementCollection collection)
    {
        string? conditionType = null;
        string? conditionValue = null;
        string? slotContentType = null;

        foreach (var element in EnumerateElements(collection))
        {
            if (element is Property<string> stringProp)
            {
                if (string.Equals(stringProp.IdShort, "ConditionType", StringComparison.OrdinalIgnoreCase))
                {
                    conditionType = stringProp.Value?.Value?.ToString();
                }
                else if (string.Equals(stringProp.IdShort, "ConditionValue", StringComparison.OrdinalIgnoreCase))
                {
                    conditionValue = stringProp.Value?.Value?.ToString();
                }
            }
            else if (element is IProperty property)
            {
                if (string.Equals(property.IdShort, "ConditionType", StringComparison.OrdinalIgnoreCase))
                {
                    conditionType = property.Value?.Value?.ToString();
                }
                else if (string.Equals(property.IdShort, "ConditionValue", StringComparison.OrdinalIgnoreCase))
                {
                    conditionValue = property.Value?.Value?.ToString();
                }
            }
            else if (element is SubmodelElementCollection nested &&
                     string.Equals(nested.IdShort, "ConditionValue", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var nestedElement in EnumerateElements(nested).OfType<IProperty>())
                {
                    if (string.Equals(nestedElement.IdShort, "SlotContentType", StringComparison.OrdinalIgnoreCase))
                    {
                        slotContentType = nestedElement.Value?.Value?.ToString();
                    }
                    else if (string.Equals(nestedElement.IdShort, "SlotValue", StringComparison.OrdinalIgnoreCase))
                    {
                        conditionValue = nestedElement.Value?.Value?.ToString();
                    }
                }
            }
        }

        var type = ParsePreconditionType(conditionType);
        if (type == SkillPreconditionType.Unknown)
        {
            return null;
        }

        var target = ParseStorageConditionTarget(slotContentType);
        var value = conditionValue ?? string.Empty;
        var id = string.IsNullOrWhiteSpace(collection.IdShort) ? "Precondition" : collection.IdShort!;
        return new ActionPreconditionDescriptor(type, target, conditionValue, value, id);
    }

    private static SkillPreconditionType ParsePreconditionType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SkillPreconditionType.Unknown;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "instorage" => SkillPreconditionType.InStorage,
            _ => SkillPreconditionType.Unknown
        };
    }

    private static StorageConditionTarget ParseStorageConditionTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return StorageConditionTarget.Unknown;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "productid" => StorageConditionTarget.ProductId,
            "producttype" => StorageConditionTarget.ProductType,
            "carrierid" => StorageConditionTarget.CarrierId,
            "carriertype" => StorageConditionTarget.CarrierType,
            _ => StorageConditionTarget.Unknown
        };
    }

    private static IEnumerable<ISubmodelElement> EnumerateElements(SubmodelElementCollection? coll)
    {
        if (coll is null)
        {
            return Array.Empty<ISubmodelElement>();
        }

        if (coll.Value is IEnumerable<ISubmodelElement> seq)
        {
            return seq;
        }

        if (coll is IEnumerable<ISubmodelElement> enumerable)
        {
            return enumerable;
        }

        return Array.Empty<ISubmodelElement>();
    }
}
