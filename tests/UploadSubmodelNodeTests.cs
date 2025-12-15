using System.Threading.Tasks;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using MAS_BT.Nodes.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests
{
    public class UploadSubmodelNodeTests
    {
        [Fact]
        public async Task Execute_NoSubmodelRepositoryConfigured_ReturnsFailure()
        {
            var context = new BTContext(NullLogger<BTContext>.Instance);
            var node = new UploadSubmodelNode { Context = context };

            var status = await node.Execute();

            Assert.Equal(NodeStatus.Failure, status);
        }

        [Fact]
        public async Task Execute_NoCandidateInContext_ReturnsFailure()
        {
            var context = new BTContext(NullLogger<BTContext>.Instance);
            // provide submodel repo but no candidate
            var node = new UploadSubmodelNode
            {
                Context = context,
                SubmodelRepositoryEndpoint = "http://localhost:8080"
            };

            var status = await node.Execute();

            Assert.Equal(NodeStatus.Failure, status);
        }

        [Fact]
        public async Task Execute_WithShellButNoRepo_UpdateSkipped_ReturnsFailure()
        {
            var context = new BTContext(NullLogger<BTContext>.Instance);
            // Put a shell in context but no submodel repo
            var shell = new AssetAdministrationShell("TestShell", new Identifier("urn:test:shell"));
            context.Set("shell", shell);

            var node = new UploadSubmodelNode { Context = context };
            var status = await node.Execute();
            Assert.Equal(NodeStatus.Failure, status);
        }
    }
}
