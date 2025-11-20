using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WindowsInput.Native;
using System.IO;

namespace SttRecorderApp
{


    /// <summary>
    /// HTTP-based planner that calls a local LLM service
    /// (e.g., a small FastAPI + Ollama server on http://localhost:5005/plan).
    ///
    /// IMPORTANT:
    /// - If anything fails (no server, bad JSON, etc.), this always returns null.
    /// - That guarantees no crashes and lets you keep iterating safely.
    /// </summary>
    public sealed class HttpAgentPlanner : IAgentPlanner
    {

        private static void PlannerLog(string msg)
        {
            try
            {
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "SttRecorder");
                Directory.CreateDirectory(baseDir);
                var logPath = Path.Combine(baseDir, "planner_debug.log");
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} {msg}\n");
            }
            catch
            {
                // never let logging break planning
            }
        }

        private static readonly HttpClient _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private readonly string _endpoint;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public HttpAgentPlanner(string endpoint)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        }

        public ActionPlan? Plan(AgentContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Transcript))
            {
                return null;
            }

            try
            {
                // --- 1) Build request payload for the LLM planning service ---
                var requestDto = new PlanRequestDto
                {
                    Transcript = context.Transcript,
                    ActiveProcessName = context.ActiveProcessName,
                    ActiveWindowTitle = context.ActiveWindowTitle,
                    Intents = BuildIntentDtos(context.Intents)
                };


                var json = JsonSerializer.Serialize(requestDto, _jsonOptions);
                Console.WriteLine("[Planner] REQUEST JSON => " + json);
                PlannerLog("[Planner] REQUEST => " + json);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                // --- 2) Synchronously call the local HTTP planning endpoint ---
                // Note: we use .GetAwaiter().GetResult() to keep the interface sync for now.
                // Later, we can move planning to a background thread / async flow.
                var response = _client.PostAsync(_endpoint, content)
                                      .GetAwaiter()
                                      .GetResult();

                PlannerLog("[Planner] STATUS <= " + (int)response.StatusCode + " " + response.ReasonPhrase);


                if (!response.IsSuccessStatusCode)
                {
                    // Non-200: treat as "no plan".
                    return null;
                }

                var responseText = response.Content.ReadAsStringAsync()
                                                   .GetAwaiter()
                                                   .GetResult();
                Console.WriteLine("[Planner] RESPONSE TEXT <= " + responseText);
                PlannerLog("[Planner] RESPONSE <= " + responseText);


                if (string.IsNullOrWhiteSpace(responseText))
                {
                    return null;
                }

                // --- 3) Parse the returned JSON into an ActionPlan ---
                var planDto = JsonSerializer.Deserialize<PlanResponseDto>(responseText, _jsonOptions);
                if (planDto == null || planDto.Actions == null || planDto.Actions.Count == 0)
                {
                    return null;
                }

                return ConvertToActionPlan(planDto);
            }
            catch (Exception ex)
            {
                PlannerLog("[Planner] EXCEPTION !! " + ex.ToString());
                return null;
            }

        }

        // ------------ Helpers: DTOs + conversion ------------

        private static List<IntentDto> BuildIntentDtos(IReadOnlyList<CommandIntent> intents)
        {
            var list = new List<IntentDto>();

            if (intents == null) return list;

            foreach (var i in intents)
            {
                if (i == null) continue;

                list.Add(new IntentDto
                {
                    Kind = i.Kind ?? string.Empty,
                    RawText = i.RawText ?? string.Empty
                });
            }

            return list;
        }

        private static ActionPlan ConvertToActionPlan(PlanResponseDto dto)
        {
            var actions = new List<ExecutableAction>();

            if (dto.Actions != null)
            {
                foreach (var actionDto in dto.Actions)
                {
                    var action = ConvertToExecutableAction(actionDto);
                    if (action != null)
                    {
                        actions.Add(action);
                    }
                }
            }

            var name = string.IsNullOrWhiteSpace(dto.Name) ? "LLMPlan" : dto.Name!.Trim();

            return new ActionPlan
            {
                Name = name,
                Actions = actions
            };
        }



        private static ExecutableAction? ConvertToExecutableAction(ActionDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Kind))
                return null;

            // 1) Parse ActionKind safely from string.
            if (!Enum.TryParse<ActionKind>(dto.Kind, ignoreCase: true, out var kind))
            {
                return null; // Unknown action kind -> ignore this action
            }

            // Prepare optional values
            VirtualKeyCode? mainKey = null;
            List<VirtualKeyCode>? modifiers = null;
            string? text = null;
            int? scrollDelta = dto.ScrollDelta;
            int? millisecondsDelay = dto.MillisecondsDelay;
            int? x = dto.X;
            int? y = dto.Y;
            int? deltaX = dto.DeltaX;
            int? deltaY = dto.DeltaY;
            MouseButton? button = null;

            // 2) Optional primary key (for KeyChord, KeyTap)
            if (!string.IsNullOrWhiteSpace(dto.MainKey) &&
                Enum.TryParse<VirtualKeyCode>(dto.MainKey, ignoreCase: true, out var vk))
            {
                mainKey = vk;
            }

            // 3) Optional modifier keys
            if (dto.Modifiers != null && dto.Modifiers.Count > 0)
            {
                var mods = new List<VirtualKeyCode>();
                foreach (var m in dto.Modifiers)
                {
                    if (string.IsNullOrWhiteSpace(m)) continue;
                    if (Enum.TryParse<VirtualKeyCode>(m, ignoreCase: true, out var mvk))
                    {
                        mods.Add(mvk);
                    }
                }

                if (mods.Count > 0)
                {
                    modifiers = mods;
                }
            }

            // 4) Optional text (for TextInput)
            if (!string.IsNullOrEmpty(dto.Text))
            {
                text = dto.Text;
            }

            // 5) Mouse button
            if (!string.IsNullOrWhiteSpace(dto.Button) &&
                Enum.TryParse<MouseButton>(dto.Button, ignoreCase: true, out var parsedButton))
            {
                button = parsedButton;
            }

            // 6) Build ExecutableAction using a single object initializer
            return new ExecutableAction
            {
                Kind = kind,
                MainKey = mainKey,
                Modifiers = modifiers,
                Text = text,
                ScrollDelta = scrollDelta,
                MillisecondsDelay = millisecondsDelay,
                X = x,
                Y = y,
                DeltaX = deltaX,
                DeltaY = deltaY,
                Button = button
            };
        }

        // ------------ DTO definitions for JSON wire format ------------

        private sealed class PlanRequestDto
        {
            public string Transcript { get; set; } = string.Empty;
            public string? ActiveProcessName { get; set; }
            public string? ActiveWindowTitle { get; set; }
            public List<IntentDto> Intents { get; set; } = new();
        }

        private sealed class IntentDto
        {
            public string Kind { get; set; } = string.Empty;
            public string RawText { get; set; } = string.Empty;
        }

        private sealed class PlanResponseDto
        {
            public string? Name { get; set; }
            public List<ActionDto> Actions { get; set; } = new();
        }

        private sealed class ActionDto
        {
            // Must match your C# enum names (ActionKind) for easiest mapping:
            // "KeyChord", "KeyTap", "TextInput", "ScrollVertical", "MouseClick", etc.
            public string? Kind { get; set; }

            // Keyboard-related
            public string? MainKey { get; set; }            // e.g., "VK_T", "RETURN"
            public List<string>? Modifiers { get; set; }    // e.g., ["CONTROL", "SHIFT"]

            // Text typing
            public string? Text { get; set; }

            // Scroll + timing
            public int? ScrollDelta { get; set; }
            public int? MillisecondsDelay { get; set; }

            // Mouse coordinates / deltas
            public int? X { get; set; }
            public int? Y { get; set; }
            public int? DeltaX { get; set; }
            public int? DeltaY { get; set; }

            // Mouse button: "Left", "Right" (must match MouseButton enum)
            public string? Button { get; set; }
        }
    }
}
