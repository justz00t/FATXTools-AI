using Anthropic;
using Anthropic.Models.Beta.Messages;
using FATX.Recovery;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FATX.AI
{
    /// <summary>
    /// Claude driven recovery assistant. Runs a tool-use loop on the Messages
    /// API; the tools execute locally against the opened drive image, so
    /// image data never leaves the machine except for the small JSON
    /// summaries the tools return.
    /// </summary>
    public class RecoveryAgent
    {
        private const string SystemPrompt = RecoveryInstructions.Text;

        private readonly RecoveryToolset tools;
        private readonly AgentConfig config;
        private readonly AnthropicClient client;
        private readonly List<BetaMessageParam> messages = new List<BetaMessageParam>();

        public RecoveryAgent(RecoverySession session, AgentConfig config)
        {
            this.config = config;
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new InvalidOperationException("No Anthropic API key. Set ANTHROPIC_API_KEY or enter a key in the settings.");
            client = new AnthropicClient { ApiKey = config.ApiKey };
            tools = new RecoveryToolset(session, config.OutputDirectory);
        }

        /// <summary>Text written by Claude.</summary>
        public event Action<string> OnText;

        /// <summary>A tool is about to run (name, JSON input).</summary>
        public event Action<string, string> OnToolCall;

        /// <summary>A tool finished (name, short summary).</summary>
        public event Action<string, string> OnToolResult;

        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }

        /// <summary>
        /// Clears the conversation.
        /// </summary>
        public void Reset() => messages.Clear();

        private List<BetaToolUnion> BuildTools()
        {
            var list = new List<BetaToolUnion>();
            foreach (var def in tools.Definitions)
            {
                var props = new Dictionary<string, JsonElement>();
                foreach (var kv in def.Properties)
                    props[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);

                list.Add(new BetaTool
                {
                    Name = def.Name,
                    Description = def.Description,
                    InputSchema = new InputSchema
                    {
                        Properties = props,
                        Required = def.Required.ToList(),
                    },
                });
            }
            return list;
        }

        /// <summary>
        /// Sends a user request and runs the tool loop until Claude is done.
        /// </summary>
        /// <returns>The final text answer.</returns>
        public async Task<string> RunAsync(string request, CancellationToken cancellationToken = default)
        {
            tools.CancellationToken = cancellationToken;
            messages.Add(new BetaUserMessageParam { Content = request });
            var toolDefs = BuildTools();
            string lastText = "";

            for (int turn = 0; turn < config.MaxTurns; turn++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parameters = new MessageCreateParams
                {
                    Model = config.Model,
                    MaxTokens = 16000,
                    System = new List<BetaTextBlockParam>
                    {
                        new BetaTextBlockParam { Text = SystemPrompt, CacheControl = new BetaCacheControlEphemeral() },
                    },
                    Tools = toolDefs,
                    Messages = messages.ToList(),
                    OutputConfig = new BetaOutputConfig { Effort = ParseEffort(config.Effort) },
                    // Cache the growing conversation prefix between turns.
                    CacheControl = new BetaCacheControlEphemeral(),
                };

                if (config.EnableFallbacks)
                {
                    // A request declined by the safety classifiers is re-served by
                    // a fallback model inside the same call.
                    parameters = parameters with
                    {
                        Betas = [Anthropic.Models.Beta.AnthropicBeta.ServerSideFallback2026_07_01],
                        Fallbacks = new Default(),
                    };
                }

                BetaMessage response = await client.Beta.Messages.Create(parameters, cancellationToken);
                InputTokens += response.Usage.InputTokens + (response.Usage.CacheReadInputTokens ?? 0) + (response.Usage.CacheCreationInputTokens ?? 0);
                OutputTokens += response.Usage.OutputTokens;

                var assistantContent = new List<BetaContentBlockParam>();
                var toolUses = new List<BetaToolUseBlock>();
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var text))
                    {
                        assistantContent.Add(new BetaTextBlockParam { Text = text.Text });
                        if (!string.IsNullOrWhiteSpace(text.Text))
                        {
                            lastText = text.Text;
                            OnText?.Invoke(text.Text);
                        }
                    }
                    else if (block.TryPickThinking(out var thinking))
                    {
                        // Thinking blocks must be passed back unchanged.
                        assistantContent.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out var redacted))
                    {
                        assistantContent.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickToolUse(out var toolUse))
                    {
                        assistantContent.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                        toolUses.Add(toolUse);
                    }
                    else
                    {
                        // Other block types (e.g. fallback markers) are echoed as-is.
                        assistantContent.Add(JsonSerializer.Deserialize<BetaContentBlockParam>(JsonSerializer.Serialize(block)));
                    }
                }

                if (assistantContent.Count > 0)
                    messages.Add(new BetaAssistantMessageParam { Content = assistantContent });

                string stop = response.StopReason?.ToString() ?? "";
                if (stop.Contains("refusal"))
                {
                    var msg = "Claude declined this request.";
                    OnText?.Invoke(msg);
                    return msg;
                }

                if (toolUses.Count == 0)
                {
                    if (stop.Contains("max_tokens"))
                        OnText?.Invoke("[response was cut off at the token limit]");
                    return lastText;
                }

                // Run every tool call and return all results in one user message.
                var results = new List<BetaContentBlockParam>();
                foreach (var toolUse in toolUses)
                {
                    var inputJson = JsonSerializer.SerializeToElement(toolUse.Input);
                    OnToolCall?.Invoke(toolUse.Name, inputJson.GetRawText());
                    string content;
                    bool isError = false;
                    try
                    {
                        content = await Task.Run(() => tools.Execute(toolUse.Name, inputJson), cancellationToken);
                        OnToolResult?.Invoke(toolUse.Name, Summarize(content));
                    }
                    catch (ToolException e)
                    {
                        content = "Error: " + e.Message;
                        isError = true;
                        OnToolResult?.Invoke(toolUse.Name, content);
                    }

                    results.Add(new BetaToolResultBlockParam
                    {
                        ToolUseID = toolUse.ID,
                        Content = Truncate(content, 60000),
                        IsError = isError,
                    });
                }

                messages.Add(new BetaUserMessageParam { Content = results });
            }

            var limit = $"Stopped after {config.MaxTurns} turns.";
            OnText?.Invoke(limit);
            return limit;
        }

        private static Effort ParseEffort(string effort)
        {
            switch ((effort ?? "high").ToLowerInvariant())
            {
                case "low": return Effort.Low;
                case "medium": return Effort.Medium;
                case "xhigh": return Effort.Xhigh;
                case "max": return Effort.Max;
                default: return Effort.High;
            }
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "\n...[truncated; use paging arguments to see more]";

        private static string Summarize(string json)
            => json.Length <= 160 ? json : json.Substring(0, 157) + "...";
    }
}
