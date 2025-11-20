using System.Collections.Generic;

namespace SttRecorderApp
{
    /// <summary>
    /// Snapshot of the world as the agent sees it for a single planning call.
    /// This will be extended over time (e.g., recent history, screen info, etc.).
    /// </summary>
    public sealed class AgentContext
    {
        /// <summary>
        /// Raw transcript text for this utterance / segment.
        /// </summary>
        public string Transcript { get; init; } = string.Empty;

        /// <summary>
        /// Intents that were detected for this transcript.
        /// For now this will usually be a single CommandIntent,
        /// but we keep it as a list for future multi-intent segments.
        /// </summary>
        public IReadOnlyList<CommandIntent> Intents { get; init; }
            = new List<CommandIntent>();

        /// <summary>
        /// Name of the foreground process at the time of planning
        /// (e.g., "chrome", "WINWORD", "Code").
        /// </summary>
        public string? ActiveProcessName { get; init; }

        /// <summary>
        /// Title of the foreground window (e.g., "Perplexity - Google Chrome").
        /// </summary>
        public string? ActiveWindowTitle { get; init; }
    }

    /// <summary>
    /// Contract for an agent/planner that turns context + goals into an ActionPlan.
    /// A real LLM-backed planner will implement this interface.
    /// </summary>
    public interface IAgentPlanner
    {
        /// <summary>
        /// Given the current context, decide what to do (if anything).
        /// Return null for "no plan" / "I don't know what to do".
        /// </summary>
        ActionPlan? Plan(AgentContext context);
    }


        /// <summary>
    /// Default no-op planner that always returns null.
    /// This keeps behavior identical while we wire the agent hook into MainWindow.
    /// </summary>
    public sealed class NullAgentPlanner : IAgentPlanner
    {
        public ActionPlan? Plan(AgentContext context)
        {
            // Intentionally do nothing for now.
            return null;
        }
    }

}
