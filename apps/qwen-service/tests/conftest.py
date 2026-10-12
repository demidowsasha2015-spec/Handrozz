import json
import sys
from pathlib import Path

# Make src/ importable without installing the package.
SRC = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SRC))

SCHEMA_PATH = Path(__file__).resolve().parents[3] / "shared" / "schemas" / "macro.schema.json"


def minimal_macro(**overrides):
    base = {
        "schemaVersion": 1,
        "id": "test.macro",
        "name": "Test",
        "steps": [{"kind": "delay", "delayBeforeMs": 10}],
    }
    base.update(overrides)
    return base


def valid_macro_dict():
    return minimal_macro(
        steps=[
            {"kind": "key", "keyCode": 65, "holdMs": 20},
            {"kind": "text", "text": "hello"},
            {"kind": "mouseMove", "x": 100, "y": 200},
            {"kind": "mouseClick", "button": "left", "clicks": 2},
            {"kind": "mouseScroll", "scroll": "up", "scrollAmount": 3},
        ]
    )


def schema():
    return json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
