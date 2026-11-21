using FATX.Recovery;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FATX.AI
{
    /// <summary>
    /// Model Context Protocol server (stdio transport) exposing the recovery
    /// tools to MCP clients such as Claude Desktop and Claude Code. The client
    /// provides the model, so no Anthropic API key is needed here.
    /// Messages are newline delimited JSON-RPC 2.0.
    /// </summary>
    public class McpServer
    {
        private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

        private readonly TextReader input;
        private readonly TextWriter output;
        private readonly object writeLock = new object();
        // Tools share one drive reader and are not thread safe: run them one at a time, in order.
        private readonly SemaphoreSlim toolGate = new SemaphoreSlim(1, 1);
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        private readonly Dictionary<string, CancellationTokenSource> running = new Dictionary<string, CancellationTokenSource>();

        private RecoverySession session;
        private RecoveryToolset tools;
        private string outputDirectory;

        public McpServer(TextReader input, TextWriter output, string imagePath = null, string outputDirectory = null)
        {
            this.input = input;
            this.output = output;
            this.outputDirectory = outputDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FATXTools Recovered");
            if (imagePath != null)
                Open(imagePath);
        }

        private string Open(string path)
        {
            session?.Dispose();
            session = null;
            tools = null;
            session = new RecoverySession(path);
            tools = new RecoveryToolset(session, outputDirectory);
            return path;
        }

        // Tools handled by the server itself (opening images), followed by the recovery tools.
        private static readonly List<ToolDefinition> SessionTools = new List<ToolDefinition>
        {
            new ToolDefinition
            {
                Name = "open_image",
                Description = "Open an Xbox / Xbox 360 hard drive image (raw .img/.bin dump) read-only. Must be called before the other tools unless the server was started with an image. Optionally sets the folder recovered files are written to.",
                Properties =
                {
                    ["path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full path of the image file, e.g. E:\\Xbox\\xnaxdk.img." },
                    ["output_directory"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Folder for recovered files (default: Documents\\FATXTools Recovered)." },
                },
                Required = { "path" },
            },
        };

        private IEnumerable<ToolDefinition> AllTools()
            => SessionTools.Concat((tools ?? new RecoveryToolset(null, outputDirectory)).Definitions);

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            var pending = new List<Task>();
            string line;
            while (!cancellationToken.IsCancellationRequested && (line = await input.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonNode message;
                try
                {
                    message = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    Send(Error(null, -32700, "Parse error"));
                    continue;
                }

                // Tool calls can take minutes (scanning a drive); run them in the
                // background so pings and cancellations are still answered.
                if (message?["method"]?.GetValue<string>() == "tools/call")
                {
                    var call = message;
                    // Take the gate in arrival order, run in the background so
                    // ping/cancel keep being answered while a scan is running.
                    var gate = toolGate.WaitAsync();
                    pending.Add(Task.Run(async () =>
                    {
                        await gate;
                        try { Send(Handle(call)); }
                        finally { toolGate.Release(); }
                    }));
                }
                else
                {
                    var response = Handle(message);
                    if (response != null)
                        Send(response);
                }

                pending.RemoveAll(t => t.IsCompleted);
            }

            await Task.WhenAll(pending);
            session?.Dispose();
        }

        private void Send(JsonNode response)
        {
            if (response == null)
                return;
            var json = response.ToJsonString(WriteOptions);
            lock (writeLock)
            {
                output.Write(json);
                output.Write('\n');
                output.Flush();
            }
        }

        /// <summary>
        /// Handles one JSON-RPC message and returns the response (null for notifications).
        /// </summary>
        public JsonNode Handle(JsonNode message)
        {
            var id = message?["id"]?.DeepClone();
            var method = message?["method"]?.GetValue<string>();
            var parameters = message?["params"];
            bool isNotification = message?["id"] == null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        var requested = parameters?["protocolVersion"]?.GetValue<string>();
                        return Result(id, new JsonObject
                        {
                            ["protocolVersion"] = SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JsonObject { ["name"] = "fatxtools", ["version"] = "1.0.0" },
                            ["instructions"] = RecoveryInstructions.Text +
                                "\n\nCall open_image with the image path the user gives you first (unless list_partitions already works).",
                        });

                    case "ping":
                        return Result(id, new JsonObject());

                    case "tools/list":
                        return Result(id, new JsonObject { ["tools"] = new JsonArray(AllTools().Select(ToolJson).ToArray()) });

                    case "tools/call":
                        return Result(id, CallTool(id?.ToJsonString(), parameters));

                    case "notifications/cancelled":
                        var requestId = parameters?["requestId"]?.ToJsonString();
                        lock (running)
                            if (requestId != null && running.TryGetValue(requestId, out var cts)) cts.Cancel();
                        return null;

                    default:
                        if (isNotification || method?.StartsWith("notifications/") == true)
                            return null;
                        return Error(id, -32601, $"Method not found: {method}");
                }
            }
            catch (Exception e)
            {
                return isNotification ? null : Error(id, -32603, e.Message);
            }
        }

        private JsonObject CallTool(string requestId, JsonNode parameters)
        {
            var name = parameters?["name"]?.GetValue<string>();
            var args = parameters?["arguments"] is JsonNode a
                ? JsonSerializer.Deserialize<JsonElement>(a.ToJsonString())
                : JsonSerializer.Deserialize<JsonElement>("{}");

            var cts = new CancellationTokenSource();
            if (requestId != null)
                lock (running) running[requestId] = cts;

            try
            {
                string text;
                if (name == "open_image")
                {
                    var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path))
                        return ToolError("'path' is required.");
                    if (args.TryGetProperty("output_directory", out var o) && o.ValueKind == JsonValueKind.String)
                        outputDirectory = o.GetString();
                    Open(path);
                    text = tools.Execute("list_partitions", JsonSerializer.Deserialize<JsonElement>("{}"));
                    text = $"{{\"opened\":{JsonSerializer.Serialize(path)},\"output_directory\":{JsonSerializer.Serialize(outputDirectory)},\"partitions\":{text}}}";
                }
                else
                {
                    if (tools == null)
                        return ToolError("No image is open. Call open_image with the path of the drive image first.");
                    tools.CancellationToken = cts.Token;
                    text = tools.Execute(name, args);
                }

                return new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = false,
                };
            }
            catch (ToolException e)
            {
                return ToolError(e.Message);
            }
            catch (OperationCanceledException)
            {
                return ToolError("Cancelled.");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is ArgumentException)
            {
                return ToolError(e.Message);
            }
            finally
            {
                if (requestId != null)
                    lock (running) running.Remove(requestId);
                cts.Dispose();
            }
        }

        private static JsonObject ToolError(string message) => new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Error: " + message }),
            ["isError"] = true,
        };

        private static JsonNode ToolJson(ToolDefinition d) => new JsonObject
        {
            ["name"] = d.Name,
            ["description"] = d.Description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = JsonNode.Parse(JsonSerializer.Serialize(d.Properties)),
                ["required"] = new JsonArray(d.Required.Select(r => (JsonNode)r).ToArray()),
            },
        };

        private static JsonObject Result(JsonNode id, JsonNode result)
            => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JsonObject Error(JsonNode id, int code, string message)
            => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    }
}
