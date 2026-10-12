"""Parity tests: Pydantic models vs the shared JSON Schema.

Both implementations (C# MacroValidator, Python models.py) must agree with
shared/schemas/macro.schema.json. These tests pin field names, enums and
bounds so a drift in either direction fails CI loudly.
"""

from conftest import minimal_macro, schema, valid_macro_dict  # noqa: F401

import pytest
from pydantic import ValidationError

from qwen_service.models import MacroDefinition


def test_field_parity_with_schema():
    s = schema()
    schema_fields = set(s["properties"].keys())
    model_fields = set(MacroDefinition.model_fields.keys())
    assert schema_fields == model_fields, (
        f"schema-only={schema_fields - model_fields}, model-only={model_fields - schema_fields}"
    )


def test_step_field_parity_with_schema():
    from qwen_service.models import MacroStep

    step_def = schema()["$defs"]["step"]
    schema_fields = set(step_def["properties"].keys())
    model_fields = set(MacroStep.model_fields.keys())
    assert schema_fields == model_fields


def test_kind_enum_parity():
    kinds_schema = set(schema()["$defs"]["step"]["properties"]["kind"]["enum"])
    from qwen_service.models import StepKind

    kinds_model = {k.value for k in StepKind}
    assert kinds_schema == kinds_model


def test_valid_macro_roundtrips_through_pydantic():
    m = MacroDefinition.model_validate(valid_macro_dict())
    dumped = m.model_dump(mode="json")
    assert dumped["id"] == "test.macro"
    assert len(dumped["steps"]) == 5


@pytest.mark.parametrize(
    "bad",
    [
        {"id": "has space"},          # pattern violation
        {"name": ""},                 # min length
        {"schemaVersion": 2},         # unsupported version
        {"steps": []},                # empty steps
        {"maxDurationMs": 900_001},   # over hard cap
        {"repeatCount": -2},          # below -1
    ],
)
def test_pydantic_rejects_schema_violations(bad):
    with pytest.raises(ValidationError):
        MacroDefinition.model_validate(minimal_macro(**bad))


def test_forbidden_key_codes_match_schema_policy():
    # schema allows 1..254 generally; the *policy* forbids Win/CapsLock via Pydantic
    from qwen_service.models import FORBIDDEN_KEY_CODES, MacroStep

    assert FORBIDDEN_KEY_CODES == frozenset({0x5B, 0x5C, 0x14})
    for code in FORBIDDEN_KEY_CODES:
        with pytest.raises(ValidationError):
            MacroStep.model_validate({"kind": "key", "keyCode": code})


def test_control_character_policy():
    from qwen_service.models import MacroStep

    MacroStep.model_validate({"kind": "text", "text": "ok\tvalue\n"})  # tab/newline allowed
    with pytest.raises(ValidationError):
        MacroStep.model_validate({"kind": "text", "text": "bad\x00char"})
