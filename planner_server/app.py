from fastapi import FastAPI
from pydantic import BaseModel, Field
from typing import List, Optional
import requests, json, re

app = FastAPI()

# --------- DTOs matching your C# HttpAgentPlanner wire format ---------

class IntentDto(BaseModel):
    kind: str = Field(alias="Kind")
    rawText: str = Field(alias="RawText")

    class Config:
        allow_population_by_field_name = True

class PlanRequestDto(BaseModel):
    transcript: str = Field(alias="Transcript")
    activeProcessName: Optional[str] = Field(default=None, alias="ActiveProcessName")
    activeWindowTitle: Optional[str] = Field(default=None, alias="ActiveWindowTitle")
    intents: List[IntentDto] = Field(default_factory=list, alias="Intents")

    class Config:
        allow_population_by_field_name = True

class ActionDto(BaseModel):
    kind: str
    mainKey: Optional[str] = None
    modifiers: Optional[List[str]] = None
    text: Optional[str] = None
    scrollDelta: Optional[int] = None
    millisecondsDelay: Optional[int] = None
    x: Optional[int] = None
    y: Optional[int] = None
    deltaX: Optional[int] = None
    deltaY: Optional[int] = None
    button: Optional[str] = None

class PlanResponseDto(BaseModel):
    name: str = "LLMPlan"
    actions: List[ActionDto] = []

# --------- Helpers ---------

OLLAMA_URL = "http://localhost:11434/api/chat"
MODEL = "llama3.2"

ALLOWED_ACTION_KINDS = [
    "KeyChord", "KeyTap", "TextInput",
    "ScrollVertical", "ScrollHorizontal",
    "MouseMoveTo", "MouseMoveBy",
    "MouseDown", "MouseUp", "MouseClick", "MouseDoubleClick",
    "Wait"
]

USE_RULE_BASED = False  # keep False so LLM plans run; set True only for fallback testing

def call_ollama(system_prompt: str, user_prompt: str) -> str:
    payload = {
        "model": MODEL,
        "stream": False,
        "messages": [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
        ],
    }
    r = requests.post(OLLAMA_URL, json=payload, timeout=20)
    r.raise_for_status()
    return r.json()["message"]["content"]

def extract_json(text: str):
    """
    Robustly pull the first JSON object from model text.
    """
    try:
        return json.loads(text)
    except:
        pass

    m = re.search(r"\{.*\}", text, flags=re.S)
    if not m:
        return None
    try:
        return json.loads(m.group(0))
    except:
        return None

# --------- System prompt (global, so no f-string brace issues) ---------

allowed_kinds = ", ".join(ALLOWED_ACTION_KINDS)

SYSTEM_PROMPT = (
    "You are a Windows voice automation planner.\n"
    "You MUST return ONLY valid JSON for an ActionPlan with this schema:\n\n"
    "{\n"
    '  "name": "ShortPlanName",\n'
    '  "actions": [\n'
    "    {\n"
    f'      "kind": "<one of: {allowed_kinds}>",\n'
    '      "mainKey": "One VirtualKeyCode name only (e.g., LWIN, RETURN, VK_T).",\n'
    '      "modifiers": ["CONTROL","SHIFT","MENU","LWIN"],\n'
    '      "text": "text to type",\n'
    '      "scrollDelta": -3,\n'
    '      "millisecondsDelay": 500,\n'
    '      "x": 500, "y": 500,\n'
    '      "deltaX": 20, "deltaY": -10,\n'
    '      "button": "Left|Right|Middle"\n'
    "    }\n"
    "  ]\n"
    "}\n\n"
    "Rules:\n"
    
    "- OPEN-APP CANONICAL FLOW (use this whenever user says open/launch/start an app):\n"
    "  1) {\"kind\":\"KeyTap\",\"mainKey\":\"LWIN\"}\n"
    "  2) {\"kind\":\"TextInput\",\"text\":\"<app name>\"}\n"
    "  3) {\"kind\":\"KeyTap\",\"mainKey\":\"RETURN\"}\n"
    "  4) {\"kind\":\"Wait\",\"millisecondsDelay\":800}\n"
    "- Never add modifiers to LWIN unless the user explicitly says a chord (e.g., 'Win+R').\n"


    "- Use ONLY the allowed action kinds.\n"
    "- For KeyChord/KeyTap: mainKey MUST be SINGLE key enum name. Never use '+' or '|' or multiple keys.\n"
    "- Use modifiers[] for chords: e.g., mainKey=\"VK_T\", modifiers=[\"CONTROL\"].\n"
    "- Do NOT return NoOp unless the user gives no actionable intent.\n"
    "- Prefer multi-step plans. Example:\n"
    "[\n"
    "  {\"kind\":\"KeyChord\",\"mainKey\":\"LWIN\"},\n"
    "  {\"kind\":\"TextInput\",\"text\":\"chrome\"},\n"
    "  {\"kind\":\"KeyTap\",\"mainKey\":\"RETURN\"},\n"
    "  {\"kind\":\"Wait\",\"millisecondsDelay\":800},\n"
    "  {\"kind\":\"KeyChord\",\"mainKey\":\"VK_T\",\"modifiers\":[\"CONTROL\"]}\n"
    "]\n\n"
    "- Output JSON ONLY. No commentary."
)


def normalize_plan(plan_json: dict) -> dict:
    if not isinstance(plan_json, dict):
        return {"name": "NoOp", "actions": []}

    actions = plan_json.get("actions") or []
    cleaned = []

    for a in actions:
        if not isinstance(a, dict):
            continue

        kind = (a.get("kind") or "").strip()
        if kind not in ALLOWED_ACTION_KINDS or kind == "NoOp":
            continue

        # convert empty strings to None
        for k, v in list(a.items()):
            if isinstance(v, str) and v.strip() == "":
                a[k] = None

        if kind in ("KeyChord", "KeyTap"):
            mk = (a.get("mainKey") or "").strip()
            mk = re.split(r"[|+,\s]+", mk)[0] if mk else None
            if not mk:   # drop broken key actions
                continue
            a["mainKey"] = mk

            mods = a.get("modifiers") or []
            norm_mods = []
            for m in mods:
                if not m:
                    continue
                m = str(m).upper().strip()
                if m in ("CTRL", "CONTROL"):
                    m = "CONTROL"
                if m in ("ALT", "MENU"):
                    m = "MENU"
                if m in ("SHIFT", "CONTROL", "MENU", "LWIN"):
                    norm_mods.append(m)
            a["modifiers"] = norm_mods or None
        else:
            a["mainKey"] = None
            a["modifiers"] = None

        # dedupe consecutive identical TextInput
        if kind == "TextInput" and cleaned:
            prev = cleaned[-1]
            if prev["kind"] == "TextInput" and (prev.get("text") or "").lower() == (a.get("text") or "").lower():
                continue

        cleaned.append(a)

    plan_json["actions"] = cleaned
    if not plan_json.get("name"):
        plan_json["name"] = "LLMPlan"
    return plan_json



def maybe_rule_based_plan(transcript: str):
    t = transcript.strip()
    tl = t.lower()

    if not (tl.startswith("open ") or tl.startswith("launch ") or tl.startswith("start ")):
        return None

    # split "open X and type Y"
    app_part = t
    type_part = ""
    if " and type " in tl:
        parts = re.split(r"\s+and\s+type\s+", t, flags=re.I, maxsplit=1)
        app_part = parts[0]
        type_part = parts[1].strip() if len(parts) > 1 else ""

    # remove leading verb
    app_name = re.sub(r"^(open|launch|start)\s+", "", app_part, flags=re.I).strip()
    if not app_name:
        return None

    # Human-like Start-menu flow with small waits so search results populate
    actions = [
        {"kind": "KeyTap", "mainKey": "LWIN"},
        {"kind": "Wait", "millisecondsDelay": 150},
        {"kind": "TextInput", "text": app_name},
        {"kind": "Wait", "millisecondsDelay": 400},
        {"kind": "KeyTap", "mainKey": "RETURN"},
        {"kind": "Wait", "millisecondsDelay": 1200},
    ]

    if type_part:
        actions += [
            {"kind": "Wait", "millisecondsDelay": 300},
            {"kind": "TextInput", "text": type_part},
        ]

    safe_name = re.sub(r"\W+", "", app_name.title())
    return {"name": f"Open{safe_name}", "actions": actions}


# --------- Endpoint ---------

@app.post("/plan", response_model=PlanResponseDto)
def plan(req: PlanRequestDto):
    transcript = req.transcript.strip()
    if not transcript:
        return PlanResponseDto(name="Empty", actions=[])
    
    if USE_RULE_BASED:
        rb_plan = maybe_rule_based_plan(transcript)
        if rb_plan:
            rb_plan = normalize_plan(rb_plan)
            return PlanResponseDto(**rb_plan)



    user_prompt = {
        "transcript": transcript,
        "activeProcessName": req.activeProcessName,
        "activeWindowTitle": req.activeWindowTitle,
        "intents": [i.dict() for i in req.intents],
    }

    try:
        model_text = call_ollama(SYSTEM_PROMPT, json.dumps(user_prompt))
        plan_json = extract_json(model_text)
        if not plan_json:
            return PlanResponseDto(name="NoOp", actions=[])

        plan_json = normalize_plan(plan_json)
        return PlanResponseDto(**plan_json)


    except Exception:
        return PlanResponseDto(name="NoOp", actions=[])

# Run with:
# uvicorn app:app --host 127.0.0.1 --port 5005
