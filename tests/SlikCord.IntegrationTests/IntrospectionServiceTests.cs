using Containerd.Services.Introspection.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace Slik.Cord.IntegrationTests
{
    [TestClass]
[TestCategory(".NET 10")]
    public class IntrospectionServiceTests : ServiceTestsBase
    {
        private readonly Introspection.IntrospectionClient _client = new(Channel);

        [TestMethod]        
        public async Task Server_ReturnsNonEmptyUuid()
        {
            var response = await _client.ServerAsync(new Empty(), Headers);
            Assert.IsFalse(string.IsNullOrEmpty(response.Uuid));
        }

        [TestMethod]
        public async Task Plugins_ReturnsNonEmptyResponse()
        {
            var response = await _client.PluginsAsync(new PluginsRequest(), Headers);
            Assert.IsTrue(response.Plugins.Count >= 0);
        }
    }
}
