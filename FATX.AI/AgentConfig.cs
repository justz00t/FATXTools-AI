using System;

namespace FATX.AI
{
    public class AgentConfig
    {
        public const string DefaultModel = "claude-opus-5-5";

        /// <summary>
        /// Claude model id.
        /// </summary>
        public string Model { get; set; } = DefaultModel;

        /// <summary>
        /// API key. Defaults to the ANTHROPIC_API_KEY environment variable.
        /// </summary>
        public string ApiKey { get; set; } = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        /// <summary>
        /// Effort level: low, medium, high, xhigh or max.
        /// </summary>
        public string Effort { get; set; } = "high";

        /// <summary>
        /// Directory that recovered files are written to. The agent can only write below it.
        /// </summary>
        public string OutputDirectory { get; set; }

        /// <summary>
        /// Maximum number of model turns per request (safety limit).
        /// </summary>
        public int MaxTurns { get; set; } = 60;

        /// <summary>
        /// Enable the server-side refusal fallback (recommended).
        /// </summary>
        public bool EnableFallbacks { get; set; } = true;
    }
}
