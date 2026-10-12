"""HTTP API: /health, /macro/generate, /macro/validate."""

from __future__ import annotations

import logging

from fastapi import APIRouter, HTTPException, Request

from ..models import (
    GenerateRequest,
    GenerateResponse,
    MacroDefinition,
    ValidateRequest,
    ValidateResponse,
)
from ..qwen_client import LlmBackend, _extract_json
from ..validation import full_validate, policy_check

log = logging.getLogger("qwen_service")

router = APIRouter(prefix="/macro", tags=["macro"])


def _backend(request: Request) -> LlmBackend:
    return request.app.state.backend


@router.post("/generate", response_model=GenerateResponse)
async def generate(req: GenerateRequest, request: Request) -> GenerateResponse:
    # 1. Content policy BEFORE touching the model.
    allowed, reason = policy_check(req.prompt, req.context)
    if not allowed:
        assert reason is not None
        return GenerateResponse(
            macro=None,
            explanation="Request refused by safety policy.",
            rejected=True,
            reject_reason=reason,
        )

    # 2. Model inference.
    backend = _backend(request)
    try:
        raw = await backend.complete_macro(req.prompt, req.context, req.screen_width, req.screen_height)
    except Exception as exc:
        log.exception("model call failed")
        raise HTTPException(status_code=502, detail=f"Model backend error: {exc}") from exc

    # 3. Parse + validate output — never trust the model.
    try:
        data = _extract_json(raw)
    except ValueError as exc:
        return GenerateResponse(
            macro=None,
            explanation=f"Model returned unparseable output ({exc}). Try rephrasing the intent.",
            warnings=["model_output_unparseable"],
        )

    if data.get("rejected") is True:
        return GenerateResponse(
            macro=None,
            explanation="The model refused this intent on safety grounds.",
            rejected=True,
            reject_reason=str(data.get("reason", "unspecified")),
        )

    ok, errors = full_validate(data)
    if not ok:
        return GenerateResponse(
            macro=None,
            explanation="Generated macro failed validation and was NOT released to the client.",
            warnings=[f"{e['path']}: {e['message']}" for e in errors[:10]],
        )

    macro = MacroDefinition.model_validate(data)
    return GenerateResponse(
        macro=macro,
        explanation=(
            f"Generated {len(macro.steps)} step(s). Review them in the UniMouse editor "
            "before running; adjust coordinates for your screen layout."
        ),
    )


@router.post("/validate", response_model=ValidateResponse)
async def validate(req: ValidateRequest) -> ValidateResponse:
    ok, errors = full_validate(req.macro)
    return ValidateResponse(valid=ok, errors=errors)
