"""Parity tests: Pydantic models vs the shared JSON Schema.

Both implementations (C# MacroValidator, Python models.py) must agree with
shared/schemas/macro.schema.json. These tests pin field names, enums and
bounds so a drift in either direction fails CI loudly.
"""

from conftest import minimal_macro, schema, valid_macro_dict  # noqa: F401

import pytest
import jsonschema
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


def test_scroll_amount_bounds_match_between_schema_and_pydantic():
    """The WPF step editor emits scrollAmount in wheel pixels (notches * 120,
    max MacroLimits.MaxScrollNotches = 5 -> 600). Schema and Pydantic must
    accept exactly the same range so a macro saved from the UI always passes
    server-side validation (and vice versa)."""
    s = schema()
    step_def = s["$defs"]["step"]["properties"]["scrollAmount"]
    assert (step_def["minimum"], step_def["maximum"]) == (-600, 600)

    from qwen_service.models import MacroStep

    field = MacroStep.model_fields["scrollAmount"]
    ge = next(c for c in field.metadata if getattr(c, "ge", None) is not None)
    le = next(c for c in field.metadata if getattr(c, "le", None) is not None)
    assert (ge.ge, le.le) == (step_def["minimum"], step_def["maximum"])

    # A 5-notch scroll as produced by the editor must validate on both sides.
    notch_step = {"kind": "mouseScroll", "scroll": "down", "scrollAmount": -600}
    MacroStep.model_validate(notch_step)  # pydantic ok
    jsonschema.validate(notch_step, s["$defs"]["step"])  # schema ok

    # And anything beyond the cap must be rejected by both.
    too_far = {"kind": "mouseScroll", "scroll": "up", "scrollAmount": 720}
    with pytest.raises(ValidationError):
        MacroStep.model_validate(too_far)
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(too_far, s["$defs"]["step"])
