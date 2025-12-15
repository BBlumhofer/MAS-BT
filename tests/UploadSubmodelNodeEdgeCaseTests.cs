using System;
using System.Collections.Generic;
using System.Linq;
using BaSyx.Models.AdminShell;
using Xunit;

namespace MAS_BT.Tests
{
    public class UploadSubmodelNodeEdgeCaseTests
    {
        [Fact]
        public void ReferenceComparison_DoesNotThrow_WhenListContainsNulls()
        {
            var id = $"https://smartfactory.de/submodels/test-{Guid.NewGuid():N}";
            var submodel = new Submodel("Test", new Identifier(id));
            var typedRef = new Reference<Submodel>(submodel);


            // Create list with a null entry and a different reference
            var refs = new List<IReference?>();
            refs.Add(null);
            refs.Add(new Reference(new Key(KeyType.Submodel, "https://smartfactory.de/submodels/other")));

            // The predicate used in UploadSubmodelNode should not throw
            var already = refs.Any(r => r != null && Reference.IsEqual(r, typedRef));

            Assert.False(already);
        }
    }
}
