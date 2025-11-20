using System.Collections.Generic;

namespace SttRecorderApp
{
    /// <summary>
    /// Represents a named sequence of low-level actions to execute.
    /// This is the core object that a planner/agent can produce.
    /// </summary>
    public sealed class ActionPlan
    {
        /// <summary>
        /// Human-readable name for this plan (e.g., "OpenNewTabInChrome").
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Ordered list of actions to execute.
        /// </summary>
        public IReadOnlyList<ExecutableAction> Actions { get; init; }
            = new List<ExecutableAction>();
    }
}
