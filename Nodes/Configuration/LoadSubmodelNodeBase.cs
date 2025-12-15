using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BaSyx.Clients.AdminShell.Http;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// Base class for loading typed AAS submodels via the BaSyx HTTP clients.
/// Handles repository resolution, retrieval, and mapping to strongly-typed AAS-Sharp models.
/// </summary>
public abstract class LoadSubmodelNodeBase<TSubmodel> : BTNode where TSubmodel : Submodel
{
    protected LoadSubmodelNodeBase(string name) : base(name)
    {
    }

    /// <summary>
    /// Optional override for the Submodel Repository endpoint.
    /// Falls back to config.AAS.SubmodelRepositoryEndpoint when empty.
    /// </summary>
    public string SubmodelRepositoryEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for the target IdShort that should be loaded.
    /// Defaults to the derived class specific value when empty.
    /// </summary>
    public string TargetIdShort { get; set; } = string.Empty;
    
    /// <summary>
    /// Optional semantic id filter. When set, only submodels whose SemanticId contains this reference will be accepted.
    /// </summary>
    public string SemanticIdFilter { get; set; } = string.Empty;

    protected abstract string DefaultIdShort { get; }
    protected abstract string BlackboardKey { get; }
    protected abstract TSubmodel CreateTypedInstance(string identifier);

    public override async Task<NodeStatus> Execute()
    {
        var shell = Context.Get<IAssetAdministrationShell>("AAS.Shell")
                    ?? Context.Get<IAssetAdministrationShell>("shell");

        if (shell == null)
        {
            Logger.LogError("{Node}: No Asset Administration Shell present in context. Run ReadShell before loading submodels.", Name);
            return NodeStatus.Failure;
        }

        LogSubmodelReferences(shell);

        var repoEndpoint = ResolveRepositoryEndpoint();
        if (string.IsNullOrWhiteSpace(repoEndpoint))
        {
            Logger.LogError("{Node}: Missing SubmodelRepositoryEndpoint (property or config.AAS.SubmodelRepositoryEndpoint)", Name);
            return NodeStatus.Failure;
        }

        var targetIdShort = string.IsNullOrWhiteSpace(TargetIdShort)
            ? DefaultIdShort
            : ResolvePlaceholders(TargetIdShort);
        var semanticIdFilter = string.IsNullOrWhiteSpace(SemanticIdFilter)
            ? string.Empty
            : ResolvePlaceholders(SemanticIdFilter);

        try
        {
            using var repoClient = new SubmodelRepositoryHttpClient(BuildUri(repoEndpoint));
            var loadResult = await RetrieveTargetSubmodelAsync(shell, repoClient, targetIdShort, semanticIdFilter).ConfigureAwait(false);

            if (loadResult == null)
            {
                Logger.LogError("{Node}: Submodel '{IdShort}' not found for shell {ShellId}",
                    Name,
                    targetIdShort,
                    shell.Id?.Id ?? Context.AgentId);
                return NodeStatus.Failure;
            }

            var (identifier, submodel) = loadResult.Value;
            var typed = CreateTypedInstance(!string.IsNullOrWhiteSpace(submodel.Id?.Id) ? submodel.Id.Id : identifier);
            CopySubmodel(submodel, typed);

            Context.Set(BlackboardKey, typed);
            Context.Set($"{BlackboardKey}.Raw", submodel);
            Context.Set($"AAS.Submodel.{typed.IdShort}", typed);

            var elementCount = typed.SubmodelElements?.Count ?? 0;
            Logger.LogInformation("{Node}: Loaded submodel {IdShort} ({Identifier}) with {ElementCount} elements",
                Name,
                typed.IdShort,
                identifier,
                elementCount);

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Node}: Error loading submodel {IdShort}", Name, targetIdShort);
            return NodeStatus.Failure;
        }
    }

    private string ResolveRepositoryEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(SubmodelRepositoryEndpoint))
        {
            return ResolvePlaceholders(SubmodelRepositoryEndpoint);
        }

        return Context.Get<string>("config.AAS.SubmodelRepositoryEndpoint")
            ?? Context.Get<string>("AAS.SubmodelRepositoryEndpoint")
            ?? string.Empty;
    }

    private static Uri BuildUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid SubmodelRepositoryEndpoint URI: {endpoint}");
        }

        return uri;
    }

    private async Task<(string Identifier, ISubmodel Submodel)?> RetrieveTargetSubmodelAsync(
        IAssetAdministrationShell shell,
        SubmodelRepositoryHttpClient repoClient,
        string targetIdShort,
        string semanticIdFilter)
    {
        // Shell references can come back as non-generic Reference instances; treat them as plain IReference
        var references = shell.SubmodelReferences?.Cast<IReference>().ToList()
                         ?? new List<IReference>();
        if (!references.Any())
        {
            Logger.LogWarning("{Node}: Shell {ShellId} exposes no submodel references", Name, shell.Id?.Id ?? Context.AgentId);
            return null;
        }

        foreach (var reference in references)
        {
            var referenceSemanticValues = GetReferenceSemanticIdValues(reference);
            if (!string.IsNullOrWhiteSpace(semanticIdFilter)
                && referenceSemanticValues.Any()
                && !referenceSemanticValues.Any(v => MatchesSemanticIdString(v, semanticIdFilter)))
            {
                continue; // skip references that explicitly do not match the requested semantic id
            }

            if (!TryGetSubmodelIdentifier(reference, out var identifier))
            {
                continue;
            }

            object? rawResult = null;
            try
            {
                rawResult = await repoClient.RetrieveSubmodelAsync(identifier).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Node}: Exception retrieving submodel {Identifier}", Name, identifier.Id);
                continue;
            }

            var success = TryGetBoolProperty(rawResult, "Success");
            var entity = TryGetProperty<ISubmodel>(rawResult, "Entity");
            var message = TryGetProperty<object>(rawResult, "Messages")?.ToString() ?? "(no message)";

            if (!success || entity == null)
            {
                Logger.LogWarning("{Node}: Unable to retrieve submodel {Identifier}: {Message}",
                    Name,
                    identifier.Id,
                    message);
                continue;
            }

            var matchesIdShort = string.Equals(entity.IdShort, targetIdShort, StringComparison.OrdinalIgnoreCase);
            var matchesSemantic = MatchesSemanticId(entity.SemanticId, semanticIdFilter);

            var acceptBySemantic = !string.IsNullOrWhiteSpace(semanticIdFilter) && matchesSemantic;
            var acceptByIdShort = matchesIdShort || string.IsNullOrWhiteSpace(targetIdShort);

            if (acceptBySemantic || (acceptByIdShort && (string.IsNullOrWhiteSpace(semanticIdFilter) || matchesSemantic)))
            {
                return (identifier.Id, entity);
            }
        }

        return null;
    }

    private static bool TryGetSubmodelIdentifier(IReference reference, out Identifier identifier)
    {
        identifier = null!;
        var keys = reference?.Keys?.ToList();
        if (keys == null || keys.Count == 0)
        {
            return false;
        }

        var key = keys.LastOrDefault(k => k.Type == KeyType.Submodel)
                  ?? keys.LastOrDefault();

        if (key == null || string.IsNullOrWhiteSpace(key.Value))
        {
            return false;
        }

        identifier = new Identifier(key.Value);
        return true;
    }

    private static bool MatchesSemanticId(IReference? semanticId, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var filters = SplitSemanticFilters(filter);
        if (filters.Length == 0)
            return true;

        var keys = semanticId?.Keys;
        if (keys == null)
            return false;

        return keys.Any(k => filters.Any(f => string.Equals(k.Value, f, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool MatchesSemanticIdString(string value, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var filters = SplitSemanticFilters(filter);
        if (filters.Length == 0)
            return true;

        return filters.Any(f => string.Equals(value, f, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SplitSemanticFilters(string filter)
    {
        return filter
            .Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToArray();
    }

    private static void CopySubmodel(ISubmodel source, Submodel target)
    {
        target.Kind = source.Kind;
        target.SemanticId = source.SemanticId;
        target.SupplementalSemanticIds = source.SupplementalSemanticIds?.ToList();
        target.EmbeddedDataSpecifications = source.EmbeddedDataSpecifications?.ToList();
        target.Qualifiers = source.Qualifiers?.ToList();
        target.Description = source.Description;
        target.DisplayName = source.DisplayName;
        target.Category = source.Category;
        target.Administration = source.Administration;

        target.SubmodelElements.Clear();
        if (source.SubmodelElements != null)
        {
            foreach (var element in source.SubmodelElements.Values)
            {
                target.SubmodelElements.Add(element);

                // Spezialfall: CapabilityDescriptionSubmodel - CapabilitySet-Property aktualisieren
                if (target is AasSharpClient.Models.CapabilityDescriptionSubmodel capSm
                    && element is SubmodelElementCollection coll
                    && string.Equals(coll.IdShort, "CapabilitySet", StringComparison.OrdinalIgnoreCase))
                {
                    // Set private setter via reflection to keep CapabilitySet pointing to the loaded collection
                    var prop = typeof(AasSharpClient.Models.CapabilityDescriptionSubmodel)
                        .GetProperty("CapabilitySet", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    prop?.SetValue(capSm, coll);
                }
            }
        }
    }

    private void LogSubmodelReferences(IAssetAdministrationShell shell)
    {
        var references = shell.SubmodelReferences?.Cast<IReference>().ToList() ?? new List<IReference>();
        if (references.Count == 0)
        {
            Logger.LogWarning("{Node}: Shell {ShellId} exposes no submodel references", Name, shell.Id?.Id ?? Context.AgentId);
            return;
        }

        Logger.LogInformation("{Node}: Shell {ShellId} exposes {Count} submodel references", Name, shell.Id?.Id ?? Context.AgentId, references.Count);

        for (var i = 0; i < references.Count; i++)
        {
            var reference = references[i];
            var idValue = reference?.Keys?.LastOrDefault()?.Value ?? "<unknown>";
            var semantic = GetSemanticIdValue(reference);
            var referredSemantic = GetReferredSemanticIdValue(reference);
            var guessedType = GuessSubmodelType(!string.IsNullOrWhiteSpace(semantic) ? semantic : referredSemantic);
            var keys = reference?.Keys?.Select(k => $"{k.Type}:{k.Value}") ?? Array.Empty<string>();
            Logger.LogInformation("{Node}:   [{Index}] Id={Id} SemanticId={SemanticId} ReferredSemanticId={ReferredSemanticId} Type={Type} Keys=[{Keys}]",
                Name,
                i,
                idValue,
                semantic,
                referredSemantic,
                guessedType,
                string.Join(", ", keys));
        }
    }

    private static string GuessSubmodelType(string semanticId)
    {
        if (string.IsNullOrWhiteSpace(semanticId))
            return "Unknown";

        return semanticId switch
        {
            "https://smartfactory.de/semantics/submodel/CapabilityDescription#1/0" => "CapabilityDescription",
            _ when semanticId.Contains("BillOfMaterial", StringComparison.OrdinalIgnoreCase) => "BillOfMaterial",
            _ when semanticId.Contains("ProductIdentification", StringComparison.OrdinalIgnoreCase) => "ProductIdentification",
            _ => "Unknown"
        };
    }

    private static string GetSemanticIdValue(IReference? reference)
    {
        if (reference == null)
        {
            return "<none>";
        }

        try
        {
            var semanticProp = reference.GetType().GetProperty("SemanticId");
            var semantic = semanticProp?.GetValue(reference);
            var keysProp = semantic?.GetType().GetProperty("Keys");
            if (keysProp?.GetValue(semantic) is IEnumerable<IKey> keys)
            {
                return keys.FirstOrDefault()?.Value ?? "<none>";
            }
        }
        catch
        {
            // ignore reflection errors
        }

        return "<none>";
    }

    private static string GetReferredSemanticIdValue(IReference? reference)
    {
        if (reference == null)
        {
            return "<none>";
        }

        try
        {
            var referredProp = reference.GetType().GetProperty("ReferredSemanticId");
            var referred = referredProp?.GetValue(reference);
            var keysProp = referred?.GetType().GetProperty("Keys");
            if (keysProp?.GetValue(referred) is IEnumerable<IKey> keys)
            {
                return keys.FirstOrDefault()?.Value ?? "<none>";
            }
        }
        catch
        {
            // ignore reflection errors
        }

        return "<none>";
    }

    private static List<string> GetReferenceSemanticIdValues(IReference? reference)
    {
        var values = new List<string>();
        var semantic = GetSemanticIdValue(reference);
        if (!string.IsNullOrWhiteSpace(semantic) && !string.Equals(semantic, "<none>", StringComparison.OrdinalIgnoreCase))
        {
            values.Add(semantic);
        }

        var referred = GetReferredSemanticIdValue(reference);
        if (!string.IsNullOrWhiteSpace(referred) && !string.Equals(referred, "<none>", StringComparison.OrdinalIgnoreCase))
        {
            values.Add(referred);
        }

        return values;
    }

    private static TProperty? TryGetProperty<TProperty>(object? obj, string propertyName) where TProperty : class
    {
        if (obj == null)
            return null;

        var prop = obj.GetType().GetProperties().FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.Ordinal));
        if (prop == null)
            return null;

        try
        {
            return prop.GetValue(obj) as TProperty;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetBoolProperty(object? obj, string propertyName)
    {
        if (obj == null)
            return false;

        var prop = obj.GetType().GetProperties().FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.Ordinal));
        if (prop == null)
            return false;

        try
        {
            var val = prop.GetValue(obj);
            if (val is bool b)
                return b;

            if (val is string s && bool.TryParse(s, out var parsed))
                return parsed;
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
