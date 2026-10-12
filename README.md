# UniMouse Macro AI

AI-assisted **desktop macro recorder & player** for Windows: describe a repetitive
task in natural language, get a structured, validated macro back from a **local**
Qwen2.5-Coder model, review it in the editor, then replay it with OS-level input
simulation. Nothing leaves your machine — the model runs locally via Ollama and
the API server binds to `127.0.0.1` only.

> ## Acceptable use policy (hard product constraint)
>
> **Allowed:** productivity automation — form filling, data entry, repetitive
> clicks/typing, app workflows, accessibility helpers.
>
> **Refused by design and enforced in code** (see `validation.py` policy + tests):
> aimbot, triggerbot, wallhack, auto-aim, recoil scripts, anti-cheat evasion or
> detection bypass, kernel drivers, DLL injection / process hollowing, reading or
> writing game memory, Cheat Engine workflows, HWID/MAC spoofing, "undetected"
> input, interception/virtual-HID driver tricks. Requests matching these are
> rejected before the model is even called, and the refusal message suggests
> legitimate alternatives.

---

## Architecture

```
┌─────────────────────────┐        HTTP (127.0.0.1:8777)      ┌──────────────────────────┐
│  UniMouse WPF app       │ ─── POST /macro/generate ───────▶ │  Python FastAPI service   │
│  (.NET 8, MVVM)         │ ◀── validated JSON macro ──────── │  safety policy → Qwen →   │
│                         │                                   │  schema+pydantic validate │
│  MacroStore (.json)     │        POST /macro/validate       │  OllamaBackend /          │
│  MacroRunner            │ ◀──────────────────────────────── │  EchoBackend              │
│  Win32InputExecutor     │                                   └────────────┬─────────────┘
│  (SendInput only)       │                                                │ localhost:11434
│  Ctrl+Alt+F12 estop     │                                                ▼
└─────────────────────────┘                                        qwen2.5-coder:7b (Ollama)
```

| Component | Path | Responsibility |
|---|---|---|
| Core models & validator | `libs/unimouse.core` | Macro schema objects, pure-function safety validation |
| Runner | `libs/unimouse.core/Runtime/MacroRunner.cs` | State machine: timeouts, cancellation, ReleaseAll on exit |
| Input executor | `libs/unimouse.input/Win32InputExecutor.cs` | **Only** place touching `SendInput` (documented public API) |
| WPF app | `apps/unimouse.wpf` | UI, macro library, AI generation, playback, emergency hotkey |
| AI service | `apps/qwen-service` | FastAPI: content policy, LLM call, output validation (never trusts the model) |
| Shared contract | `shared/schemas/macro.schema.json` | Single source of truth; parity-tested on both sides |

### Safety layers (defense in depth)

1. **Content policy** (`validation.py::policy_check`) — regex gate refuses cheat/evasion intents *before* inference.
2. **Model system prompt** — instructs Qwen to answer `{"rejected": true}` for out-of-scope requests.
3. **Structural validation** — model output must pass JSON Schema **and** Pydantic (forbidden VK codes: `LWIN/RWIN/CAPSLOCK`, control chars, size/time caps). Invalid output is never released to the client.
4. **Client-side re-validation** — `MacroValidator` (C#) mirrors the same rules; `MacroRunner` refuses to execute anything that fails validation.
5. **Runtime guardrails** — hard `maxDurationMs` timeout (≤15 min), single-playback lock, cooperative cancellation, `ReleaseAllAsync()` in a `finally` block, global **Ctrl+Alt+F12** emergency stop.
6. **OS posture** — `asInvoker` manifest (no admin), `uiAccess=false`, no hooks, no drivers, no other-process access.

---

## Quick start

### Prerequisites
- Windows 10/11, .NET 8 SDK
- Python 3.11+
- [Ollama](https://ollama.com) with `qwen2.5-coder:7b` (optional — see Echo backend)

### 1. AI service

```powershell
cd apps\qwen-service
python -m venv .venv ; .\.venv\Scripts\Activate.ps1
pip install -e ".[dev]"

# CPU-only / no-GPU demo mode:
$env:QWEN_BACKEND = "echo"
uvicorn qwen_service.main:app --host 127.0.0.1 --port 8777

# Real model mode (Ollama running locally):
ollama pull qwen2.5-coder:7b
$env:QWEN_BACKEND = "ollama"
uvicorn qwen_service.main:app --host 127.0.0.1 --port 8777

# OpenAI-compatible server mode (vLLM / llama.cpp / LM Studio / TGI):
$env:QWEN_BACKEND   = "openai"          # "vllm" is an alias
$env:OPENAI_BASE_URL= "http://127.0.0.1:8000/v1"
$env:OPENAI_MODEL   = "qwen2.5-coder-7b-instruct"
$env:OPENAI_API_KEY = ""                # optional Bearer token
uvicorn qwen_service.main:app --host 127.0.0.1 --port 8777
```

Optional shared secret: set `UNIMOUSE_API_KEY` on both the service and the app;
all `/macro/*` calls then require the `x-api-key` header.

### 2. Desktop app

```powershell
dotnet build UniMouseMacroAI.sln
dotnet run --project apps\unimouse.wpf
```

### 3. Use it

1. Type an intent, e.g. `type "Good morning" then click at 960 , 540`.
2. Press **Generate** — the macro appears in the library.
3. **Review** the steps (coordinates are screen-layout dependent!).
4. **Play**; hit **Ctrl+Alt+F12** any time to stop and release all keys.

### Example macro file (`*.unimacro.json`)

```json
{
  "schemaVersion": 1,
  "id": "ai.morning-greeting",
  "name": "Morning greeting",
  "description": "Type a greeting and click Send",
  "steps": [
    { "kind": "text", "text": "Good morning!", "delayBeforeMs": 300 },
    { "kind": "key", "keyCode": 13, "holdMs": 30 }
  ],
  "repeatCount": 1,
  "repeatDelayMs": 500,
  "maxDurationMs": 120000
}
```

---

## Tests

```powershell
# Python (service, policy, schema parity) — 55 tests
cd apps\qwen-service ; pytest -q

# C# (validator, runner state machine) — requires .NET 8 SDK on Windows
dotnet test libs\unimouse.core.tests
```

Key suites:
- `tests/test_policy.py` — every forbidden category (aimbot/EAC/spoofing/injection/memory) **must** be rejected; benign productivity prompts **must** pass.
- `tests/test_schema_parity.py` — Pydantic fields/enums == JSON Schema fields (catches C#/Python drift).
- `MacroRunnerTests` / `MacroValidatorTests` — timeout abort, user cancel, ReleaseAll guarantee, refusal to run invalid macros.

## Repository layout

```
UniMouseMacroAI.sln
Directory.Build.props
shared/schemas/macro.schema.json
libs/unimouse.core/          # models, validator, runner (+ IInputExecutor)
libs/unimouse.input/         # Win32 SendInput executor
libs/unimouse.core.tests/    # xUnit
apps/unimouse.wpf/           # WPF UI (App, MainWindow, MainViewModel, Services)
apps/qwen-service/           # FastAPI + Qwen clients + pytest suite
```

## Roadmap / TODO

- [x] Step editor grid in WPF (`Views/StepEditorWindow.xaml` + `ViewModels/StepEditorViewModel.cs`): inline-editable rows (kind combo, delays, friendly key names via `Core/Runtime/VirtualKey`, text, coords, buttons, scroll), per-row validation errors, add/remove/reorder; Save goes through `MacroValidator` before persisting — invalid macros can never be written. Drag-reorder implemented (`PreviewMouseLeftButtonDown`/`PreviewMouseMove` + `DoDragDrop`, `StepEditorViewModel.MoveTo`) alongside Up/Down buttons; headless VM tests in `apps/unimouse.wpf.tests` (registered in the solution)
- [ ] Macro recorder (capture real user input into steps, with explicit "recording" UX) — **not implemented**: an earlier attempt was lost and never landed in the repository; requires WH_KEYBOARD_LL/WH_MOUSE_LL passive hooks behind an explicit record-mode UI, still subject to the safety policy (no capture while elevated/other secure desktops)
- [x] Per-monitor DPI coordinate normalization helper (`libs/unimouse.core/Runtime/CoordinateSpace.cs` + xUnit tests)
- [ ] Import/export dialog + signed macro sharing (read-only previews)
- [x] OpenAI-compatible backend option (vLLM) behind the same `LlmBackend` interface
- [x] Per-macro start hotkeys via RegisterHotKey (`Services/HotkeyManager.cs`, wired into MainWindow + `MainViewModel.PlayById`) — tray icon still TODO
- [x] CI: GitHub Actions (`.github/workflows/ci.yml`: pytest on ubuntu, dotnet build+test on windows-latest, schema sanity) — added

## Security notes

- The service listens on loopback only; do not port-forward it.
- Model output is treated as untrusted input end-to-end.
- Macros can only synthesize what the logged-in user could type themselves; elevated
  target windows are unreachable by design (UIPI), which we consider a feature.
