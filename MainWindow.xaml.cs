﻿using System;                                                // Basic .NET types (EventArgs, Exception, etc.)
using System.IO;                                             // File and path operations (File, Path, Directory)
using System.Windows;                                        // WPF core (Window, RoutedEventArgs, Dispatcher, etc.)
using System.Diagnostics;                                    // For starting the Whisper CLI process (Process, ProcessStartInfo)
using System.Collections.Generic;                            // Generic collections like List<T>
using System.Text.RegularExpressions;                        // (Currently unused) regex support for text parsing if needed
using WindowsInput;                                          // InputSimulator library namespace for keyboard/mouse simulation
using WindowsInput.Native;                                   // VirtualKeyCode enum (Ctrl, T, W, etc.)
using System.Runtime.InteropServices;
using System.Text;


namespace SttRecorderApp                                     // App namespace shared across all project files
{
    public partial class MainWindow : Window                 // Main WPF window class; partial because XAML defines the other part
    {
        private readonly AudioRecorder _recorder = new AudioRecorder(); // Manages mic input and segmentation into audio files
        private readonly string _audioFolderPath;            // Path where audio segment WAVs are stored
        private readonly string _transcriptFolderPath;       // Path where transcript .txt files are stored
        private readonly IInputSimulator _inputSimulator = new InputSimulator(); // Used to send keyboard shortcuts (Ctrl+T, Ctrl+W)

        private readonly IAgentPlanner _planner = new HttpAgentPlanner("http://localhost:5005/plan"); // LLM planning endpoint (server next step)

        // Path to whisper-ctranslate2 CLI (from your manual test)
        private const string WhisperCliPath = @"C:\Users\aravi\AppData\Local\Programs\Python\Python311\Scripts\whisper-ctranslate2.exe"; // Hard-coded path to Whisper CLI exe

        // Model: "small" is a good speed/accuracy tradeoff
        private const string WhisperModelName = "small";      // Whisper model size chosen for balance of speed and accuracy

        public MainWindow()                                  // Constructor; runs when the window is created
        {
            InitializeComponent();                           // WPF call to load XAML UI and wire up named controls
            EnsurePlannerServerRunning();


            // Root folder: Documents\SttRecorder
            var rootFolder = Path.Combine(                   // Build root folder path under user Documents
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), // Get user's Documents directory
                "SttRecorder");                              // Our app-specific subfolder name

            // Subfolders for audio and transcripts
            _audioFolderPath = Path.Combine(rootFolder, "audio");      // Folder path for audio segments
            _transcriptFolderPath = Path.Combine(rootFolder, "transcripts"); // Folder path for transcript files

            Directory.CreateDirectory(_audioFolderPath);     // Ensure audio folder exists (create if missing)
            Directory.CreateDirectory(_transcriptFolderPath); // Ensure transcript folder exists (create if missing)

            // Subscribe to segment completion event
            _recorder.SegmentCompleted += Recorder_SegmentCompleted;   // When AudioRecorder finishes a segment, handle it here

            UpdateButtonStates(isListening: false);           // Initialize button states: Start enabled, Stop disabled
        }

        private void StartListeningButton_Click(object sender, RoutedEventArgs e) // Click handler for "Start Listening" button
        {
            _recorder.Start(_audioFolderPath);               // Start audio capture and segmentation into the audio folder

            UpdateButtonStates(isListening: true);           // Disable Start, enable Stop
            AudioHistoryList.Items.Add("Listening started (continuous, speech-based segments)."); // Log status to history list
        }

        private void StopListeningButton_Click(object sender, RoutedEventArgs e)  // Click handler for "Stop Listening" button
        {
            _recorder.Stop();                                // Stop audio capture (AudioRecorder will close any active segment)

            UpdateButtonStates(isListening: false);          // Enable Start, disable Stop
            AudioHistoryList.Items.Add("Listening stopped."); // Log status to history list
        }


        private void EnsurePlannerServerRunning()
        {
            try
            {
                // quick port check
                using var client = new System.Net.Sockets.TcpClient();
                var task = client.ConnectAsync("127.0.0.1", 5005);
                if (task.Wait(150)) return; // already up

                // find planner_server folder by walking up from exe dir
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                string? plannerDir = null;
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    var candidate = Path.Combine(dir, "planner_server");
                    if (Directory.Exists(candidate)) { plannerDir = candidate; break; }
                    dir = Directory.GetParent(dir)?.FullName;
                }
                if (plannerDir == null) return;

                var uvicorn = Path.Combine(plannerDir, ".venv", "Scripts", "uvicorn.exe");
                if (!File.Exists(uvicorn)) return;

                Process.Start(new ProcessStartInfo
                {
                    FileName = uvicorn,
                    Arguments = "app:app --host 127.0.0.1 --port 5005",
                    WorkingDirectory = plannerDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { /* never break baseline */ }
        }

        private void Recorder_SegmentCompleted(AudioSegment segment)   // Called whenever AudioRecorder closes a segment
        {
            // Event comes from audio thread; marshal to UI thread
            Dispatcher.Invoke(() =>                            // Ensure UI updates run on the main UI thread
            {
                var audioFileName = Path.GetFileName(segment.FilePath);           // Extract just the file name from full path
                var baseName = Path.GetFileNameWithoutExtension(audioFileName);   // Base name used for matching transcript file

                // 1) Update the audio history list (existing behavior)
                AudioHistoryList.Items.Add(                    // Show segment summary in the audio history list
                    $"{segment.StartTimeIst:HH:mm:ss} ({segment.Duration.TotalSeconds:F1}s) -> {audioFileName}");

                // 2) Create a matching transcript file in Documents\SttRecorder\transcripts
                var transcriptFileName = baseName + ".txt";    // Transcript file name mirrors audio file name with .txt extension
                var transcriptPath = Path.Combine(_transcriptFolderPath, transcriptFileName); // Full path to transcript file

                var lines = new[]                              // Initial placeholder contents for transcript file
                {
                    $"Start (IST): {segment.StartTimeIst:yyyy-MM-dd HH:mm:ss.fff}", // Log segment start time
                    $"End   (IST): {segment.EndTimeIst:yyyy-MM-dd HH:mm:ss.fff}",   // Log segment end time
                    "",
                    "[Placeholder transcript]",               // Placeholder before Whisper overwrites this with real text
                    "This is where the translated English text for this segment will go."
                };

                File.WriteAllLines(transcriptPath, lines);     // Write placeholder header to the new .txt file

                // Optional: log that the transcript file was created
                AudioHistoryList.Items.Add($"Transcript file created -> {transcriptFileName}"); // Confirm transcript file creation
            });

            RunWhisperForSegment(segment);                     // Kick off Whisper STT processing for this segment (off-UI-thread)
        }

        private void RunWhisperForSegment(AudioSegment segment) // Runs Whisper CLI to transcribe a single audio segment
        {
            try
            {
                if (!File.Exists(WhisperCliPath))             // Verify the Whisper CLI binary exists at configured path
                {
                    // If the CLI is missing, just log and return.
                    Dispatcher.Invoke(() =>                   // Back to UI thread for logging
                    {
                        AudioHistoryList.Items.Add("Whisper CLI not found, STT skipped."); // Inform user STT was skipped
                    });
                    return;                                   // Abort transcription for this segment
                }

                // Build the process start info, mirroring your manual PowerShell test
                var psi = new ProcessStartInfo                // Configure how to start Whisper CLI
                {
                    FileName = WhisperCliPath,                // Executable to run (whisper-ctranslate2)
                    Arguments =
                        $"\"{segment.FilePath}\" " +          // Input audio file path (quoted)
                        $"--model {WhisperModelName} " +      // Model name to use (e.g., "small")
                        $"--task translate " +                // Force Whisper to output English translation
                        $"--output_format txt " +             // Output plain text file
                        $"--output_dir \"{_transcriptFolderPath}\" " + // Directory where Whisper should write transcripts
                        $"--compute_type int8",               // Use int8 quantization for speed
                    UseShellExecute = false,                  // Required for redirecting output and running without shell
                    RedirectStandardOutput = true,            // Capture standard output (not heavily used here)
                    RedirectStandardError = true,             // Capture error output for debugging (if needed)
                    CreateNoWindow = true                     // Run Whisper without opening a visible console window
                };

                using var process = Process.Start(psi);       // Start Whisper CLI process with the configured settings
                if (process == null)                          // If process failed to start (unlikely but possible)
                {
                    Dispatcher.Invoke(() =>
                    {
                        AudioHistoryList.Items.Add("Failed to start Whisper process."); // Log failure to start process
                    });
                    return;                                   // Abort this segment's transcription
                }

                // Block until Whisper finishes for this segment
                process.WaitForExit();                        // Wait synchronously until Whisper finishes processing this file

                if (process.ExitCode == 0)                    // Exit code 0 indicates success
                {
                    // Locate the transcript file whisper wrote
                    string baseName = Path.GetFileNameWithoutExtension(segment.FilePath); // Audio base name (no extension)
                    string transcriptPath = Path.Combine(_transcriptFolderPath, baseName + ".txt"); // Transcript path expected

                    string transcriptText = string.Empty;     // Will hold transcript text or error message
                    if (File.Exists(transcriptPath))          // If Whisper did create the transcript file
                    {
                        try
                        {
                            transcriptText = File.ReadAllText(transcriptPath).Trim(); // Read entire transcript and trim whitespace
                        }
                        catch (Exception readEx)              // If reading file failed for some reason
                        {
                            transcriptText = $"[Error reading transcript: {readEx.Message}]"; // Record error message instead
                        }
                    }
                    else                                      // Whisper didn't create the transcript as expected
                    {
                        transcriptText = "[Transcript file not found after Whisper run]"; // Log missing transcript case
                    }

                    // Parse intents *before* going back to UI thread
                    var intents = ExtractIntentsFromTranscript(transcriptText); // Turn raw text into a sequence of command intents

                    Dispatcher.Invoke(() =>                   // Back to UI thread to update UI lists and execute intents
                    {
                        AudioHistoryList.Items.Add("Whisper transcript generated."); // Log that STT finished

                        // preview...
                        if (!string.IsNullOrEmpty(transcriptText)) // Only show preview if we have some text
                        {
                            var preview = transcriptText;         // Local copy for possible truncation
                            const int maxLen = 200;               // Max characters to show in UI preview
                            if (preview.Length > maxLen)          // If text is long, cut it down
                            {
                                preview = preview.Substring(0, maxLen) + "..."; // Show first 200 chars with ellipsis
                            }

                            AudioHistoryList.Items.Add("  → " + preview); // Indented preview line for readability
                        }


                        // If nothing matched the catalog, fall back to agent planning on the whole utterance.
                        if (intents.Count == 0)
                        {
                            var ctx = BuildAgentContextFromTranscript(transcriptText, intents);
                            var plan = _planner.Plan(ctx);
                            if (plan != null)
                            {
                                RunActionPlan(plan);
                                AudioHistoryList.Items.Add($"  [Agent] plan executed: {plan.Name}");
                            }
                            else
                            {
                                AudioHistoryList.Items.Add("  [Agent] no plan");
                            }
                        }

                        // Log and "execute" each intent in order
                        foreach (var intent in intents)          // Loop over each recognized command intent in sequence
                        {
                            AudioHistoryList.Items.Add($"  [Intent] {intent.Kind}"); // Log the high-level intent type
                            ExecuteIntent(intent);               // Execute the intent (some real actions, some still dry-run)
                        }
                    });

                }
                else                                          // Whisper exited with a non-zero code (error case)
                {
                    Dispatcher.Invoke(() =>
                    {
                        AudioHistoryList.Items.Add($"Whisper exited with code {process.ExitCode}."); // Log exit code for debugging
                    });
                }
            }
            catch (Exception ex)                              // Catch any unexpected errors (process start, IO, etc.)
            {
                // Very basic error logging to the UI
                Dispatcher.Invoke(() =>
                {
                    AudioHistoryList.Items.Add($"Whisper exception: {ex.Message}"); // Log exception message for user/dev
                });
            }
        }

        private List<CommandIntent> ExtractIntentsFromTranscript(string transcriptText) // Parses transcript text into ordered intents
        {
            var intents = new List<CommandIntent>();          // List to accumulate detected command intents

            if (string.IsNullOrWhiteSpace(transcriptText))    // If transcript is empty or whitespace
                return intents;                               // Return empty list (no commands)

            // Work on a lowercase copy for matching
            var lower = transcriptText.ToLowerInvariant();    // Lowercased transcript for case-insensitive search

            // Helper: find the earliest occurrence of any of the patterns
            int FindFirstIndex(System.Collections.Generic.IEnumerable<string> patterns)
            {
                int best = -1;                                // -1 means "not found yet"

                foreach (var pattern in patterns)             // Loop over each phrase we want to match
                {
                    var idx = lower.IndexOf(pattern, StringComparison.InvariantCulture); // Find first index of this phrase
                    if (idx >= 0 && (best == -1 || idx < best)) // If found and earlier than previous best, update best
                    {
                        best = idx;                           // Track earliest match position so far
                    }
                }

                return best;                                  // Return earliest index for this set of patterns (or -1)
            }

            // Collect (index, kind) pairs for each command we know about
            var matches = new List<(int index, string kind)>(); // List of (position, command kind) tuples

            // NEW: drive entirely from the command catalog instead of hard-coded AddIfFound calls
            foreach (var def in CommandCatalog.Commands)
            {
                int idx = FindFirstIndex(def.Phrases);        // Use this command's phrase list
                if (idx >= 0)                                 // If any phrase was found
                {
                    matches.Add((idx, def.Kind));             // Record its index and associated command type
                }
            }

            // Sort by order of appearance in the transcript
            matches.Sort((a, b) => a.index.CompareTo(b.index)); // Ensure commands are executed in spoken order

            // Turn matches into CommandIntent objects in sequence
            foreach (var m in matches)                        // Convert each (index, kind) into a CommandIntent object
            {
                intents.Add(new CommandIntent
                {
                    Kind = m.kind,                            // High-level command type (NewTab, CloseTab, etc.)
                    RawText = transcriptText                  // Full original transcript (kept for context/debugging)
                });
            }

            return intents;                                   // Return ordered list of intents extracted from this transcript
        }

        private CommandIntent? TryParseCommand(string transcriptText) // Legacy single-intent parser (currently unused by main flow)
        {
            if (string.IsNullOrWhiteSpace(transcriptText))   // Ignore empty / whitespace-only transcripts
                return null;                                 // No intent

            var text = transcriptText.Trim().ToLowerInvariant(); // Normalize text for simple phrase checks

            // Very simple phrase-based mapping for now.
            // We'll expand this later.

            if (text.Contains("close tab"))                  // If phrase "close tab" appears anywhere
            {
                return new CommandIntent
                {
                    Kind = "CloseTab",                       // Map to CloseTab intent
                    RawText = transcriptText
                };
            }

            if (text.Contains("new tab") || text.Contains("open new tab")) // Recognize "new tab" and "open new tab"
            {
                return new CommandIntent
                {
                    Kind = "NewTab",                         // Map to NewTab intent
                    RawText = transcriptText
                };
            }

            if (text.Contains("next tab"))                   // Recognize "next tab"
            {
                return new CommandIntent
                {
                    Kind = "NextTab",                        // Map to NextTab intent
                    RawText = transcriptText
                };
            }

            if (text.Contains("previous tab") || text.Contains("prev tab") || text.Contains("last tab")) // Various previous-tab phrases
            {
                return new CommandIntent
                {
                    Kind = "PreviousTab",                    // Map to PreviousTab intent
                    RawText = transcriptText
                };
            }

            if (text.Contains("scroll down"))                // Any phrase containing "scroll down"
            {
                return new CommandIntent
                {
                    Kind = "ScrollDown",                     // Map to ScrollDown intent
                    RawText = transcriptText
                };
            }

            if (text.Contains("scroll up"))                  // Any phrase containing "scroll up"
            {
                return new CommandIntent
                {
                    Kind = "ScrollUp",                       // Map to ScrollUp intent
                    RawText = transcriptText
                };
            }

            // No intent recognized
            return null;                                     // Return null meaning "couldn't parse this into a command"
        }

        // Look up the catalog definition for a given command kind (e.g., "NewTab")
        private CommandDefinition? FindCommandDefinition(string kind)
        {
            foreach (var def in CommandCatalog.Commands)              // Iterate through known commands
            {
                if (string.Equals(def.Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    return def;                                      // Return the first matching definition
                }
            }

            return null;                                             // No match found
        }


        // Execute a single low-level action using the shared InputSimulator instance.
        // For now we only implement KeyChord and ScrollVertical; other kinds are stubs.
        private void ExecuteAction(ExecutableAction action)                   // Execute a low-level micro action using InputSimulator
        {
            if (action == null) return;                                       // Defensive guard: ignore null actions
            AudioHistoryList.Items.Add($"    [Action] {DescribeAction(action)}");

            switch (action.Kind)
            {
                case ActionKind.KeyChord:                                     // Chord: e.g., Ctrl+T, Ctrl+W, etc.
                    if (action.MainKey == null)                               // Need a primary key
                        return;

                    var key = action.MainKey.Value;                           // Main key in the chord
                    var modifiers = action.Modifiers;                         // Modifier keys (Ctrl, Shift, Alt, etc.)

                    if (modifiers == null || modifiers.Count == 0)
                    {
                        // Simple key press (no modifiers)
                        _inputSimulator.Keyboard.KeyPress(key);
                    }
                    else
                    {
                        // Modifier(s) + main key (e.g., Ctrl+T)
                        _inputSimulator.Keyboard.ModifiedKeyStroke(
                            modifiers,
                            new[] { key });
                    }
                    break;

                case ActionKind.KeyTap:                                       // Single key press: e.g., Enter, Esc, F5
                    if (action.MainKey != null)
                    {
                        _inputSimulator.Keyboard.KeyPress(action.MainKey.Value);
                    }
                    break;

                case ActionKind.TextInput:                                    // Type raw text into the active window
                    if (!string.IsNullOrEmpty(action.Text))
                    {
                        _inputSimulator.Keyboard.TextEntry(action.Text);
                    }
                    break;

                case ActionKind.ScrollVertical:                               // Mouse wheel up/down
                    if (action.ScrollDelta is int vDelta && vDelta != 0)
                    {
                        _inputSimulator.Mouse.VerticalScroll(vDelta);
                    }
                    break;

                case ActionKind.ScrollHorizontal:                             // Horizontal wheel (tilt) scroll
                    if (action.ScrollDelta is int hDelta && hDelta != 0)
                    {
                        _inputSimulator.Mouse.HorizontalScroll(hDelta);
                    }
                    break;

                case ActionKind.MouseMoveTo:                                  // Absolute move on virtual desktop (0..65535 coords)
                    if (action.X is int x && action.Y is int y)
                    {
                        _inputSimulator.Mouse.MoveMouseToPositionOnVirtualDesktop(x, y);
                    }
                    break;

                case ActionKind.MouseMoveBy:                                  // Relative move in pixels from current position
                    {
                        int dx = action.DeltaX ?? 0;
                        int dy = action.DeltaY ?? 0;
                        if (dx != 0 || dy != 0)
                        {
                            _inputSimulator.Mouse.MoveMouseBy(dx, dy);
                        }
                    }
                    break;

                case ActionKind.MouseDown:                                    // Press (but don’t release) a mouse button
                    if (action.Button == MouseButton.Left)
                    {
                        _inputSimulator.Mouse.LeftButtonDown();
                    }
                    else if (action.Button == MouseButton.Right)
                    {
                        _inputSimulator.Mouse.RightButtonDown();
                    }
                    // MouseButton.Middle is currently ignored to avoid calling non-existent APIs
                    break;

                case ActionKind.MouseUp:                                      // Release a mouse button
                    if (action.Button == MouseButton.Left)
                    {
                        _inputSimulator.Mouse.LeftButtonUp();
                    }
                    else if (action.Button == MouseButton.Right)
                    {
                        _inputSimulator.Mouse.RightButtonUp();
                    }
                    break;

                case ActionKind.MouseClick:                                   // Click (down+up) a mouse button
                    if (action.Button == MouseButton.Left)
                    {
                        _inputSimulator.Mouse.LeftButtonClick();
                    }
                    else if (action.Button == MouseButton.Right)
                    {
                        _inputSimulator.Mouse.RightButtonClick();
                    }
                    break;

                case ActionKind.MouseDoubleClick:                             // Double-click a mouse button
                    if (action.Button == MouseButton.Left)
                    {
                        _inputSimulator.Mouse.LeftButtonDoubleClick();
                    }
                    else if (action.Button == MouseButton.Right)
                    {
                        _inputSimulator.Mouse.RightButtonDoubleClick();
                    }
                    break;

                case ActionKind.Wait:                                         // Wait/pause between actions (no-op for now)
                                                                              // Intentionally a no-op for now to avoid blocking the UI thread.
                                                                              // Later, when we have an async/agent executor, this can honor action.MillisecondsDelay.
                    break;

                default:
                    // Unknown / not yet implemented action kind: ignore safely.
                    break;
            }
        }

        // Produce a human-readable description of a single low-level action
        // for debugging and timeline purposes.
        private string DescribeAction(ExecutableAction action)
        {
            if (action == null)
                return "(null action)";

            switch (action.Kind)
            {
                case ActionKind.KeyChord:
                    {
                        var key = action.MainKey?.ToString() ?? "None";
                        var mods = action.Modifiers != null
                            ? string.Join("+", action.Modifiers)
                            : string.Empty;
                        return string.IsNullOrEmpty(mods)
                            ? $"KeyChord {key}"
                            : $"KeyChord {mods}+{key}";
                    }

                case ActionKind.KeyTap:
                    return $"KeyTap {action.MainKey?.ToString() ?? "None"}";

                case ActionKind.TextInput:
                    {
                        var text = action.Text ?? string.Empty;
                        var preview = text.Length <= 40 ? text : text.Substring(0, 40) + "…";
                        return $"TextInput \"{preview}\"";
                    }

                case ActionKind.ScrollVertical:
                    return $"ScrollVertical {action.ScrollDelta ?? 0}";

                case ActionKind.ScrollHorizontal:
                    return $"ScrollHorizontal {action.ScrollDelta ?? 0}";

                case ActionKind.MouseMoveTo:
                    return $"MouseMoveTo ({action.X ?? 0}, {action.Y ?? 0})";

                case ActionKind.MouseMoveBy:
                    return $"MouseMoveBy (dx={action.DeltaX ?? 0}, dy={action.DeltaY ?? 0})";

                case ActionKind.MouseDown:
                    return $"MouseDown {action.Button?.ToString() ?? "Unknown"}";

                case ActionKind.MouseUp:
                    return $"MouseUp {action.Button?.ToString() ?? "Unknown"}";

                case ActionKind.MouseClick:
                    return $"MouseClick {action.Button?.ToString() ?? "Left"}";

                case ActionKind.MouseDoubleClick:
                    return $"MouseDoubleClick {action.Button?.ToString() ?? "Left"}";

                case ActionKind.Wait:
                    return $"Wait {action.MillisecondsDelay ?? 0} ms";

                default:
                    return $"{action.Kind}";
            }
        }

        // Execute a sequence of low-level actions in order.
        // This is the core hook that future "plans" (multi-step tasks) will use.
        private void ExecuteActions(IEnumerable<ExecutableAction> actions)
        {
            if (actions == null) return;                     // Defensive: ignore null sequences

            foreach (var action in actions)                  // Run each action in sequence
            {
                ExecuteAction(action);                       // Delegate to the single-action executor
            }
        }

        // Async versions so multi-step plans can honor Wait without freezing UI.
        private async Task ExecuteActionAsync(ExecutableAction action)
        {
            if (action == null) return;
            AudioHistoryList.Items.Add($"    [Action] {DescribeAction(action)}");

            switch (action.Kind)
            {
                case ActionKind.Wait:
                    var ms = action.MillisecondsDelay ?? 0;
                    if (ms > 0) await Task.Delay(ms);
                    break;
                default:
                    ExecuteAction(action); // reuse all existing, proven sync behavior
                    break;
            }
        }

        private async Task ExecuteActionsAsync(IEnumerable<ExecutableAction> actions)
        {
            if (actions == null) return;
            foreach (var a in actions)
                await ExecuteActionAsync(a);
        }


        // Build the list of low-level actions that should be executed for a given
        // command definition + intent. For now this just returns a single action
        // for KeyChord and Scroll commands, but later it can return multi-step plans.

        // High-level entry point for executing a planned sequence of actions.
        // This is what an "agent" or planner will eventually call with a named plan.
        private void RunActionPlan(string planName, IReadOnlyList<ExecutableAction> actions)
        {
            if (actions == null || actions.Count == 0)
            {
                // Nothing to do; log a minimal message for debugging
                AudioHistoryList.Items.Add($"  [Plan] {planName}: no actions.");
                return;
            }

            // Log a summary before running the plan
            AudioHistoryList.Items.Add($"  [Plan] {planName}: executing {actions.Count} action(s).");

            // Also log the current active window context for debugging / future agent decisions
            var (processName, windowTitle) = GetActiveWindowInfo();
            if (!string.IsNullOrWhiteSpace(processName) || !string.IsNullOrWhiteSpace(windowTitle))
            {
                var proc = string.IsNullOrWhiteSpace(processName) ? "unknown" : processName;
                var title = string.IsNullOrWhiteSpace(windowTitle) ? "no title" : windowTitle;
                AudioHistoryList.Items.Add($"    [Context] Active: {proc} | {title}");
            }

            _ = ExecuteActionsAsync(actions);

        }


        // New OVERLOAD – ADD THIS *below* the one above
        private void RunActionPlan(ActionPlan plan)
        {
            if (plan == null)
            {
                AudioHistoryList.Items.Add("  [Plan] (null): no actions.");
                return;
            }

            RunActionPlan(plan.Name, plan.Actions);
        }

        private List<ExecutableAction> BuildActionsFor(CommandDefinition def, CommandIntent intent)
        {
            var actions = new List<ExecutableAction>();

            if (def.ActionType == CommandActionType.KeyChord)
            {
                if (def.KeyChordKey == null)
                    return actions; // No configured key: nothing to do

                // Single key-chord action (e.g., Ctrl+T)
                actions.Add(new ExecutableAction
                {
                    Kind = ActionKind.KeyChord,
                    MainKey = def.KeyChordKey.Value,
                    Modifiers = def.KeyChordModifiers
                });

                return actions;
            }

            if (def.ActionType == CommandActionType.Scroll)
            {
                const int ScrollAmount = 3; // Same base scroll amount as before
                int delta;

                if (string.Equals(def.Kind, "ScrollDown", StringComparison.OrdinalIgnoreCase))
                {
                    delta = -ScrollAmount;  // Negative = scroll down
                }
                else if (string.Equals(def.Kind, "ScrollUp", StringComparison.OrdinalIgnoreCase))
                {
                    delta = ScrollAmount;   // Positive = scroll up
                }
                else
                {
                    // Unknown scroll variant: no actions
                    return actions;
                }

                actions.Add(new ExecutableAction
                {
                    Kind = ActionKind.ScrollVertical,
                    ScrollDelta = delta
                });

                return actions;
            }

            if (def.ActionType == CommandActionType.TextInput)
            {
                var raw = intent.RawText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw))
                    return actions; // Nothing meaningful to type

                var lower = raw.ToLowerInvariant();
                string textToType = raw.Trim();

                // Try to strip the first matching trigger phrase from the catalog
                if (def.Phrases != null)
                {
                    foreach (var phrase in def.Phrases)
                    {
                        if (string.IsNullOrWhiteSpace(phrase))
                            continue;

                        var lowerPhrase = phrase.ToLowerInvariant();
                        var idx = lower.IndexOf(lowerPhrase, StringComparison.InvariantCulture);
                        if (idx >= 0)
                        {
                            textToType = raw.Substring(idx + phrase.Length).Trim();
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(textToType))
                    return actions; // No payload text after stripping trigger

                actions.Add(ExecutableAction.TextInput(textToType));
                return actions;
            }

            // Other ActionTypes will be added later (Mouse, etc.)
            return actions;
        }

        // ---------- Foreground window helpers (context for agent/planner) ----------

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>
        /// Snapshot of the current foreground window: process name + title.
        /// Used by future agent logic to decide where to apply actions.
        /// </summary>
        private (string? ProcessName, string? WindowTitle) GetActiveWindowInfo()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    return (null, null);                      // No active window
                }

                // Get window title
                int length = GetWindowTextLength(hwnd);
                var sb = new StringBuilder(length + 1);
                _ = GetWindowText(hwnd, sb, sb.Capacity);
                string? title = sb.ToString();

                // Get process id and name
                GetWindowThreadProcessId(hwnd, out uint pid);
                string? processName = null;

                if (pid != 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        processName = proc.ProcessName;
                    }
                    catch
                    {
                        // Swallow; leave processName as null if we can't resolve it
                    }
                }

                return (processName, title);
            }
            catch
            {
                // On any unexpected error, return nulls to stay safe.
                return (null, null);
            }
        }

        // Build the context snapshot that will be passed into the agent/planner.
        private AgentContext BuildAgentContext(CommandIntent intent)
        {
            var (processName, windowTitle) = GetActiveWindowInfo();

            return new AgentContext
            {
                Transcript = intent.RawText ?? string.Empty,
                Intents = new List<CommandIntent> { intent },
                ActiveProcessName = processName,
                ActiveWindowTitle = windowTitle
            };
        }


        // When no catalog intents are found, let the agent plan directly from raw transcript.
        private AgentContext BuildAgentContextFromTranscript(string transcriptText, IReadOnlyList<CommandIntent> intents)
        {
            var (processName, windowTitle) = GetActiveWindowInfo();
            return new AgentContext
            {
                Transcript = transcriptText ?? string.Empty,
                Intents = intents ?? new List<CommandIntent>(),
                ActiveProcessName = processName,
                ActiveWindowTitle = windowTitle
            };
        }



        // Try to execute a key-chord command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        // Try to execute a key-chord command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        // Try to execute a key-chord command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        // Try to execute a key-chord command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        private bool TryExecuteKeyChordFromCatalog(CommandIntent intent)
        {
            var def = FindCommandDefinition(intent.Kind);            // Find the definition for this intent
            if (def == null)                                         // If we don't know this command in the catalog
                return false;

            if (def.ActionType != CommandActionType.KeyChord)        // Only handle key-chord commands here
                return false;

            var actions = BuildActionsFor(def, intent);              // Build actions for this command
            if (actions.Count == 0)                                  // Nothing to do
                return false;

            var plan = new ActionPlan
            {
                Name = def.Kind,
                Actions = actions
            };

            RunActionPlan(plan);                                     // Run via the ActionPlan overload
            return true;                                             // Indicate that we executed via catalog
        }



        // Try to execute a text-input command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        // Try to execute a text-input command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        private bool TryExecuteTextInputFromCatalog(CommandIntent intent)
        {
            var def = FindCommandDefinition(intent.Kind);              // Find definition for this intent
            if (def == null)
                return false;

            if (def.ActionType != CommandActionType.TextInput)         // Only handle TextInput commands here
                return false;

            var actions = BuildActionsFor(def, intent);                // Build actions for this command
            if (actions.Count == 0)
                return false;

            var plan = new ActionPlan
            {
                Name = def.Kind,
                Actions = actions
            };

            RunActionPlan(plan);                                       // Run via the ActionPlan overload
            return true;
        }


        // Indicate that we executed via catalog
        // Try to execute a scroll command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        // Try to execute a scroll command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        // Try to execute a scroll command based on catalog metadata.
        // Returns true if it executed something, false if we should fall back.
        private bool TryExecuteScrollCommand(CommandIntent intent)
        {
            var def = FindCommandDefinition(intent.Kind);              // Find command definition for this intent
            if (def == null)                                           // Unknown command
                return false;

            if (def.ActionType != CommandActionType.Scroll)            // Only handle Scroll-type commands here
                return false;

            var actions = BuildActionsFor(def, intent);                // Build actions for this scroll command
            if (actions.Count == 0)                                    // Nothing to do
                return false;

            var plan = new ActionPlan
            {
                Name = def.Kind,
                Actions = actions
            };

            RunActionPlan(plan);                                       // Run via the ActionPlan overload
            return true;
        }


        private void ExecuteIntent(CommandIntent intent)      // Takes a parsed intent and performs the corresponding action
        {
            if (intent == null)                              // Guard against null intents
                return;

            switch (intent.Kind)                             // Branch based on intent type
            {
                case "NewTab":
                    // First, try the data-driven path via the catalog.
                    // If anything is missing, fall back to the old hard-coded shortcut.
                    if (!TryExecuteKeyChordFromCatalog(intent))
                    {
                        // Fallback: real action using hard-coded Ctrl+T
                        _inputSimulator.Keyboard.ModifiedKeyStroke(
                            VirtualKeyCode.CONTROL,
                            VirtualKeyCode.VK_T);
                    }

                    // Log message kept EXACTLY the same as before
                    AudioHistoryList.Items.Add("  [Execute] NewTab (Ctrl+T sent)");
                    break;

                case "CloseTab":
                    // Same pattern for CloseTab.
                    if (!TryExecuteKeyChordFromCatalog(intent))
                    {
                        // Fallback: real action using hard-coded Ctrl+W
                        _inputSimulator.Keyboard.ModifiedKeyStroke(
                            VirtualKeyCode.CONTROL,
                            VirtualKeyCode.VK_W);
                    }

                    // Log message kept EXACTLY the same as before
                    AudioHistoryList.Items.Add("  [Execute] CloseTab (Ctrl+W sent)");
                    break;
                default:
                    // First, try catalog-based key-chord
                    if (TryExecuteKeyChordFromCatalog(intent))
                    {
                        AudioHistoryList.Items.Add($"  [Execute] {intent.Kind} (key chord sent)");
                    }
                    else if (TryExecuteScrollCommand(intent))
                    {
                        AudioHistoryList.Items.Add($"  [Execute] {intent.Kind} (scroll sent)");
                    }
                    else if (TryExecuteTextInputFromCatalog(intent))
                    {
                        AudioHistoryList.Items.Add($"  [Execute] {intent.Kind} (text input sent)");
                    }
                    else
                    {
                        // NEW: Ask the agent/planner if it wants to handle this.
                        var context = BuildAgentContext(intent);
                        var plan = _planner.Plan(context);

                        if (plan != null)
                        {
                            RunActionPlan(plan);
                            AudioHistoryList.Items.Add(
                                $"  [Execute] {intent.Kind} (agent plan executed: {plan.Name})");
                        }
                        else
                        {
                            // Fallback: pure dry run when neither catalog nor agent knows what to do.
                            AudioHistoryList.Items.Add($"  [Execute] {intent.Kind} (dry run)");
                        }
                    }
                    break;




            }
        }

        private void UpdateButtonStates(bool isListening)     // Helper to enable/disable Start/Stop buttons based on listening state
        {
            StartListeningButton.IsEnabled = !isListening;    // Start enabled only when not listening
            StopListeningButton.IsEnabled = isListening;      // Stop enabled only when listening
        }

        protected override void OnClosed(EventArgs e)         // Called when the window is closing
        {
            _recorder.SegmentCompleted -= Recorder_SegmentCompleted; // Unsubscribe from event to avoid callbacks after close
            _recorder.Dispose();                              // Dispose AudioRecorder to release mic and writer resources
            base.OnClosed(e);                                 // Let base class perform its own cleanup logic
        }
    }
}
