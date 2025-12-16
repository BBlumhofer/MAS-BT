using System;
using System.Net.Http;
using System.Threading.Tasks;
using BaSyx.Clients.AdminShell.Http;
using BaSyx.Models.AdminShell;
using Xunit;

namespace MAS_BT.Tests
{
    public class UploadSubmodelIntegrationTests
    {
        private static readonly Uri SubmodelRepositoryUri = new("http://192.168.178.30:8080/submodels", UriKind.Absolute);

        private static async Task<bool> EnsureServerAvailableAsync()
        {
            if (Environment.GetEnvironmentVariable("BXS_SKIP_INTEGRATION_TESTS") == "1")
                return false;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var response = await http.GetAsync(SubmodelRepositoryUri);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        [Fact]
        public async Task Uploads_And_Retrieves_Submodel_On_Remote_Repository()
        {
            if (!await EnsureServerAvailableAsync())
            {
                // Skip if server not reachable
                return;
            }

            var id = $"https://smartfactory.de/submodels/test-{Guid.NewGuid():N}";
            var sm = new Submodel("TestSubmodel", new Identifier(id));
            var prop = new Property<string>("TestValue") { Value = new PropertyValue<string>("hello") };
            sm.SubmodelElements.Add(prop);

            using var client = new SubmodelRepositoryHttpClient(SubmodelRepositoryUri);
            var createRes = await client.CreateSubmodelAsync(sm);
            Assert.True(createRes.Success, "CreateSubmodelAsync failed: " + (createRes.Messages?.ToString() ?? "(no message)"));

            var retrieveRes = await client.RetrieveSubmodelAsync(new Identifier(id));
            Assert.True(retrieveRes.Success, "RetrieveSubmodelAsync failed: " + (retrieveRes.Messages?.ToString() ?? "(no message)"));

            var retrieved = Assert.IsAssignableFrom<ISubmodel>(retrieveRes.Entity);
            var typed = Assert.IsAssignableFrom<Submodel>(retrieved);

            var found = typed.SubmodelElements?.FirstOrDefault(e => e is Property p && string.Equals(p.IdShort, "TestValue", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(found);

            // Cleanup: delete the created submodel to avoid littering the remote repository
            try
            {
                var del = await client.DeleteSubmodelAsync(new Identifier(id));
                Assert.True(del.Success, "DeleteSubmodelAsync failed: " + (del.Messages?.ToString() ?? "(no message)"));
            }
            catch (Exception ex)
            {
                // Don't fail the test on cleanup exception, but log for diagnostics
                Console.WriteLine("Cleanup DeleteSubmodelAsync threw: " + ex.Message);
            }
        }
    }
}
