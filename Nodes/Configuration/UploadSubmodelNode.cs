using System;
using System.Linq;
using System.Threading.Tasks;
using BaSyx.Clients.AdminShell.Http;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Configuration
{
    /// <summary>
    /// UploadSubmodel - Generic node to upload a submodel to a Submodel repository.
    /// It can take a submodel element (e.g. `AasSharpClient.Models.ProcessChain.ProcessChain`) from the blackboard
    /// and wrap it into a Submodel, upload it and optionally add a reference to the agent's AAS.
    /// </summary>
    public class UploadSubmodelNode : BTNode
    {
        // Key in context where the element/submodel is stored (e.g. "ProcessChain.Result")
        public string ContextKey { get; set; } = "ProcessChain.Result";
        // Optional explicit submodel id
        public string SubmodelId { get; set; } = string.Empty;
        // idShort for the submodel
        public string SubmodelIdShort { get; set; } = "ProcessChain";
        // Prefix for generated id when SubmodelId not provided
        public string SubmodelIdPrefix { get; set; } = "processChain";
        public string SubmodelRepositoryEndpoint { get; set; } = string.Empty;
        public string ShellRepositoryEndpoint { get; set; } = string.Empty;
        public bool AddReferenceToShell { get; set; } = true;

        public UploadSubmodelNode() : base("UploadSubmodel") { }

        public override async Task<NodeStatus> Execute()
        {
            // Resolve endpoints
            var submodelRepo = ResolveSubmodelEndpoint();
            if (string.IsNullOrWhiteSpace(submodelRepo))
            {
                Logger.LogError("UploadSubmodel: Submodel repository endpoint not configured");
                return NodeStatus.Failure;
            }

            // Get candidate object from context
            object? candidate = null;
            try { candidate = Context.Get<object>(ContextKey); } catch { }
            if (candidate == null && Context.Has("LastReceivedMessage"))
            {
                var msg = Context.Get<I40Message>("LastReceivedMessage");
                candidate = ExtractElementFromMessage(msg);
            }

            if (candidate == null)
            {
                Logger.LogWarning("UploadSubmodel: No element found under context key '{Key}'", ContextKey);
                return NodeStatus.Failure;
            }

            // Build Submodel
            var smId = !string.IsNullOrWhiteSpace(SubmodelId) ? SubmodelId : $"https://smartfactory.de/submodels/{SubmodelIdPrefix}/{Guid.NewGuid()}";
            var submodel = new Submodel(SubmodelIdShort ?? "ProcessChain", new Identifier(smId));

            // If candidate already is a Submodel, upload directly
            if (candidate is Submodel existingSubmodel)
            {
                submodel = existingSubmodel;
                if (submodel.Id == null || string.IsNullOrWhiteSpace(submodel.Id.Id))
                {
                    submodel.Id = new Identifier(smId);
                }
            }
            else if (candidate is ISubmodelElement element)
            {
                submodel.SubmodelElements.Add(element);
            }
            else if (candidate is System.Collections.IEnumerable en)
            {
                foreach (var it in en)
                {
                    if (it is ISubmodelElement se)
                        submodel.SubmodelElements.Add(se);
                }
            }
            else
            {
                // try to serialize to a Property wrapper
                var prop = new Property<string>("Value") { Value = new PropertyValue<string>(candidate.ToString() ?? string.Empty) };
                submodel.SubmodelElements.Add(prop);
            }

            try
            {
                using var smClient = new SubmodelRepositoryHttpClient(new Uri(submodelRepo));
                var result = await smClient.CreateSubmodelAsync(submodel);
                if (!result.Success)
                {
                    Logger.LogError("UploadSubmodel: CreateSubmodel failed: {Msg}", result.Messages);
                    return NodeStatus.Failure;
                }

                Logger.LogInformation("UploadSubmodel: Submodel uploaded: {Id}", smId);
                Context.Set("Submodel.LastUploadedId", smId);

                if (AddReferenceToShell)
                {
                    var shell = Context.Has("shell") ? Context.Get<IAssetAdministrationShell>("shell") : (Context.Has("AAS.Shell") ? Context.Get<IAssetAdministrationShell>("AAS.Shell") : null);
                    var shellRepo = ResolveShellEndpoint();
                    if (shell != null && !string.IsNullOrWhiteSpace(shellRepo))
                    {
                        try
                        {
                            // Create a typed Reference<Submodel> from the uploaded submodel (ensures correct key type/structure)
                            var typedRef = new Reference<Submodel>(submodel);

                            using var aasClient = new AssetAdministrationShellRepositoryHttpClient(new Uri(shellRepo));

                            // Prefer fetching the current shell from the repository to preserve all fields/metadata
                            IAssetAdministrationShell existingShell = shell;
                            try
                            {
                                var getRes = await aasClient.RetrieveAssetAdministrationShellAsync(shell.Id);
                                if (getRes != null && getRes.Success && getRes.Entity != null)
                                {
                                    existingShell = getRes.Entity;
                                }
                            }
                            catch (Exception exGet)
                            {
                                Logger.LogDebug(exGet, "UploadSubmodel: Could not retrieve shell from repository, proceeding with local shell instance");
                            }

                            // Ensure concrete instance so we can set the SubmodelReferences collection
                            if (existingShell is BaSyx.Models.AdminShell.AssetAdministrationShell concreteExisting)
                            {
                                var refs = concreteExisting.SubmodelReferences?.ToList() ?? new System.Collections.Generic.List<IReference<ISubmodel>>();
                                // Avoid duplicate references (guard null entries)
                                var already = refs.Any(r => r != null && Reference.IsEqual(r, typedRef));
                                if (!already)
                                {
                                    refs.Add(typedRef);
                                    concreteExisting.SubmodelReferences = refs;
                                }

                                // Log JSON payload for debugging server 400 responses
                                try
                                {
                                    var json = System.Text.Json.JsonSerializer.Serialize(concreteExisting, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                                    Logger.LogDebug("UploadSubmodel: Shell JSON payload: {Json}", json);
                                }
                                catch (Exception exSer)
                                {
                                    Logger.LogDebug(exSer, "UploadSubmodel: Failed to serialize shell for debug");
                                }

                                var update = await aasClient.UpdateAssetAdministrationShellAsync(concreteExisting.Id, concreteExisting);
                                if (!update.Success)
                                {
                                    Logger.LogWarning("UploadSubmodel: Failed to update shell with new submodel reference: {Msg}", update.Messages);
                                }
                                else
                                {
                                    Context.Set("AAS.Shell", concreteExisting);
                                    Context.Set("shell", concreteExisting);
                                    Logger.LogInformation("UploadSubmodel: Added submodel reference to shell");
                                }
                            }
                            else
                            {
                                Logger.LogWarning("UploadSubmodel: Shell instance is not mutable; cannot add submodel reference");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "UploadSubmodel: Error updating shell");
                        }
                    }
                }

                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "UploadSubmodel: Exception during upload");
                return NodeStatus.Failure;
            }
        }

        private object? ExtractElementFromMessage(I40Message? msg)
        {
            if (msg == null) return null;
            if (msg.InteractionElements == null) return null;
            // Prefer a ProcessChain typed element
            var pc = msg.InteractionElements.OfType<AasSharpClient.Models.ProcessChain.ProcessChain>().FirstOrDefault();
            if (pc != null) return pc;
            // Otherwise prefer a collection with idShort ProcessChain
            var coll = msg.InteractionElements.OfType<SubmodelElementCollection>().FirstOrDefault(c => string.Equals(c.IdShort, "ProcessChain", StringComparison.OrdinalIgnoreCase));
            if (coll != null) return coll;
            // Fallback: return first element
            return msg.InteractionElements.FirstOrDefault();
        }

        private string ResolveSubmodelEndpoint()
        {
            if (!string.IsNullOrWhiteSpace(SubmodelRepositoryEndpoint))
                return ResolvePlaceholders(SubmodelRepositoryEndpoint);

            return Context.Get<string>("config.AAS.SubmodelRepositoryEndpoint") ?? Context.Get<string>("AAS.SubmodelRepositoryEndpoint") ?? string.Empty;
        }

        private string ResolveShellEndpoint()
        {
            if (!string.IsNullOrWhiteSpace(ShellRepositoryEndpoint))
                return ResolvePlaceholders(ShellRepositoryEndpoint);

            return Context.Get<string>("config.AAS.ShellRepositoryEndpoint") ?? Context.Get<string>("AAS.ShellRepositoryEndpoint") ?? string.Empty;
        }
    }
}
