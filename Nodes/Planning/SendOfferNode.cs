using System.Text.Json;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using AasSharpClient.Models.ManufacturingSequence;
using AasSharpClient.Models.ProcessChain;
using AasSharpClient.Models;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SendOffer - stub: publishes a simple offer message (inform) if MessagingClient present.
/// </summary>
public class SendOfferNode : BTNode
{
    public string ReceiverId { get; set; } = "broadcast";

    public SendOfferNode() : base("SendOffer") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var offer = Context.Get<object>("CurrentOffer");

        if (client == null)
        {
            Logger.LogWarning("SendOffer: MessagingClient missing, skipping publish");
            return NodeStatus.Success;
        }

        var conv = Context.Get<string>("ConversationId") ?? System.Guid.NewGuid().ToString();

        var builder = new I40MessageBuilder()
            .From("PlanningAgent", "PlanningAgent")
            .To(ReceiverId, "ProductAgent")
            .WithType(I40MessageTypes.PROPOSAL)
            .WithConversationId(conv);

        // If a full plan was built earlier in the planning flow, send its OfferedCapability (preserves CapabilitySequence)
        var plan = Context.Get<MAS_BT.Nodes.Planning.ProcessChain.CapabilityOfferPlan>("Planning.CapabilityOffer");
        if (plan != null && plan.OfferedCapability != null)
        {
            builder.AddElement(plan.OfferedCapability);
            var messagePlan = builder.Build();
            var nsPlan = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var offerTopicPlan = $"/{nsPlan}/Offer";
            await client.PublishAsync(messagePlan, offerTopicPlan);
            Logger.LogInformation("SendOffer: published planned offer {OfferId} to topic {Topic}", plan.OfferId, offerTopicPlan);
            Context.Set("Planning.CapabilityOffer", null);
            return NodeStatus.Success;
        }

        // attach offer as SubmodelElement if possible
        if (offer is SubmodelElement sme)
        {
            builder.AddElement(sme);
        }
        else
        {
            // Convert generic offer into an OfferedCapability submodel element so dispatcher can parse fields reliably.
            try
            {
                var payload = offer ?? "offer";
                var json = JsonSerializer.Serialize(payload);
                using var doc = JsonDocument.Parse(json);
                var oc = new OfferedCapability("OfferedCapability");
                ManufacturingOfferedCapabilitySequence? offeredSequence = null;

                // InstanceIdentifier: prefer explicit payload fields, otherwise use conversation id
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("InstanceIdentifier", out var iid) && iid.ValueKind == JsonValueKind.String)
                {
                    oc.InstanceIdentifier.Value = new PropertyValue<string>(iid.GetString() ?? string.Empty);
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("IdentifierValue", out var idv) && idv.ValueKind == JsonValueKind.String)
                {
                    oc.InstanceIdentifier.Value = new PropertyValue<string>(idv.GetString() ?? string.Empty);
                }
                else
                {
                    oc.InstanceIdentifier.Value = new PropertyValue<string>(conv ?? System.Guid.NewGuid().ToString());
                }

                // Station
                oc.Station.Value = new PropertyValue<string>(ReceiverId ?? string.Empty);

                // MatchingScore
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("MatchingScore", out var scoreElem) && scoreElem.ValueKind == JsonValueKind.Number)
                {
                    if (scoreElem.TryGetDouble(out var score))
                    {
                        oc.MatchingScore.Value = new PropertyValue<double>(score);
                    }
                }
                else
                {
                    oc.MatchingScore.Value = new PropertyValue<double>(1.0);
                }

                // Cost
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Cost", out var costElem) && costElem.ValueKind == JsonValueKind.Number)
                {
                    if (costElem.TryGetDouble(out var cost))
                    {
                        oc.SetCost(cost);
                    }
                }
                // Actions: if payload contains an Actions array, materialize Action elements
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Actions", out var actionsElem) && actionsElem.ValueKind == JsonValueKind.Array)
                {
                    try
                    {
                        var idx = 1;
                        foreach (var actionElem in actionsElem.EnumerateArray())
                        {
                            string title = actionElem.ValueKind == JsonValueKind.Object && actionElem.TryGetProperty("ActionTitle", out var at) && at.ValueKind == JsonValueKind.String
                                ? at.GetString() ?? string.Empty
                                : (actionElem.ValueKind == JsonValueKind.String ? actionElem.GetString() ?? string.Empty : $"Action_{idx}");

                            // Status mapping: accept string like "planned"/"open" or numeric; default to PLANNED
                            ActionStatusEnum status = ActionStatusEnum.PLANNED;
                            if (actionElem.ValueKind == JsonValueKind.Object && actionElem.TryGetProperty("Status", out var st) && st.ValueKind == JsonValueKind.String)
                            {
                                if (Enum.TryParse<ActionStatusEnum>(st.GetString(), true, out var parsed))
                                {
                                    status = parsed;
                                }
                                else
                                {
                                    // Accept 'open' -> PLANNED
                                    status = ActionStatusEnum.PLANNED;
                                }
                            }

                            // Transport-specific: Materialize nested capability sequence entries if present
                            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                                (doc.RootElement.TryGetProperty("CapabilitySequence", out var capSeq) || doc.RootElement.TryGetProperty("CapabilitiesSequence", out capSeq))
                                && capSeq.ValueKind == JsonValueKind.Array)
                            {
                                try
                                {
                                    offeredSequence ??= new ManufacturingOfferedCapabilitySequence();
                                    foreach (var seqEntry in capSeq.EnumerateArray())
                                    {
                                        if (seqEntry.ValueKind != JsonValueKind.Object) continue;

                                        var nested = new OfferedCapability("OfferedCapability");

                                        // OfferedCapabilityReference: may be an object or string
                                        if (seqEntry.TryGetProperty("OfferedCapabilityReference", out var refElem))
                                        {
                                            if (refElem.ValueKind == JsonValueKind.Object && refElem.TryGetProperty("value", out var keys) && keys.ValueKind == JsonValueKind.Array)
                                            {
                                                // try to extract first key value
                                                var first = keys.EnumerateArray().FirstOrDefault();
                                                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("value", out var kv) && kv.ValueKind == JsonValueKind.String)
                                                {
                                                    var modelRef = kv.GetString() ?? string.Empty;
                                                    nested.OfferedCapabilityReference.Value = new ReferenceElementValue(new BaSyx.Models.AdminShell.Reference(new[] { new BaSyx.Models.AdminShell.Key(BaSyx.Models.AdminShell.KeyType.GlobalReference, modelRef) }) { Type = BaSyx.Models.AdminShell.ReferenceType.ExternalReference });
                                                }
                                            }
                                            else if (refElem.ValueKind == JsonValueKind.String)
                                            {
                                                var modelRef = refElem.GetString() ?? string.Empty;
                                                nested.OfferedCapabilityReference.Value = new ReferenceElementValue(new BaSyx.Models.AdminShell.Reference(new[] { new BaSyx.Models.AdminShell.Key(BaSyx.Models.AdminShell.KeyType.GlobalReference, modelRef) }) { Type = BaSyx.Models.AdminShell.ReferenceType.ExternalReference });
                                            }
                                        }

                                        // InstanceIdentifier
                                        if (seqEntry.TryGetProperty("InstanceIdentifier", out var seqIid) && seqIid.ValueKind == JsonValueKind.String)
                                        {
                                            nested.InstanceIdentifier.Value = new PropertyValue<string>(seqIid.GetString() ?? string.Empty);
                                        }

                                        // MatchingScore
                                        if (seqEntry.TryGetProperty("MatchingScore", out var seqMs) && seqMs.ValueKind == JsonValueKind.Number && seqMs.TryGetDouble(out var seqMscore))
                                        {
                                            nested.MatchingScore.Value = new PropertyValue<double>(seqMscore);
                                        }

                                        // Station
                                        if (seqEntry.TryGetProperty("Station", out var seqStn) && seqStn.ValueKind == JsonValueKind.String)
                                        {
                                            nested.Station.Value = new PropertyValue<string>(seqStn.GetString() ?? string.Empty);
                                        }

                                        // EarliestSchedulingInformation
                                        if (seqEntry.TryGetProperty("EarliestSchedulingInformation", out var esi) && esi.ValueKind == JsonValueKind.Object)
                                        {
                                            if (esi.TryGetProperty("StartDateTime", out var esiStartElem) && esiStartElem.ValueKind == JsonValueKind.String && DateTime.TryParse(esiStartElem.GetString(), null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var esiStartDt))
                                            {
                                                var esiEndDt = DateTime.MinValue;
                                                if (esi.TryGetProperty("EndDateTime", out var esiEndElem) && esiEndElem.ValueKind == JsonValueKind.String)
                                                {
                                                    DateTime.TryParse(esiEndElem.GetString(), null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out esiEndDt);
                                                }
                                                TimeSpan esiSetup = TimeSpan.Zero;
                                                if (esi.TryGetProperty("SetupTime", out var esiSetupElem) && esiSetupElem.ValueKind == JsonValueKind.String && TimeSpan.TryParse(esiSetupElem.GetString(), out var esiSu))
                                                {
                                                    esiSetup = esiSu;
                                                }
                                                TimeSpan esiCycle = TimeSpan.Zero;
                                                if (esi.TryGetProperty("CycleTime", out var esiCycleElem) && esiCycleElem.ValueKind == JsonValueKind.String && TimeSpan.TryParse(esiCycleElem.GetString(), out var esiCy))
                                                {
                                                    esiCycle = esiCy;
                                                }
                                                if (esiStartDt != DateTime.MinValue && esiEndDt != DateTime.MinValue && esiSetup != TimeSpan.Zero && esiCycle != TimeSpan.Zero)
                                                {
                                                    nested.SetEarliestScheduling(esiStartDt.ToUniversalTime(), esiEndDt.ToUniversalTime(), esiSetup, esiCycle);
                                                }
                                            }
                                        }

                                        // Actions inside sequence entry
                                        if (seqEntry.TryGetProperty("Actions", out var seqActions) && seqActions.ValueKind == JsonValueKind.Array)
                                        {
                                            var seqActionIdx = 1;
                                            foreach (var seqActionElem in seqActions.EnumerateArray())
                                            {
                                                if (seqActionElem.ValueKind != JsonValueKind.Object) continue;
                                                var seqActionTitle = seqActionElem.TryGetProperty("ActionTitle", out var seqAt) && seqAt.ValueKind == JsonValueKind.String ? seqAt.GetString() ?? $"Action_{seqActionIdx}" : $"Action_{seqActionIdx}";
                                                var seqMachine = seqActionElem.TryGetProperty("MachineName", out var seqMn) && seqMn.ValueKind == JsonValueKind.String ? seqMn.GetString() ?? string.Empty : string.Empty;
                                                var seqActionObj = new AasSharpClient.Models.Action($"Action_{seqActionIdx}", seqActionTitle, ActionStatusEnum.PLANNED, null, null, null, null, seqMachine);
                                                nested.AddAction(seqActionObj);
                                                seqActionIdx++;
                                            }
                                        }

                                        // Cost
                                        if (seqEntry.TryGetProperty("Cost", out var costElemSeq) && costElemSeq.ValueKind == JsonValueKind.Number && costElemSeq.TryGetDouble(out var costValSeq))
                                        {
                                            nested.SetCost(costValSeq);
                                        }

                                        // SequencePlacement
                                        if (seqEntry.TryGetProperty("SequencePlacement", out var seqPlacementElem) && seqPlacementElem.ValueKind == JsonValueKind.String)
                                        {
                                            nested.SetSequencePlacement(seqPlacementElem.GetString() ?? string.Empty);
                                        }

                                        // Add nested capability to explicit sequence element
                                        offeredSequence.AddCapability(nested);
                                                            }
                                                        }
                                                        catch { }
                                                    }

                                                    string machine = actionElem.ValueKind == JsonValueKind.Object && actionElem.TryGetProperty("MachineName", out var mn) && mn.ValueKind == JsonValueKind.String
                                                        ? mn.GetString() ?? string.Empty
                                                        : ReceiverId ?? string.Empty;

                                                    // InputParameters: object of key->value
                                                    InputParameters? inputParams = null;
                                                    if (actionElem.ValueKind == JsonValueKind.Object && actionElem.TryGetProperty("InputParameters", out var ip) && ip.ValueKind == JsonValueKind.Object)
                                                    {
                                                        inputParams = new InputParameters();
                                                        foreach (var prop in ip.EnumerateObject())
                                                        {
                                                            try
                                                            {
                                                                inputParams.SetParameter(prop.Name, prop.Value.GetRawText().Trim('"'));
                                                            }
                                                            catch { }
                                                        }
                                                    }

                                                    var action = new AasSharpClient.Models.Action($"Action_{idx}", title, status, inputParams, null, null, null, machine);
                                                    oc.AddAction(action);
                                                    idx++;
                                                }
                                            }
                                            catch { }
                                        }
                                        if (offeredSequence != null && offeredSequence.Any())
                                        {
                                            offeredSequence.AddCapability(oc);
                                            builder.AddElement(offeredSequence);
                                        }
                                        else
                                        {
                                            builder.AddElement(oc);
                                        }
                                    }
                                    catch
                                    {
                                        // Fallback: send JSON payload as property if anything goes wrong
                                        var payload = offer ?? "offer";
                                        var json = JsonSerializer.Serialize(payload);
                                        var prop = new Property<string>("OfferPayload")
                                        {
                                            Value = new PropertyValue<string>(json)
                                        };
                                        builder.AddElement(prop);
                                    }
                                }
                                var message = builder.Build();
                                var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
                                var offerTopic = $"/{ns}/Offer";
                                await client.PublishAsync(message, offerTopic);
                                Logger.LogInformation("SendOffer: published offer to topic {Topic} for receiver={Receiver}", offerTopic, ReceiverId);
                                return NodeStatus.Success;
                            }
                        }
