"""Safety policy for AI-generated macros.

Two layers:
 1. Content policy — refuse to translate intents that describe game cheating,
    anti-cheat evasion, stealth input, memory reading, injection, etc.
 2. Structural validation — the generated JSON must pass both the JSON schema
    and Pydantic model checks before it is ever returned to the client.

The service NEVER executes anything; it only produces validated JSON that the
desktop app (which owns the actual SendInput layer) may choose to run.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

from jsonschema import Draft202012Validator

from .models import MacroDefinition

SCHEMA_PATH = (
    Path(__file__).resolve().parents[4] / "shared" / "schemas" / "macro.schema.json"
)

# Lowercase keyword phrases whose presence in a prompt marks it as out of policy.
BLOCKED_PATTERNS: tuple[str, ...] = (
    # cheating / combat automation
    r"\baim\s*bot\b",
    r"\baimbot\b",
    r"\btrigger\s*bot\b",
    r"\btriggerbot\b",
    r"\bwall\s*hack\b",
    r"\bwallhack\b",
    r"\bauto\s*-?\s*aim\b",
    r"\bauto-?aiming\b",
    r"\bspin-?bot\b",
    r"\bradar\s*hack\b",
    r"\bno-?recoil\b",
    r"\brecoil\s*(script|control)\b",
    r"\bsilent\s*aim\b",
    r"\bmagic\s*bullet\b",
    r"\bfly\s*hack\b",
    r"\bteleport\s*hack\b",
    # anti-cheat / security evasion
    r"\banti[\s-]?cheat\b",
    r"\beac\b(?!\w)",
    r"\bbattleye\b",
    r"\bvalojant\b|\bvalve\s*anti-?cheat\b",
    r"\beasy\s*anti[\s-]?cheat\b",
    r"\bfaceit\s*ac\b",
    r"\bvanguard\b",
    r"\briot\s*vanguard\b",
    r"\bdps\s*monitor\b",
    r"\bbypass.{0,20}(detect|anti|kernel|security|defender)",
    r"\bevade.{0,20}(detect|anti|kernel|ban)",
    r"\bavoid\s*banned?\b",
    r"\bunbann?able\b",
    r"\bhwid\s*spoof\b",
    r"\bspoof(ing)?\b",
    r"\bmac\s*address\s*spoof\b",
    # kernel / injection / memory
    r"\bkernel\s*driver\b",
    r"\bdriver\s*inject",
    r"\bdll\s*inject",
    r"\bprocess\s*hollow",
    r"\bmemory\s*(read|write|hacking|patch)",
    r"\bread\s*(game\s*)?memory\b",
    r"\bgame\s*memory\b",
    r"\bpointer\s*scan",
    r"\bcheat\s*engine\b",
    r"\bhook(s|ing)?\s*(directx|vulkan|opengl|input|device)",
    r"\braw\s*input\s*hook\b",
    r"\binterception\s*driver\b",
    r"\bvirtual\s*(hid|keyboard|mouse)\s*driver\b",
    r"\barduino\s*(leonardo|pro\s*micro)?.{0,30}(cheat|aim|bypass|undetected)",
    r"\bteensy.{0,30}(cheat|aim|bypass|undetected)",
    r"\bundetected?\b",
    r"\bnot\s*detect(ed|able)\b",
    r"\binvisible\s*to\s*(anti|detect)",
)

_COMPILED = [re.compile(p, re.IGNORECASE) for p in BLOCKED_PATTERNS]


def policy_check(prompt: str, extra_context: str = "") -> tuple[bool, str | None]:
    """Return (allowed, reason). Allowed means 'may be sent to the model'."""
    haystack = f"{prompt}\n{extra_context}"
    for rx in _COMPILED:
        if rx.search(haystack):
            return False, (
                "This request matches our safety policy against game cheats, "
                "anti-cheat evasion, input spoofing, injection or memory access. "
                "UniMouse only supports transparent productivity automation."
            )
    return True, None


_schema_cache: dict | None = None


def load_schema() -> dict:
    global _schema_cache
    if _schema_cache is None:
        _schema_cache = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    return _schema_cache


def structural_errors(macro_json: dict) -> list[dict]:
    """Validate raw dict against the shared JSON Schema. Returns error list."""
    validator = Draft202012Validator(load_schema())
    return [
        {
            "path": "/".join(str(p) for p in e.absolute_path) or "<root>",
            "message": e.message,
        }
        for e in validator.iter_errors(macro_json)
    ]


def full_validate(macro_json: dict) -> tuple[bool, list[dict]]:
    """Schema + Pydantic (which carries the forbidden-key rules)."""
    errors = structural_errors(macro_json)
    if errors:
        return False, errors
    try:
        MacroDefinition.model_validate(macro_json)
    except Exception as exc:  # pydantic ValidationError
        return False, [{"path": "<pydantic>", "message": str(exc)}]
    return True, []
