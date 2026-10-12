"""Pydantic models mirroring shared/schemas/macro.schema.json and UniMouse.Core.

Keeping the two implementations in sync is a hard requirement; tests in
tests/test_schema_parity.py verify field names against the JSON schema file.
"""

from __future__ import annotations

import re
from datetime import datetime, timezone
from enum import Enum
from typing import Literal

from pydantic import BaseModel, Field, field_validator

ID_RE = re.compile(r"^[A-Za-z0-9._-]+$")

FORBIDDEN_KEY_CODES: frozenset[int] = frozenset({0x5B, 0x5C, 0x14})  # LWIN, RWIN, CAPSLOCK


class StepKind(str, Enum):
    KEY = "key"
    TEXT = "text"
    MOUSE_MOVE = "mouseMove"
    MOUSE_CLICK = "mouseClick"
    MOUSE_SCROLL = "mouseScroll"
    DELAY = "delay"


class MouseButton(str, Enum):
    LEFT = "left"
    RIGHT = "right"
    MIDDLE = "middle"


class ScrollDirection(str, Enum):
    UP = "up"
    DOWN = "down"


class Hotkey(BaseModel):
    keyCode: int = Field(ge=1, le=254)
    modifiers: int = Field(default=0, ge=0, le=15)


class MacroStep(BaseModel):
    kind: StepKind
    delayBeforeMs: int = Field(default=0, ge=0, le=60_000)
    holdMs: int = Field(default=0, ge=0, le=10_000)
    keyCode: int | None = Field(default=None, ge=1, le=254)
    text: str | None = Field(default=None, max_length=10_000)
    x: int | None = None
    y: int | None = None
    button: MouseButton | None = None
    clicks: int = Field(default=1, ge=1, le=5)
    scroll: ScrollDirection | None = None
    scrollAmount: int = Field(default=0, ge=-120, le=120)

    @field_validator("keyCode")
    @classmethod
    def _no_forbidden_keys(cls, v: int | None) -> int | None:
        if v is not None and v in FORBIDDEN_KEY_CODES:
            raise ValueError(f"Virtual key {v:#04x} is forbidden for safety")
        return v

    @field_validator("text")
    @classmethod
    def _no_control_chars(cls, v: str | None) -> str | None:
        if v is None:
            return v
        for ch in v:
            if ord(ch) < 0x20 and ch not in ("\t", "\n"):
                raise ValueError("Text must not contain control characters other than tab/newline")
        return v


class MacroDefinition(BaseModel):
    schemaVersion: Literal[1] = 1
    id: str = Field(max_length=64)
    name: str = Field(min_length=1, max_length=120)
    description: str = Field(default="", max_length=2_000)
    author: str = Field(default="unknown", max_length=64)
    createdAtUtc: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    steps: list[MacroStep] = Field(min_length=1, max_length=2_000)
    repeatCount: int = Field(default=1, ge=-1, le=10_000)
    repeatDelayMs: int = Field(default=500, ge=0, le=60_000)
    maxDurationMs: int = Field(default=120_000, ge=1, le=900_000)
    startHotkey: Hotkey | None = None
    tags: list[str] = Field(default_factory=list, max_length=16)

    @field_validator("id")
    @classmethod
    def _id_pattern(cls, v: str) -> str:
        if not ID_RE.match(v):
            raise ValueError("id must match [A-Za-z0-9._-]+")
        return v


# ---- API request/response envelopes ----------------------------------------


class GenerateRequest(BaseModel):
    """Natural-language intent from the WPF app."""

    prompt: str = Field(min_length=3, max_length=2_000)
    context: str = Field(default="", max_length=4_000)
    screen_width: int = Field(default=1920, ge=320, le=7680)
    screen_height: int = Field(default=1080, ge=240, le=4320)
    macro_id_prefix: str = Field(default="ai", pattern=r"^[a-z0-9._-]{1,20}$")


class GenerateResponse(BaseModel):
    macro: MacroDefinition | None
    explanation: str
    warnings: list[str] = Field(default_factory=list)
    rejected: bool = False
    reject_reason: str | None = None


class ValidateRequest(BaseModel):
    macro: dict  # raw JSON, validated server-side against schema + rules


class ValidateResponse(BaseModel):
    valid: bool
    errors: list[dict] = Field(default_factory=list)
