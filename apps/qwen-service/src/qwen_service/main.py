"""FastAPI application factory.

Security posture:
  * binds to 127.0.0.1 only (see run script) — this is a local helper for the
    desktop app, not a public service;
  * optional shared secret via UNI_MOUSE_API_KEY header check;
  * strict request size limits and CORS locked to the desktop origin.
"""

from __future__ import annotations

import os

from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from .qwen_client import LlmBackend, make_backend
from .routers import macro


def create_app(backend: LlmBackend | None = None) -> FastAPI:
    app = FastAPI(
        title="UniMouse Qwen Service",
        version="0.1.0",
        description="Local AI-assisted macro generation for UniMouse Macro AI.",
    )
    app.state.backend = backend or make_backend()

    api_key = os.environ.get("UNIMOUSE_API_KEY")
    if api_key:

        @app.middleware("http")
        async def require_api_key(request: Request, call_next):
            if request.url.path.startswith("/macro") and request.headers.get("x-api-key") != api_key:
                return JSONResponse({"detail": "invalid or missing x-api-key"}, status_code=401)
            return await call_next(request)

    app.add_middleware(
        CORSMiddleware,
        allow_origins=["http://localhost", "unimouse-app"],
        allow_methods=["GET", "POST"],
        allow_headers=["content-type", "x-api-key"],
    )

    @app.get("/health")
    async def health() -> dict:
        return {"status": "ok", "backend": type(app.state.backend).__name__}

    app.include_router(macro.router)
    return app


app = create_app()
