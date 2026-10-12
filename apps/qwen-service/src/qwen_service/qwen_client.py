"""Qwen2.5-Coder inference backends.

Two interchangeable backends:
  * OllamaBackend  — talks to a local `ollama serve` running qwen2.5-coder.
                     Default model: qwen2.5-coder:7b, host http://127.0.0.1:11434.
  * EchoBackend    — deterministic template generator used in tests and when
                     QWEN_BACKEND=echo (no GPU required).

The service is designed so that model output is NEVER trusted: it is parsed,
schema-validated and safety-checked before being returned.
"""

from __future__ import annotations

import json
import os
import re
import uuid
from abc import ABC, abstractmethod

import httpx

SYSTEM_PROMPT = """You are UniMouse Macro AI, a code model that converts a user's
desktop-automation intent into a strict JSON macro definition.

HARD RULES:
1. Output ONLY a JSON object. No markdown fences, no commentary.
2. The object MUST conform to this schema (camelCase keys):
{
  "schemaVersion": 1,
  "id": "ai.<slug>",
  "name": "<short title>",
  "description": "<user intent>",
  "steps": [ { "kind": "key|text|mouseMove|mouseClick|mouseScroll|delay", ... } ],
  "repeatCount": <int -1..10000>,
  "repeatDelayMs": <int 0..60000>,
  "maxDurationMs": <int 1..900000>
}
3. Step fields: key{keyCode,holdMs}, text{text}, mouseMove{x,y},
   mouseClick{button:left|right|middle,clicks}, mouseScroll{scroll:up|down,scrollAmount},
   delay{delayBeforeMs}. Every step may also have delayBeforeMs.
4. keyCode must be a Windows virtual-key code 1..254. NEVER use 0x5B/0x5C (Win) or 0x14.
5. Coordinates must stay inside the screen size given by the user.
6. If the intent is a game cheat (aimbot, triggerbot, wallhack, recoil control),
   anti-cheat evasion, input spoofing, memory reading or driver injection,
   output exactly: {"rejected": true, "reason": "<why>"}
7. Prefer human-like delays (>=50ms between actions). Cap total steps at 200.
8. Common VK codes: A-Z 0x41-0x5A, 0-9 0x30-0x39, ENTER 0x0D, TAB 0x09, ESC 0x1B,
   SPACE 0x20, CTRL 0x11, SHIFT 0x10, ALT 0x12, WIN forbidden, arrows 0x25-0x28,
   F1-F12 0x70-0x7B, BACKSPACE 0x08, HOME 0x24, END 0x23.
"""


class LlmBackend(ABC):
    @abstractmethod
    async def complete_macro(self, prompt: str, context: str, width: int, height: int) -> str:
        """Return raw model text which should be a JSON object."""


def _extract_json(text: str) -> dict:
    """Best-effort extraction of the first JSON object from model output."""
    text = text.strip()
    fence = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", text, re.DOTALL)
    if fence:
        text = fence.group(1)
    start = text.find("{")
    if start == -1:
        raise ValueError("no JSON object found in model output")
    depth = 0
    for i, ch in enumerate(text[start:], start):
        depth += ch == "{"
        depth -= ch == "}"
        if depth == 0:
            return json.loads(text[start : i + 1])
    raise ValueError("unbalanced JSON braces in model output")


class OllamaBackend(LlmBackend):
    def __init__(self, host: str | None = None, model: str | None = None, timeout_s: float = 120):
        self.host = host or os.environ.get("OLLAMA_HOST", "http://127.0.0.1:11434")
        self.model = model or os.environ.get("QWEN_MODEL", "qwen2.5-coder:7b")
        self.timeout = timeout_s

    async def complete_macro(self, prompt: str, context: str, width: int, height: int) -> str:
        user = (
            f"Intent: {prompt}\n"
            f"Context: {context or 'none'}\n"
            f"Screen: {width}x{height}\n"
            "Respond with the JSON object only."
        )
        async with httpx.AsyncClient(base_url=self.host, timeout=self.timeout) as client:
            r = await client.post(
                "/api/generate",
                json={
                    "model": self.model,
                    "system": SYSTEM_PROMPT,
                    "prompt": user,
                    "stream": False,
                    "format": "json",
                    "options": {"temperature": 0.1, "num_predict": 4096},
                },
            )
            r.raise_for_status()
            return r.json().get("response", "")


class EchoBackend(LlmBackend):
    """Deterministic offline backend: recognizes a few safe intents, else emits
    a single-step placeholder. Used for CI and CPU-only machines."""

    async def complete_macro(self, prompt: str, context: str, width: int, height: int) -> str:
        p = prompt.lower()
        slug = re.sub(r"[^a-z0-9]+", "-", p)[:24].strip("-") or "macro"
        mid = f"ai.{slug}-{uuid.uuid4().hex[:6]}"

        steps: list[dict] = []
        m = re.search(r"type\s+[\"'](.+?)[\"']", prompt)
        if m:
            steps.append({"kind": "text", "text": m.group(1), "delayBeforeMs": 200})
        m = re.search(r"(click|press)\s+(?:the\s+)?(?:button\s+)?at\s+(\d+)\s*,\s*(\d+)", p)
        if m:
            x, y = int(m.group(2)), int(m.group(3))
            x = min(max(x, 0), width - 1)
            y = min(max(y, 0), height - 1)
            steps += [
                {"kind": "mouseMove", "x": x, "y": y, "delayBeforeMs": 100},
                {"kind": "mouseClick", "button": "left", "clicks": 1},
            ]
        if re.search(r"\b(ctrl|control)\s*\+\s*c\b|\bcopy\b", p):
            steps.append({"kind": "key", "keyCode": 0x43, "holdMs": 30, "delayBeforeMs": 50})
        if re.search(r"\bctrl\s*\+\s*v\b|\bpaste\b", p):
            steps.append({"kind": "key", "keyCode": 0x56, "holdMs": 30, "delayBeforeMs": 50})
        if not steps:
            steps.append({"kind": "delay", "delayBeforeMs": 500})

        # NOTE: Ctrl combos need modifier key steps; kept simple here — the WPF
        # editor lets users refine generated macros.
        return json.dumps(
            {
                "schemaVersion": 1,
                "id": mid,
                "name": prompt[:80],
                "description": prompt,
                "steps": steps,
                "repeatCount": 1,
                "repeatDelayMs": 500,
                "maxDurationMs": 120_000,
            }
        )


def make_backend() -> LlmBackend:
    kind = os.environ.get("QWEN_BACKEND", "ollama").lower()
    if kind == "echo":
        return EchoBackend()
    return OllamaBackend()
