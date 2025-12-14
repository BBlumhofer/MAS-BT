using System;
using System.Collections.Generic;
using System.Linq;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Tasks;
using I40Sharp.Messaging.Core;
using Xunit;

namespace MAS_BT.Tests
{
    public class PairwiseSimilarityTests
    {
        [Fact]
        public async Task CalcPairwiseSimilarity_ComputesCorrectNumberOfPairs()
        {
            var ctx = new BTContext();
            var node = new CalcPairwiseSimilarityNode();
            node.Initialize(ctx, NullLogger.Instance);

            // Prepare 4 simple embeddings (dimension 2)
            var embeddings = new List<double[]>
            {
                new double[] {1,0},
                new double[] {0,1},
                new double[] {1,1},
                new double[] {0.5,0.5}
            };
            var names = new List<string> {"A","B","C","D"};
            ctx.Set("Embeddings", embeddings);
            ctx.Set("CapabilityNames", names);

            var status = await node.Execute();
            Assert.Equal(NodeStatus.Success, status);

            var pairs = ctx.Get<List<(int I,int J,string A,string B,double Similarity)>>("PairwiseSimilarities");
            Assert.NotNull(pairs);
            // expected n*(n-1)/2 = 6
            Assert.Equal(6, pairs.Count);
        }

        [Fact]
        public async Task BuildPairwiseSimilarityResponse_ConstructsMessage()
        {
            var ctx = new BTContext();
            var node = new BuildPairwiseSimilarityResponseNode();
            node.Initialize(ctx, NullLogger.Instance);

            // fake request message minimal
            var msg = new I40MessageBuilder()
                .From("Requester","RequesterRole")
                .To("SimilarityAnalysisAgent_phuket","AIAgent")
                .WithType("calcSimilarity")
                .WithConversationId(Guid.NewGuid().ToString())
                .Build();

            ctx.Set("CalcSimilarityTargetMessage", msg);

            var pairs = new List<(int I,int J,string A,string B,double Similarity)>
            {
                (0,1,"A","B",0.9),
                (0,2,"A","C",0.7)
            };
            ctx.Set("PairwiseSimilarities", pairs);

            var status = await node.Execute();
            Assert.Equal(NodeStatus.Success, status);

            var response = ctx.Get<I40Sharp.Messaging.Models.I40Message>("ResponseMessage");
            Assert.NotNull(response);
            Assert.Equal("informConfirm", response.Frame?.Type);
            Assert.NotNull(response.InteractionElements);
            // There should be one element: SimilarityMatrix (collection)
            Assert.Contains(response.InteractionElements, e => e.IdShort == "SimilarityMatrix");
        }
    }
}
