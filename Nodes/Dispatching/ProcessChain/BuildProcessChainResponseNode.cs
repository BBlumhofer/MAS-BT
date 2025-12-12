using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using ProcessChainModel = AasSharpClient.Models.ProcessChain.ProcessChain;
using RequiredCapabilityModel = AasSharpClient.Models.ProcessChain.RequiredCapability;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class BuildProcessChainResponseNode : BTNode
{
    public BuildProcessChainResponseNode() : base("BuildProcessChainResponse") { }

    public override Task<NodeStatus> Execute()
    {
        var negotiation = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        if (negotiation == null)
        {
            Logger.LogError("BuildProcessChainResponse: negotiation context missing");
            return Task.FromResult(NodeStatus.Failure);
        }

        var processChain = new ProcessChainModel();
        var requirementIndex = 0;
        foreach (var requirement in negotiation.Requirements)
        {
            var requiredCapability = new RequiredCapabilityModel($"RequiredCapability_{++requirementIndex}");
            requiredCapability.SetInstanceIdentifier(requirement.RequirementId);
            requiredCapability.SetRequiredCapabilityReference(CreateCapabilityReference(requirement.Capability));

            foreach (var offer in requirement.CapabilityOffers)
            {
                requiredCapability.AddCapabilityOffer(offer);
            }

            processChain.AddRequiredCapability(requiredCapability);
        }

        var success = negotiation.HasCompleteProcessChain;
        Context.Set("ProcessChain.Result", processChain);
        Context.Set("ProcessChain.Success", success);
        Logger.LogInformation("BuildProcessChainResponse: built process chain with {Count} requirements (success={Success})", requirementIndex, success);
        return Task.FromResult(NodeStatus.Success);
    }

        private static Reference CreateCapabilityReference(string capability)
        {
            var keys = new List<IKey>
            {
                new Key(KeyType.GlobalReference, capability ?? string.Empty)
            };

            return new Reference(keys)
            {
                Type = ReferenceType.ExternalReference
            };
        }
}
