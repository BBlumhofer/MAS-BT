using System;
using System.Linq;
using MAS_BT;
using Xunit;

namespace MAS_BT.Tests
{
    public class DispatchingStateTests
    {
        [Fact]
        public void UpsertAndFindModulesForCapability_Works()
        {
            var state = new DispatchingState();
            var m1 = new DispatchingModuleInfo { ModuleId = "P1", Capabilities = new System.Collections.Generic.List<string> { "Drill", "Screw" } };
            var m2 = new DispatchingModuleInfo { ModuleId = "P2", Capabilities = new System.Collections.Generic.List<string> { "Assemble" } };

            state.Upsert(m1);
            state.Upsert(m2);

            var drill = state.FindModulesForCapability("Drill").ToList();
            Assert.Contains("P1", drill);

            var assemble = state.FindModulesForCapability("Assemble").ToList();
            Assert.Contains("P2", assemble);

            var all = state.FindModulesForCapability(string.Empty).ToList();
            Assert.Contains("P1", all);
            Assert.Contains("P2", all);
        }

        [Fact]
        public void UpsertInventory_PruneStaleModules_Works()
        {
            var state = new DispatchingState();
            state.UpsertInventory("M1", 5, 0, DateTime.UtcNow.AddMinutes(-10));
            state.UpsertInventory("M2", 2, 1, DateTime.UtcNow);

            var all = state.AllModuleIds().ToList();
            Assert.Contains("M1", all);
            Assert.Contains("M2", all);

            var removed = state.PruneStaleModules(TimeSpan.FromMinutes(5), DateTime.UtcNow);
            Assert.Contains("M1", removed);
            Assert.DoesNotContain("M2", removed);
        }

        [Fact]
        public void CapabilityDescriptionAndSimilarityCache_Works()
        {
            var state = new DispatchingState();
            Assert.False(state.TryGetCapabilityDescription("drill", out _));
            state.SetCapabilityDescription("drill", "desc");
            Assert.True(state.TryGetCapabilityDescription("drill", out var desc));
            Assert.Equal("desc", desc);

            Assert.False(state.TryGetCapabilitySimilarity("A","B", out _));
            state.SetCapabilitySimilarity("A","B", 0.75);
            Assert.True(state.TryGetCapabilitySimilarity("A","B", out var sim));
            Assert.Equal(0.75, sim);
            // symmetric key
            Assert.True(state.TryGetCapabilitySimilarity("B","A", out var sim2));
            Assert.Equal(0.75, sim2);
        }
    }
}
