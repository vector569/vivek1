using System.Collections.Generic;
using WindowsInput.Native;   // <-- add this


namespace SttRecorderApp
{
    /// <summary>
    /// Declarative definition of a command type and the phrases
    /// that should map to it in the transcript.
    /// </summary>
    /// 
    ///     /// <summary>
    /// High-level grouping for commands so we can do things like:
    /// - Only run Browser commands when the active window is a browser.
    /// - Treat Scroll commands slightly differently in the UI or logging.
    /// </summary>
    public enum CommandCategory
    {
        Browser,
        Scroll,
        Global
    }

    /// <summary>
    /// Describes how a command will eventually be executed.
    /// For now we only need KeyChord and Scroll, but we can add
    /// TextEntry, AppLaunch, etc. later without breaking callers.
    /// </summary>
    public enum CommandActionType
    {
        KeyChord,
        Scroll,
        TextInput    // New: represents "type this text into the active window"
    }

    public class CommandDefinition
    {
        // High-level command kind, e.g. "NewTab", "CloseTab"
        public string Kind { get; init; }

        // Category for this command (Browser, Scroll, Global, etc.)
        public CommandCategory Category { get; init; }

        // How this command will be executed (key chord, scroll, etc.)
        public CommandActionType ActionType { get; init; }

        // For KeyChord commands: which main key should be pressed (e.g., VK_T, VK_W).
        // Null for commands that are not key-chord based (e.g., Scroll).
        public VirtualKeyCode? KeyChordKey { get; init; }

        // For KeyChord commands: modifier keys like CONTROL, SHIFT, ALT, etc.
        // Null or empty for commands that don't use modifiers.
        public IReadOnlyList<VirtualKeyCode>? KeyChordModifiers { get; init; }

        // Lowercase phrases that should map to this command.
        // Example: "open a new tab", "open new tab", "new tab".
        public IReadOnlyList<string> Phrases { get; init; }
    }


    /// <summary>
    /// Central catalog of all supported commands and their phrase patterns.
    /// For now this just mirrors the phrases already hard-coded in
    /// ExtractIntentsFromTranscript, but as data instead of logic.
    /// </summary>
    public static class CommandCatalog
    {
        /// <summary>
        /// Static, read-only list of command definitions.
        /// NOTE: At this step, this catalog is not yet used by the parser.
        /// It’s pure scaffolding and does not change behavior.
        /// </summary>
        public static IReadOnlyList<CommandDefinition> Commands { get; } =
            new List<CommandDefinition>
            {
                // New tab – several natural phrasings
           // New tab – several natural phrasings
new CommandDefinition
{
    Kind = "NewTab",
    Category = CommandCategory.Browser,       // Browser-oriented command
    ActionType = CommandActionType.KeyChord,  // Executed via keyboard shortcut
    KeyChordKey = VirtualKeyCode.VK_T,        // The main key in the chord (T)
    KeyChordModifiers = new[]
    {
        VirtualKeyCode.CONTROL                // Ctrl + T
    },
    Phrases = new[]
    {
        "open a new tab",
        "open new tab",
        "new tab"
    }
},

// Close tab – your current tuned phrase
new CommandDefinition
{
    Kind = "CloseTab",
    Category = CommandCategory.Browser,
    ActionType = CommandActionType.KeyChord,
    KeyChordKey = VirtualKeyCode.VK_W,        // The main key in the chord (W)
    KeyChordModifiers = new[]
    {
        VirtualKeyCode.CONTROL                // Ctrl + W
    },
    Phrases = new[]
    {
        "close a tab"
    }
},

// Next tab – move to the next browser tab
new CommandDefinition
{
    Kind = "NextTab",
    Category = CommandCategory.Browser,
    ActionType = CommandActionType.KeyChord,
    KeyChordKey = VirtualKeyCode.TAB,          // The main key in the chord (Tab)
    KeyChordModifiers = new[]
    {
        VirtualKeyCode.CONTROL                 // Ctrl + Tab
    },
    Phrases = new[]
    {
        "next tab",
        "go to next tab",
        "switch to next tab"
    }
},



                // Scroll down
                new CommandDefinition
                {
                    Kind = "ScrollDown",
                    Category = CommandCategory.Scroll,        // Scroll-related command
                    ActionType = CommandActionType.Scroll,    // Will eventually drive scroll behaviour
                    Phrases = new[]
                    {
                        "scroll down",
                        "scroll down a little",
                        "scroll down a bit"
                    }
                },

                // Scroll up
                new CommandDefinition
                {
                    Kind = "ScrollUp",
                    Category = CommandCategory.Scroll,        // Scroll-related command
                    ActionType = CommandActionType.Scroll,    // Will eventually drive scroll behaviour
                    Phrases = new[]
                    {
                        "scroll up",
                        "scroll up a little",
                        "scroll up a bit"
                    }
                },

                                // Debug: generic "type text" command.
                // Any transcript containing one of these phrases will map to TextInput;
                // what actually gets typed is decided in BuildActionsFor.
                new CommandDefinition
                {
                    Kind = "TextInput",
                    Category = CommandCategory.Global,         // Not tied to a specific app
                    ActionType = CommandActionType.TextInput,  // Will drive a TextInput ExecutableAction
                    Phrases = new[]
                    {
                        "debug type",                          // e.g., "debug type hello world"
                        "debug write"                          // alternate debug phrase if you want
                    }
                }




            };
    }
}
