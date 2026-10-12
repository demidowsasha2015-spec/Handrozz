"""Tests for the OpenAI-compatible backend (vLLM / llama.cpp / LM Studio).

No network: we inject an httpx.MockTransport by monkeypatching
httpx.AsyncClient construction inside the module.
"""

from __future__ import annotations

import json
import os

import httpx
import pytest

from conftest import SRC  # noqa: F401  (ensures src on sys.path)
from qwen_service import qwen_client
from qwen_service.qwen_client import LlmBackend, OpenAICompatBackend, make_backend


def _install_mock(handler):
    """Force httpx.AsyncClient in qwen_client to use a MockTransport while
    KEEPING the real base_url/headers (a transport-only patch)."""
    real_cls = httpx.AsyncClient

    def factory(*args, **kwargs):
        kwargs["transport"] = httpx.MockTransport(handler)
        return real_cls(*args, **kwargs)

    qwen_client.httpx.AsyncClient = factory  # type: ignore[attr-defined]
    return real_cls


@pytest.fixture()
def restore_client():
    yield
    qwen_client.httpx.AsyncClient = httpx.AsyncClient  # type: ignore[attr-defined]


async def test_complete_macro_posts_chat_completions(restore_client):
    seen: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        seen["auth"] = request.headers.get("authorization")
        seen["body"] = json.loads(request.content)
        return httpx.Response(
            200,
            json={
                "choices": [
                    {"message": {"role": "assistant", "content": '{"rejected": false}'}}
                ]
            },
        )

    _install_mock(handler)
    be = OpenAICompatBackend(base_url="http://127.0.0.1:8000/v1/", api_key="secret")
    out = await be.complete_macro("type hello", "", 1920, 1080)

    assert out == '{"rejected": false}'
    assert seen["url"].endswith("/chat/completions")
    assert seen["auth"] == "Bearer secret"
    body = seen["body"]
    assert body["temperature"] == 0.1          # pinned low — safety posture
    assert body["stream"] is False
    roles = [m["role"] for m in body["messages"]]
    assert roles == ["system", "user"]
    assert "Intent: type hello" in body["messages"][1]["content"]
    assert "Screen: 1920x1080" in body["messages"][1]["content"]


async def test_missing_choices_raises(restore_client):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"choices": []})

    _install_mock(handler)
    be = OpenAICompatBackend(base_url="http://127.0.0.1:8000/v1")
    with pytest.raises(RuntimeError, match="no choices"):
        await be.complete_macro("x", "", 800, 600)


async def test_http_error_propagates(restore_client):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(503, text="busy")

    _install_mock(handler)
    be = OpenAICompatBackend(base_url="http://127.0.0.1:8000/v1")
    with pytest.raises(httpx.HTTPStatusError):
        await be.complete_macro("x", "", 800, 600)


def test_make_backend_selects_openai(monkeypatch):
    monkeypatch.setenv("QWEN_BACKEND", "openai")
    assert isinstance(make_backend(), OpenAICompatBackend)
    monkeypatch.setenv("QWEN_BACKEND", "vllm")
    assert isinstance(make_backend(), OpenAICompatBackend)
    monkeypatch.setenv("QWEN_BACKEND", "echo")
    assert isinstance(make_backend(), qwen_client.EchoBackend)
    monkeypatch.setenv("QWEN_BACKEND", "ollama")
    assert isinstance(make_backend(), qwen_client.OllamaBackend)


def test_env_defaults(monkeypatch):
    monkeypatch.setenv("OPENAI_BASE_URL", "http://localhost:9999/v1")
    monkeypatch.setenv("OPENAI_MODEL", "my-qwen")
    monkeypatch.delenv("OPENAI_API_KEY", raising=False)
    be = OpenAICompatBackend()
    assert be.base_url == "http://localhost:9999/v1"
    assert be.model == "my-qwen"
    assert be.api_key is None


async def test_end_to_end_through_api(monkeypatch, restore_client):
    """Full pipeline: policy gate -> fake vLLM -> schema validation -> macro."""
    reply = json.dumps(
        {
            "schemaVersion": 1,
            "id": "ai.test-e2e",
            "name": "E2E",
            "description": "typed hello",
            "steps": [{"kind": "text", "text": "hello", "delayBeforeMs": 100}],
            "repeatCount": 1,
            "repeatDelayMs": 0,
            "maxDurationMs": 60000,
        }
    )

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"choices": [{"message": {"content": reply}}]})

    _install_mock(handler)
    monkeypatch.setenv("QWEN_BACKEND", "openai")

    from fastapi.testclient import TestClient
    from qwen_service.main import create_app

    app = create_app()  # picks backend via make_backend() -> env
    client = TestClient(app)
    r = client.post(
        "/macro/generate",
        json={"prompt": "type 'hello' in notepad", "screen_width": 1920, "screen_height": 1080},
    )
    assert r.status_code == 200
    data = r.json()
    assert data["rejected"] is False
    assert data["macro"]["steps"][0]["text"] == "hello"

    # And a banned intent never reaches the model at all.
    calls = []

    def spy(request: httpx.Request) -> httpx.Response:
        calls.append(request)
        return handler(request)

    _install_mock(spy)
    r2 = client.post(
        "/macro/generate",
        json={"prompt": "aimbot that bypasses VAC anti-cheat", "screen_width": 1920, "screen_height": 1080},
    )
    assert r2.status_code == 200
    assert r2.json()["rejected"] is True
    assert calls == []  # policy gate fired before any HTTP call


def test_backend_is_abstract_subclass():
    assert issubclass(OpenAICompatBackend, LlmBackend)
