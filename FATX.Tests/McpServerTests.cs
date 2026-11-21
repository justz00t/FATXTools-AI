using FATX.AI;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace FATX.Tests
{
    public class McpServerTests
    {
        [Fact]
        public async Task SpeaksMcpOverStdio()
        {
            var outDir = Path.Combine(Path.GetTempPath(), "mcp-" + Guid.NewGuid());
            try
            {
                var requests = string.Join("\n", new[]
                {
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}",
                    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"list_partitions\",\"arguments\":{}}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"ping\"}",
                    "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"bogus\"}",
                }) + "\n";

                var output = new StringWriter();
                var server = new McpServer(new StringReader(requests), output, null, outDir);
                await server.RunAsync();

                var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => JsonNode.Parse(l)).ToDictionary(r => r["id"].GetValue<int>());

                Assert.Equal(5, responses.Count);   // the notification gets no response
                Assert.Equal("2025-06-18", responses[1]["result"]["protocolVersion"].GetValue<string>());
                Assert.False(string.IsNullOrEmpty(responses[1]["result"]["instructions"].GetValue<string>()));

                var tools = responses[2]["result"]["tools"].AsArray().Select(t => t["name"].GetValue<string>()).ToList();
                Assert.Contains("open_image", tools);
                Assert.Contains("find_executables", tools);
                Assert.Equal("object", responses[2]["result"]["tools"][0]["inputSchema"]["type"].GetValue<string>());

                // Without an image the tool reports a helpful error instead of failing the request.
                Assert.True(responses[3]["result"]["isError"].GetValue<bool>());
                Assert.Contains("open_image", responses[3]["result"]["content"][0]["text"].GetValue<string>());

                Assert.NotNull(responses[4]["result"]);
                Assert.Equal(-32601, responses[5]["error"]["code"].GetValue<int>());
            }
            finally
            {
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            }
        }
    }
}
