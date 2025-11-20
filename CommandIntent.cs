namespace SttRecorderApp
{
    public class CommandIntent
    {
        // High-level kind of action, e.g. "CloseTab", "ScrollDown"
        public string Kind { get; set; }

        // Original transcript text that produced this intent
        public string RawText { get; set; }
    }
}
