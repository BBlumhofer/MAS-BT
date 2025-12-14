using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using RangeElement = BaSyx.Models.AdminShell.Range;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning.ProcessChain;
using MAS_BT.Services.Embeddings;
using MAS_BT.Services.Graph;
using Neo4j.Driver;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// Matches requested capability properties against offered ones using equality first and
/// embedding similarity as a fallback.
/// </summary>
public class CapabilityMatchmakingNode : BTNode
{
    private const string RequestContextKey = "CapabilityMatchmaking.RequestContext";
    private const string ResultContextKey = "CapabilityMatchmaking.Result";
    private const string EmbeddingProviderKey = "CapabilityMatchmaking.EmbeddingProvider";

    public string RequiredCapability { get; set; } = string.Empty;
    public string RefusalReason { get; set; } = "capability_not_found";
    public string EmbeddingEndpoint { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public double SimilarityThreshold { get; set; } = 0.82;

    public bool UseNeo4jForOfferings { get; set; } = true;

    public CapabilityMatchmakingNode() : base("CapabilityMatchmaking") {}

    public override async Task<NodeStatus> Execute()
    {
        var request = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");
        if (request?.CapabilityContainer == null)
        {
            Logger.LogWarning("CapabilityMatchmaking: capability request context missing or lacks property set");
            Context.Set("RefusalReason", RefusalReason);
            return NodeStatus.Failure;
        }

        var capabilityName = ResolveCapabilityName(request);
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            Logger.LogWarning("CapabilityMatchmaking: capability name missing in request");
            Context.Set("RefusalReason", RefusalReason);
            return NodeStatus.Failure;
        }

        var moduleId = Context.Get<string>("config.Agent.ModuleId")
            ?? Context.Get<string>("ModuleId")
            ?? Context.Get<string>("config.Agent.ModuleName")
            ?? string.Empty;
        Logger.LogInformation(
            "CapabilityMatchmaking: start (module={ModuleId}, capability={Capability}, requiredMode={Mode}, threshold={Threshold:F3})",
            string.IsNullOrWhiteSpace(moduleId) ? "<unknown>" : moduleId,
            capabilityName,
            UseNeo4jForOfferings ? "neo4j" : "aas",
            SimilarityThreshold);

        var requiredProperties = ExtractProperties(request.CapabilityContainer);

        var offeredProperties = await LoadOfferedPropertiesAsync(capabilityName, CancellationToken.None);
        if (offeredProperties == null || offeredProperties.Count == 0)
        {
            Logger.LogWarning("CapabilityMatchmaking: capability {Capability} not offered by module (Neo4j)", capabilityName);
            Context.Set("RefusalReason", RefusalReason);
            return NodeStatus.Failure;
        }

        Logger.LogInformation(
            "CapabilityMatchmaking: loaded properties (required={RequiredCount}, offered={OfferedCount})",
            requiredProperties.Count,
            offeredProperties.Count);

        if (requiredProperties.Count == 0)
        {
            Logger.LogInformation("CapabilityMatchmaking: capability {Capability} has no property constraints", capabilityName);
            PersistSuccess(request, capabilityName, Array.Empty<MatchedProperty>());
            return NodeStatus.Success;
        }

        var embeddingProvider = ResolveEmbeddingProvider();
        var matcher = new PropertyMatcher(Logger, embeddingProvider, SimilarityThreshold);
        var outcome = await matcher.MatchAsync(requiredProperties, offeredProperties, CancellationToken.None);

        if (!outcome.Success)
        {
            var detail = outcome.FailureReason;
            var reason = string.IsNullOrWhiteSpace(RefusalReason)
                ? (string.IsNullOrWhiteSpace(detail) ? "capability_not_found" : detail!)
                : RefusalReason;

            if (!string.IsNullOrWhiteSpace(detail))
            {
                Context.Set("CapabilityMatchmaking.FailureDetail", detail);
            }

            Logger.LogWarning("CapabilityMatchmaking: failed ({Detail}); refusalReason={Reason}", detail, reason);
            Context.Set("RefusalReason", reason);
            Context.Remove(ResultContextKey);
            return NodeStatus.Failure;
        }

        PersistSuccess(request, capabilityName, outcome.Matches);
        // Attempt to enrich the request's capability container with constraints from Neo4j
        try
        {
            await AttachConstraintsFromNeo4jAsync(request, capabilityName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "CapabilityMatchmaking: failed to attach constraints from Neo4j");
        }
        return NodeStatus.Success;
    }

    private async Task AttachConstraintsFromNeo4jAsync(CapabilityRequestContext request, string capabilityName, CancellationToken cancellationToken)
    {
        if (request == null) return;
        var driver = Context.Get<IDriver>("Neo4jDriver");
        var moduleId = Context.Get<string>("config.Agent.ModuleId")
            ?? Context.Get<string>("ModuleId")
            ?? Context.Get<string>("config.Agent.ModuleName")
            ?? string.Empty;

        if (driver == null || string.IsNullOrWhiteSpace(moduleId))
        {
            return;
        }

        var database = Context.Get<string>("config.Neo4j.Database") ?? "neo4j";
        await using var session = driver.AsyncSession(o => o.WithDatabase(database));
        var query = @"
MATCH (a:Asset)-[:PROVIDES_CAPABILITY]->(cap:Capability)-[:HAS_CONSTRAINT]->(c:ConstraintContainer)
WHERE a.shell_id = $moduleShellId AND cap.idShort = $capabilityIdShort
OPTIONAL MATCH (c)-[*1..3]->(child)
RETURN c AS container, collect(DISTINCT child) AS children";

        var cursor = await session.RunAsync(query, new { moduleShellId = moduleId, capabilityIdShort = capabilityName });
        var constraintContainers = new List<SubmodelElementCollection>();
        while (await cursor.FetchAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = cursor.Current["container"].As<INode>();
            var children = cursor.Current["children"].As<List<INode>>() ?? new List<INode>();
            var cId = GetString(node, "idShort") ?? GetString(node, "IdShort") ?? "Constraint";
            var constraintSmc = new SubmodelElementCollection(cId);

            // map simple properties from node
            foreach (var kv in node.Properties)
            {
                if (kv.Key.Equals("idShort", StringComparison.OrdinalIgnoreCase) || kv.Value == null) continue;
                constraintSmc.Add(new Property<string>(kv.Key, kv.Value.ToString() ?? string.Empty));
            }

            // map children nodes as properties or nested CustomConstraint
            foreach (var child in children)
            {
                var childId = GetString(child, "idShort") ?? GetString(child, "IdShort") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(childId)) continue;

                if (string.Equals(childId, "CustomConstraint", StringComparison.OrdinalIgnoreCase))
                {
                    var custom = new SubmodelElementCollection("CustomConstraint");
                    // map child's properties into custom
                    foreach (var kv in child.Properties)
                    {
                        if (kv.Key.Equals("idShort", StringComparison.OrdinalIgnoreCase) || kv.Value == null) continue;
                        custom.Add(new Property<string>(kv.Key, kv.Value.ToString() ?? string.Empty));
                    }
                    constraintSmc.Add(custom);
                    continue;
                }

                // otherwise add as plain property if it has a value
                foreach (var kv in child.Properties)
                {
                    if (kv.Key.Equals("idShort", StringComparison.OrdinalIgnoreCase) || kv.Value == null) continue;
                    constraintSmc.Add(new Property<string>(kv.Key, kv.Value.ToString() ?? string.Empty));
                }
            }

            constraintContainers.Add(constraintSmc);
        }

        if (constraintContainers.Count == 0) return;

        // attach to request.CapabilityContainer
        var container = request.CapabilityContainer ?? new CapabilityContainer(capabilityName + "Container");
        // ensure relations collection exists
        var relations = container.EnsureRelations();
        // create ConstraintSet collection and mark semantic ids so AAS helpers can find them
        var constraintSet = new SubmodelElementCollection("ConstraintSet");
        foreach (var cc in constraintContainers)
        {
            constraintSet.Add(cc);
        }

        relations.Source.Add(constraintSet);
        // persist back
        request.CapabilityContainer = container;
        Context.Set("Planning.CapabilityRequest", request);
    }

    private static string? GetString(INode node, string key)
    {
        if (node.Properties.TryGetValue(key, out var value) && value != null)
        {
            return value.ToString();
        }

        return null;
    }

    private async Task<List<CapabilityPropertyDescriptor>> LoadOfferedPropertiesAsync(string capabilityName, CancellationToken cancellationToken)
    {
        if (!UseNeo4jForOfferings)
        {
            var offered = FindOfferedCapabilityContainer(capabilityName);
            return offered == null ? new List<CapabilityPropertyDescriptor>() : ExtractProperties(offered);
        }

        var query = Context.Get<ICapabilityPropertyQuery>("CapabilityPropertyQuery");
        var moduleId = Context.Get<string>("config.Agent.ModuleId")
            ?? Context.Get<string>("ModuleId")
            ?? Context.Get<string>("config.Agent.ModuleName")
            ?? string.Empty;

        if (query == null || string.IsNullOrWhiteSpace(moduleId))
        {
            // Fallback to AAS offerings if Neo4j is not available.
            Logger.LogWarning(
                "CapabilityMatchmaking: Neo4j CapabilityPropertyQuery unavailable (module={ModuleId}). Falling back to AAS submodel offerings.",
                string.IsNullOrWhiteSpace(moduleId) ? "<unknown>" : moduleId);
            var offered = FindOfferedCapabilityContainer(capabilityName);
            return offered == null ? new List<CapabilityPropertyDescriptor>() : ExtractProperties(offered);
        }

        Logger.LogInformation(
            "CapabilityMatchmaking: querying Neo4j offered properties (module={ModuleId}, capability={Capability})",
            moduleId,
            capabilityName);

        IReadOnlyList<GraphCapabilityPropertyContainer> containers;
        try
        {
            containers = await query.GetCapabilityPropertyContainersAsync(moduleId, capabilityName, cancellationToken);
        }
        catch (Neo4j.Driver.ServiceUnavailableException ex)
        {
            Context.Set(
                "CapabilityMatchmaking.FailureDetail",
                $"neo4j_unavailable: {ex.Message}");
            Logger.LogWarning(
                ex,
                "CapabilityMatchmaking: Neo4j unavailable while loading offerings (module={ModuleId}, capability={Capability}). Falling back to AAS.",
                moduleId,
                capabilityName);

            var offered = FindOfferedCapabilityContainer(capabilityName);
            return offered == null ? new List<CapabilityPropertyDescriptor>() : ExtractProperties(offered);
        }
        catch (Exception ex)
        {
            Context.Set(
                "CapabilityMatchmaking.FailureDetail",
                $"neo4j_error: {ex.Message}");
            Logger.LogWarning(
                ex,
                "CapabilityMatchmaking: Neo4j error while loading offerings (module={ModuleId}, capability={Capability}). Falling back to AAS.",
                moduleId,
                capabilityName);

            var offered = FindOfferedCapabilityContainer(capabilityName);
            return offered == null ? new List<CapabilityPropertyDescriptor>() : ExtractProperties(offered);
        }

        var result = new List<CapabilityPropertyDescriptor>();
        foreach (var container in containers)
        {
            var descriptor = CapabilityPropertyDescriptor.TryCreate(container);
            if (descriptor != null)
            {
                result.Add(descriptor);
            }
        }

        Logger.LogInformation(
            "CapabilityMatchmaking: Neo4j returned containers={ContainerCount}, mappedDescriptors={DescriptorCount}",
            containers.Count,
            result.Count);

        if (result.Count == 0)
        {
            var offered = FindOfferedCapabilityContainer(capabilityName);
            if (offered != null)
            {
                Logger.LogWarning(
                    "CapabilityMatchmaking: Neo4j returned 0 properties for (module={ModuleId}, capability={Capability}). Falling back to AAS offerings.",
                    moduleId,
                    capabilityName);
                return ExtractProperties(offered);
            }
        }

        return result;
    }

    private string ResolveCapabilityName(CapabilityRequestContext request)
    {
        var capability = ResolvePlaceholders(RequiredCapability);
        if (!string.IsNullOrWhiteSpace(capability))
        {
            return capability;
        }

        capability = request.Capability ?? Context.Get<string>("RequiredCapability");
        return capability ?? string.Empty;
    }

    private CapabilityContainer? FindOfferedCapabilityContainer(string capabilityName)
    {
        var submodel = Context.Get<CapabilityDescriptionSubmodel>("CapabilityDescriptionSubmodel")
                      ?? Context.Get<CapabilityDescriptionSubmodel>("AAS.Submodel.CapabilityDescription");
        if (submodel?.CapabilitySet == null)
        {
            Logger.LogWarning("CapabilityMatchmaking: capability description submodel missing");
            return null;
        }

        foreach (var element in submodel.CapabilitySet.OfType<SubmodelElementCollection>())
        {
            var container = new CapabilityContainer(element);
            var name = container.Capability?.IdShort ?? container.GetCapabilityName();
            if (string.Equals(name, capabilityName, StringComparison.OrdinalIgnoreCase))
            {
                return container;
            }
        }

        return null;
    }

    private static List<CapabilityPropertyDescriptor> ExtractProperties(CapabilityContainer container)
    {
        var result = new List<CapabilityPropertyDescriptor>();
        foreach (var section in container.PropertyContainers)
        {
            var descriptor = CapabilityPropertyDescriptor.TryCreate(section);
            if (descriptor != null)
            {
                result.Add(descriptor);
            }
        }

        return result;
    }

    private void PersistSuccess(
        CapabilityRequestContext request,
        string capabilityName,
        IReadOnlyList<MatchedProperty> matches)
    {
        var result = new CapabilityMatchResult(capabilityName, matches);
        Context.Set(RequestContextKey, request);
        Context.Set(ResultContextKey, result);
        Context.Set("LastMatchedCapability", capabilityName);
        Context.Set("MatchedCapability", capabilityName);
        Logger.LogInformation(
            "CapabilityMatchmaking: matched capability {Capability} (properties={PropertyCount})",
            capabilityName,
            matches.Count);

        foreach (var match in matches)
        {
            Logger.LogInformation(
                "CapabilityMatchmaking: result requirement={Requirement} offered={Offered} method={Method} similarity={Similarity:F3}",
                match.Requirement.DisplayName,
                match.Offering.DisplayName,
                match.Method,
                match.Similarity);
        }
    }

    private ITextEmbeddingProvider ResolveEmbeddingProvider()
    {
        try
        {
            var provider = Context.Get<ITextEmbeddingProvider>(EmbeddingProviderKey);
            if (provider != null)
            {
                return provider;
            }
        }
        catch
        {
            // ignore missing keys
        }

        return new OllamaEmbeddingProvider(EmbeddingEndpoint, EmbeddingModel);
    }

    private sealed class PropertyMatcher
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly ITextEmbeddingProvider _embeddingProvider;
        private readonly double _threshold;

        public PropertyMatcher(Microsoft.Extensions.Logging.ILogger logger, ITextEmbeddingProvider embeddingProvider, double threshold)
        {
            _logger = logger;
            _embeddingProvider = embeddingProvider;
            _threshold = threshold <= 0 ? 0.5 : threshold;
        }

        public async Task<PropertyMatchOutcome> MatchAsync(
            IReadOnlyList<CapabilityPropertyDescriptor> required,
            List<CapabilityPropertyDescriptor> offered,
            CancellationToken cancellationToken)
        {
            var matches = new List<MatchedProperty>();
            foreach (var requirement in required)
            {
                if (offered.Count == 0)
                {
                    return PropertyMatchOutcome.CreateFailure(
                        $"No remaining capability properties to satisfy {requirement.DisplayName}",
                        matches);
                }

                var (match, similarity, method) = FindByEquality(requirement, offered);
                if (match == null)
                {
                    (match, similarity, method) = await FindByEmbeddingAsync(requirement, offered, cancellationToken);
                }
                else
                {
                    _logger.LogInformation(
                        "CapabilityMatchmaking: match requirement={Requirement} offered={Offered} method={Method} similarity={Similarity:F3}",
                        requirement.DisplayName,
                        match.DisplayName,
                        method,
                        similarity);
                }

                if (match == null)
                {
                    var top = await DescribeTopCandidatesAsync(requirement, offered, cancellationToken, max: 3);
                    var partnerPairs = DescribePartnerPairs(matches);
                    var reason = string.IsNullOrWhiteSpace(top)
                        ? $"Unable to match property {requirement.DisplayName}"
                        : $"Unable to match property {requirement.DisplayName}. TopCandidates: {top}";

                    if (!string.IsNullOrWhiteSpace(partnerPairs))
                    {
                        reason += $"; MatchedPairs: {partnerPairs}";
                    }

                    return PropertyMatchOutcome.CreateFailure(reason, matches);
                }

                if (!CapabilityPropertyDescriptor.IsCompatible(requirement, match))
                {
                    var partnerPairs = DescribePartnerPairs(matches);
                    var human =
                        $"Property {requirement.DisplayName} incompatible with offered {match.DisplayName} (method={method}, similarity={similarity:F3})" +
                        $"; required={CapabilityPropertyDescriptor.Describe(requirement)}" +
                        $"; offered={CapabilityPropertyDescriptor.Describe(match)}";
                    if (!string.IsNullOrWhiteSpace(partnerPairs))
                    {
                        human += $"; MatchedPairs: {partnerPairs}";
                    }

                    var code = DetermineIncompatibilityCode(requirement, match);
                    var reason = string.IsNullOrWhiteSpace(human) ? code : $"{code}: {human}";
                    return PropertyMatchOutcome.CreateFailure(reason, matches);
                }

                matches.Add(new MatchedProperty(requirement, match, similarity, method));
                offered.Remove(match);
            }

            return PropertyMatchOutcome.CreateSuccess(matches);
        }

        private static string DescribePartnerPairs(IReadOnlyList<MatchedProperty> matches)
        {
            if (matches == null || matches.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(", ", matches.Select(m =>
                $"{m.Requirement.DisplayName}->{m.Offering.DisplayName}({m.Method},{m.Similarity:F3})"));
        }

            private static string DetermineIncompatibilityCode(CapabilityPropertyDescriptor required, CapabilityPropertyDescriptor offered)
            {
                if (required == null || offered == null) return "parameter_not_compatible";

                // Value required but offering is a range -> value out of range
                if (required.Kind == PropertyKind.Value && offered.Kind == PropertyKind.Range)
                {
                    return "parameter_not_in_range";
                }

                // Value required but offering is a list -> not in allowed values
                if (required.Kind == PropertyKind.Value && offered.Kind == PropertyKind.List)
                {
                    return "parameter_not_in_allowed_values";
                }

                // Range required but offering is range but not containing -> range mismatch
                if (required.Kind == PropertyKind.Range && offered.Kind == PropertyKind.Range)
                {
                    return "parameter_range_mismatch";
                }

                // Range required but offering is value outside required range
                if (required.Kind == PropertyKind.Range && offered.Kind == PropertyKind.Value)
                {
                    return "parameter_not_in_range";
                }

                // List requirement but offering doesn't provide required values
                if (required.Kind == PropertyKind.List && offered.Kind == PropertyKind.List)
                {
                    return "parameter_values_mismatch";
                }

                // Fallback
                return "parameter_not_compatible";
            }

        private static (CapabilityPropertyDescriptor? match, double similarity, string method) FindByEquality(
            CapabilityPropertyDescriptor requirement,
            IReadOnlyList<CapabilityPropertyDescriptor> offered)
        {
            var requirementKey = CapabilityPropertyDescriptor.NormalizeIdShort(requirement.ElementIdShort);

            var match = offered.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(requirementKey) &&
                string.Equals(
                    CapabilityPropertyDescriptor.NormalizeIdShort(candidate.ElementIdShort),
                    requirementKey,
                    StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return (match, 1.0, "idShort");
            }

            if (!string.IsNullOrWhiteSpace(requirement.SemanticId))
            {
                match = offered.FirstOrDefault(candidate =>
                    string.Equals(candidate.SemanticId, requirement.SemanticId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return (match, 1.0, "semanticId");
                }
            }

            return (null, 0.0, "none");
        }

        private async Task<(CapabilityPropertyDescriptor? match, double similarity, string method)> FindByEmbeddingAsync(
            CapabilityPropertyDescriptor requirement,
            IReadOnlyList<CapabilityPropertyDescriptor> offered,
            CancellationToken cancellationToken)
        {
            var requirementEmbedding = await requirement.GetEmbeddingAsync(_embeddingProvider, cancellationToken);
            if (requirementEmbedding == null || requirementEmbedding.Length == 0)
            {
                _logger.LogDebug("CapabilityMatchmaking: embedding unavailable for requirement {Property}", requirement.DisplayName);
                return (null, 0.0, "embedding_unavailable");
            }

            CapabilityPropertyDescriptor? best = null;
            double bestScore = double.MinValue;

            var scored = new List<(CapabilityPropertyDescriptor candidate, double score)>();

            foreach (var candidate in offered)
            {
                var candidateEmbedding = await candidate.GetEmbeddingAsync(_embeddingProvider, cancellationToken);
                if (candidateEmbedding == null || candidateEmbedding.Length == 0)
                {
                    continue;
                }

                if (candidateEmbedding.Length != requirementEmbedding.Length)
                {
                    continue;
                }

                var score = CosineSimilarity(requirementEmbedding, candidateEmbedding);
                scored.Add((candidate, score));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best == null || bestScore < _threshold)
            {
                var top = scored
                    .OrderByDescending(s => s.score)
                    .Take(3)
                    .Select(s => $"{s.candidate.DisplayName}={s.score:F3}")
                    .ToArray();

                _logger.LogInformation(
                    "CapabilityMatchmaking: no embedding match for {Property} (best={Score:F3}, threshold={Threshold:F3}, top=[{Top}])",
                    requirement.DisplayName,
                    bestScore,
                    _threshold,
                    string.Join(", ", top));
                return (null, bestScore, "embedding");
            }

            _logger.LogInformation(
                "CapabilityMatchmaking: match requirement={Requirement} offered={Offered} method=embeddings similarity={Score:F3}",
                requirement.DisplayName,
                best.DisplayName,
                bestScore);

            return (best, bestScore, "embeddings");
        }

        private async Task<string> DescribeTopCandidatesAsync(
            CapabilityPropertyDescriptor requirement,
            IReadOnlyList<CapabilityPropertyDescriptor> offered,
            CancellationToken cancellationToken,
            int max)
        {
            try
            {
                var requirementEmbedding = await requirement.GetEmbeddingAsync(_embeddingProvider, cancellationToken);
                if (requirementEmbedding == null || requirementEmbedding.Length == 0)
                {
                    return string.Join(", ", offered.Take(max).Select(o => o.DisplayName));
                }

                var scored = new List<(CapabilityPropertyDescriptor candidate, double score)>();
                foreach (var candidate in offered)
                {
                    var candidateEmbedding = await candidate.GetEmbeddingAsync(_embeddingProvider, cancellationToken);
                    if (candidateEmbedding == null || candidateEmbedding.Length == 0 || candidateEmbedding.Length != requirementEmbedding.Length)
                    {
                        continue;
                    }

                    scored.Add((candidate, CosineSimilarity(requirementEmbedding, candidateEmbedding)));
                }

                return string.Join(", ",
                    scored.OrderByDescending(s => s.score)
                        .Take(max)
                        .Select(s => $"{s.candidate.DisplayName}={s.score:F3}"));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static double CosineSimilarity(double[] left, double[] right)
        {
            double dot = 0;
            double leftNorm = 0;
            double rightNorm = 0;

            for (int i = 0; i < left.Length; i++)
            {
                dot += left[i] * right[i];
                leftNorm += left[i] * left[i];
                rightNorm += right[i] * right[i];
            }

            leftNorm = Math.Sqrt(leftNorm);
            rightNorm = Math.Sqrt(rightNorm);
            if (leftNorm == 0 || rightNorm == 0)
            {
                return 0;
            }

            return dot / (leftNorm * rightNorm);
        }
    }

    private sealed record PropertyMatchOutcome(bool Success, string? FailureReason, IReadOnlyList<MatchedProperty> Matches)
    {
        public static PropertyMatchOutcome CreateFailure(string reason, IReadOnlyList<MatchedProperty>? matches = null)
            => new(false, reason, matches ?? Array.Empty<MatchedProperty>());
        public static PropertyMatchOutcome CreateSuccess(IReadOnlyList<MatchedProperty> matches) => new(true, null, matches);
    }
}

internal sealed record CapabilityMatchResult(string Capability, IReadOnlyList<MatchedProperty> Matches);

internal sealed record MatchedProperty(
    CapabilityPropertyDescriptor Requirement,
    CapabilityPropertyDescriptor Offering,
    double Similarity,
    string Method);

internal sealed class CapabilityPropertyDescriptor
{
    private readonly string? _comment;
    private double[]? _embedding;

    private CapabilityPropertyDescriptor(
        string containerId,
        string elementIdShort,
        string? semanticId,
        PropertyKind kind,
        string? valueType,
        string? fixedValue,
        double? min,
        double? max,
        IReadOnlyList<string> listValues,
        bool isWildcard,
        string? comment,
        double[]? embedding)
    {
        ContainerId = containerId;
        ElementIdShort = elementIdShort;
        SemanticId = semanticId;
        Kind = kind;
        ValueType = valueType;
        FixedValue = fixedValue;
        RangeMin = min;
        RangeMax = max;
        ListValues = listValues;
        IsWildcard = isWildcard;
        _comment = comment;
        _embedding = embedding;
    }

    public string ContainerId { get; }
    public string ElementIdShort { get; }
    public string? SemanticId { get; }
    public PropertyKind Kind { get; }
    public string? ValueType { get; }
    public string? FixedValue { get; }
    public double? RangeMin { get; }
    public double? RangeMax { get; }
    public IReadOnlyList<string> ListValues { get; }
    public bool IsWildcard { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(ElementIdShort) ? ContainerId : ElementIdShort;

    public static string NormalizeIdShort(string? idShort)
    {
        if (string.IsNullOrWhiteSpace(idShort))
        {
            return string.Empty;
        }

        var trimmed = idShort.Trim();

        // Common naming patterns between AAS containers and graph exports:
        // - GripForceContainer -> GripForce
        // - DepthRange -> Depth
        // - ProductIdFixed -> ProductId
        // Keep this conservative and only strip well-known suffixes.
        trimmed = TrimSuffix(trimmed, "Container");
        trimmed = TrimSuffix(trimmed, "Range");
        trimmed = TrimSuffix(trimmed, "List");
        trimmed = TrimSuffix(trimmed, "Fixed");
        return trimmed;
    }

    public static string Describe(CapabilityPropertyDescriptor descriptor)
    {
        if (descriptor == null)
        {
            return "<null>";
        }

        return descriptor.Kind switch
        {
            PropertyKind.Value => $"Value(idShort={descriptor.ElementIdShort}, value={descriptor.FixedValue ?? "<null>"}, type={descriptor.ValueType ?? "<null>"}, wildcard={descriptor.IsWildcard})",
            PropertyKind.Range => $"Range(idShort={descriptor.ElementIdShort}, min={descriptor.RangeMin?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}, max={descriptor.RangeMax?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}, type={descriptor.ValueType ?? "<null>"})",
            PropertyKind.List => $"List(idShort={descriptor.ElementIdShort}, values=[{string.Join(",", descriptor.ListValues)}], type={descriptor.ValueType ?? "<null>"}, wildcard={descriptor.IsWildcard})",
            _ => $"Unknown(idShort={descriptor.ElementIdShort})"
        };
    }

    private static string TrimSuffix(string value, string suffix)
    {
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value.Substring(0, value.Length - suffix.Length)
            : value;
    }

    public static CapabilityPropertyDescriptor? TryCreate(CapabilityPropertyContainerSection section)
    {
        var embedding = TryReadEmbedding(section.Source);

        if (section.Property is Property property)
        {
            var value = property.Value?.Value?.ToString();
            return new CapabilityPropertyDescriptor(
                section.Source.IdShort ?? property.IdShort ?? string.Empty,
                property.IdShort ?? string.Empty,
                ResolveSemanticId(property.SemanticId),
                PropertyKind.Value,
                property.ValueType,
                value,
                null,
                null,
                Array.Empty<string>(),
                string.Equals(value, "*", StringComparison.Ordinal),
                ExtractComment(section.Comment),
                embedding);
        }

        if (section.Range is RangeElement range && range.Value != null)
        {
            var min = TryParseDouble(range.Value.Min?.Value?.ToString());
            var max = TryParseDouble(range.Value.Max?.Value?.ToString());
            return new CapabilityPropertyDescriptor(
                section.Source.IdShort ?? range.IdShort ?? string.Empty,
                range.IdShort ?? string.Empty,
                ResolveSemanticId(range.SemanticId),
                PropertyKind.Range,
                range.ValueType,
                null,
                min,
                max,
                Array.Empty<string>(),
                false,
                ExtractComment(section.Comment),
                embedding);
        }

        if (section.PropertyList is SubmodelElementList list)
        {
            var values = new List<string>();
            foreach (var item in list)
            {
                if (item is Property entry && entry.Value?.Value != null)
                {
                    values.Add(entry.Value.Value.ToString() ?? string.Empty);
                }
            }

            return new CapabilityPropertyDescriptor(
                section.Source.IdShort ?? list.IdShort ?? string.Empty,
                list.IdShort ?? string.Empty,
                ResolveSemanticId(list.SemanticId),
                PropertyKind.List,
                list.ValueTypeListElement,
                null,
                null,
                null,
                values,
                values.Any(v => string.Equals(v, "*", StringComparison.Ordinal)),
                ExtractComment(section.Comment),
                embedding);
        }

        return null;
    }

    public static CapabilityPropertyDescriptor? TryCreate(GraphCapabilityPropertyContainer container)
    {
        if (container == null)
        {
            return null;
        }

        var idShort = container.IdShort ?? string.Empty;
        if (string.IsNullOrWhiteSpace(idShort))
        {
            return null;
        }

        var isWildcard = string.Equals(container.Value, "*", StringComparison.Ordinal)
            || container.ListValues.Any(v => string.Equals(v, "*", StringComparison.Ordinal));

        PropertyKind kind;
        if (container.Min.HasValue || container.Max.HasValue)
        {
            kind = PropertyKind.Range;
        }
        else if (container.ListValues.Count > 0)
        {
            kind = PropertyKind.List;
        }
        else
        {
            kind = PropertyKind.Value;
        }

        var elementIdShort = NormalizeIdShort(idShort);

        return new CapabilityPropertyDescriptor(
            containerId: idShort,
            elementIdShort: string.IsNullOrWhiteSpace(elementIdShort) ? idShort : elementIdShort,
            semanticId: container.SemanticId,
            kind: kind,
            valueType: container.ValueType,
            fixedValue: container.Value,
            min: container.Min,
            max: container.Max,
            listValues: container.ListValues,
            isWildcard: isWildcard,
            comment: null,
            embedding: container.Embedding);
    }

    public async Task<double[]?> GetEmbeddingAsync(ITextEmbeddingProvider provider, CancellationToken cancellationToken)
    {
        if (_embedding != null)
        {
            return _embedding;
        }

        var identity = BuildIdentityText();
        if (string.IsNullOrWhiteSpace(identity))
        {
            return null;
        }

        _embedding = await provider.GetEmbeddingAsync(identity, cancellationToken);
        return _embedding;
    }

    private string BuildIdentityText()
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ElementIdShort))
        {
            builder.Append(ElementIdShort);
        }

        if (!string.IsNullOrWhiteSpace(SemanticId))
        {
            builder.Append(" | ").Append(SemanticId);
        }

        if (!string.IsNullOrWhiteSpace(ValueType))
        {
            builder.Append(" | ").Append(ValueType);
        }

        if (!string.IsNullOrWhiteSpace(_comment))
        {
            builder.Append(" | ").Append(_comment);
        }

        return builder.ToString();
    }

    private static double[]? TryReadEmbedding(SubmodelElementCollection source)
    {
        if (source == null)
        {
            return null;
        }

        foreach (var element in source.Value.Value)
        {
            if (element is Property property &&
                string.Equals(property.IdShort, "embedding", StringComparison.OrdinalIgnoreCase))
            {
                var raw = property.Value?.Value?.ToString();
                return Neo4jCapabilityPropertyQuery.TryParseEmbedding(raw);
            }
        }

        return null;
    }

    public static bool IsCompatible(CapabilityPropertyDescriptor required, CapabilityPropertyDescriptor offered)
    {
        if (required.IsWildcard || offered.IsWildcard)
        {
            return true;
        }

        return required.Kind switch
        {
            PropertyKind.Value => MatchValueRequirement(required, offered),
            PropertyKind.Range => MatchRangeRequirement(required, offered),
            PropertyKind.List => MatchListRequirement(required, offered),
            _ => false
        };
    }

    private static bool MatchValueRequirement(CapabilityPropertyDescriptor required, CapabilityPropertyDescriptor offered)
    {
        if (string.IsNullOrWhiteSpace(required.FixedValue))
        {
            return true;
        }

        return offered.Kind switch
        {
            PropertyKind.Value => CompareValues(required.FixedValue, offered.FixedValue, required.IsNumeric || offered.IsNumeric),
            PropertyKind.Range => CheckValueInRange(required.FixedValue, offered.RangeMin, offered.RangeMax),
            PropertyKind.List => offered.ListValues.Any(v => CompareValues(required.FixedValue, v, required.IsNumeric || offered.IsNumeric)),
            _ => false
        };
    }

    private static bool MatchRangeRequirement(CapabilityPropertyDescriptor required, CapabilityPropertyDescriptor offered)
    {
        return offered.Kind switch
        {
            PropertyKind.Range => RangeContains(offered.RangeMin, offered.RangeMax, required.RangeMin, required.RangeMax),
            PropertyKind.Value => CheckValueInRange(offered.FixedValue, required.RangeMin, required.RangeMax),
            PropertyKind.List => offered.ListValues.Any(v => CheckValueInRange(v, required.RangeMin, required.RangeMax)),
            _ => false
        };
    }

    private static bool MatchListRequirement(CapabilityPropertyDescriptor required, CapabilityPropertyDescriptor offered)
    {
        if (required.ListValues.Count == 0)
        {
            return true;
        }

        return offered.Kind switch
        {
            PropertyKind.List => offered.ListValues.Any(v => required.ListValues.Any(r => CompareValues(r, v, required.IsNumeric || offered.IsNumeric))),
            PropertyKind.Value => required.ListValues.Any(v => CompareValues(v, offered.FixedValue, required.IsNumeric || offered.IsNumeric)),
            PropertyKind.Range => required.ListValues.Any(v => CheckValueInRange(v, offered.RangeMin, offered.RangeMax)),
            _ => false
        };
    }

    private static bool CompareValues(string? left, string? right, bool numeric)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (numeric)
        {
            var leftValue = TryParseDouble(left);
            var rightValue = TryParseDouble(right);
            return leftValue.HasValue && rightValue.HasValue && Math.Abs(leftValue.Value - rightValue.Value) <= 0.0001;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CheckValueInRange(string? value, double? min, double? max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var numeric = TryParseDouble(value);
        if (!numeric.HasValue)
        {
            return false;
        }

        if (min.HasValue && numeric.Value < min.Value)
        {
            return false;
        }

        if (max.HasValue && numeric.Value > max.Value)
        {
            return false;
        }

        return true;
    }

    private static bool RangeContains(double? outerMin, double? outerMax, double? innerMin, double? innerMax)
    {
        if (!innerMin.HasValue && !innerMax.HasValue)
        {
            return true;
        }

        if (innerMin.HasValue && outerMin.HasValue && innerMin.Value < outerMin.Value)
        {
            return false;
        }

        if (innerMax.HasValue && outerMax.HasValue && innerMax.Value > outerMax.Value)
        {
            return false;
        }

        if (outerMin.HasValue && !innerMin.HasValue)
        {
            return false;
        }

        if (outerMax.HasValue && !innerMax.HasValue)
        {
            return false;
        }

        return true;
    }

    private bool IsNumeric => ValueType != null && ValueType.Contains("double", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveSemanticId(IReference? reference)
    {
        var key = reference?.Keys?.FirstOrDefault();
        return string.IsNullOrWhiteSpace(key?.Value) ? null : key.Value;
    }

    private static string? ExtractComment(MultiLanguageProperty? comment)
    {
        return comment?.Value?.ToString();
    }

    private static double? TryParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

internal enum PropertyKind
{
    Value,
    Range,
    List
}

