using System.Collections.Generic;
using WindowsInput.Native;

namespace SttRecorderApp
{
    /// <summary>
    /// The generic kinds of low-level actions our executor can perform.
    /// These are intentionally device-level: keyboard, mouse, scroll, wait, text.
    /// Higher-level behaviors (like "open notepad") will be planned as
    /// sequences of these actions, not hard-coded here.
    /// </summary>
    public enum ActionKind
    {
        KeyChord,        // e.g. Ctrl+T, Alt+Tab, Win+R
        KeyTap,          // Single key tap, e.g. Enter, Esc, F5
        TextInput,       // Type a string as if via keyboard

        MouseMoveTo,     // Move cursor to absolute screen coordinates
        MouseMoveBy,     // Move cursor relative to current position
        MouseDown,       // Press mouse button (left/right/middle)
        MouseUp,         // Release mouse button
        MouseClick,      // Click at current position
        MouseDoubleClick,// Double-click at current position

        ScrollVertical,  // Mouse wheel up/down (positive/negative delta)
        ScrollHorizontal,// Horizontal scroll (if supported)

        Wait             // Pause for a number of milliseconds
    }

    /// <summary>
    /// Single executable action that the low-level executor can perform.
    /// Fields are intentionally generic and nullable; each ActionKind uses
    /// only the fields it needs.
    /// </summary>
    public class ExecutableAction
    {
        public ActionKind Kind { get; init; }

        // Keyboard-related fields
        public VirtualKeyCode? MainKey { get; init; }                   // Primary key, e.g. VK_T
        public IReadOnlyList<VirtualKeyCode>? Modifiers { get; init; }  // Modifier keys, e.g. Ctrl, Shift, Alt

        // Text input
        public string? Text { get; init; }                              // For TextInput

        // Mouse coordinates (for move/click/drag-style actions later)
        public int? X { get; init; }                                    // Absolute X coordinate
        public int? Y { get; init; }                                    // Absolute Y coordinate
        public int? DeltaX { get; init; }                               // Relative X offset
        public int? DeltaY { get; init; }                               // Relative Y offset

        // Mouse button for MouseDown / MouseUp / MouseClick
        public MouseButton? Button { get; init; }

        // Scroll deltas
        public int? ScrollDelta { get; init; }                          // Positive = up, negative = down (vertical/horizontal depends on Kind)

        // Wait
        public int? MillisecondsDelay { get; init; }                    // For Wait actions

        // ---------- Factory helpers: build common actions in a single line ----------

        public static ExecutableAction KeyChord(
            VirtualKeyCode mainKey,
            IReadOnlyList<VirtualKeyCode>? modifiers = null)
        {
            return new ExecutableAction
            {
                Kind = ActionKind.KeyChord,
                MainKey = mainKey,
                Modifiers = modifiers
            };
        }

        public static ExecutableAction KeyTap(VirtualKeyCode key)
        {
            return new ExecutableAction
            {
                Kind = ActionKind.KeyTap,
                MainKey = key
            };
        }

        public static ExecutableAction ScrollVertical(int delta)
        {
            return new ExecutableAction
            {
                Kind = ActionKind.ScrollVertical,
                ScrollDelta = delta
            };
        }

        public static ExecutableAction TextInput(string text)
        {
            return new ExecutableAction
            {
                Kind = ActionKind.TextInput,
                Text = text
            };
        }

        public static ExecutableAction MouseMoveTo(int x, int y)
        {
            return new ExecutableAction
            {
                Kind = ActionKind.MouseMoveTo,
                X = x,
                Y = y
            };
        }

        public static ExecutableAction MouseClick(MouseButton button = MouseButton.Left)
        {
            return new ExecutableAction
            {
                Kind = ActionKind.MouseClick,
                Button = button
            };
        }

    }

    /// <summary>
    /// Simple enum to express which mouse button is being used.
    /// </summary>
    public enum MouseButton
    {
        Left,
        Right,
        Middle
    }
}
