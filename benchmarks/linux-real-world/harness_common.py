#!/usr/bin/env python3
"""Shared deterministic helpers for the Linux real-world benchmark harness."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any


def strip_json_comments(text: str) -> str:
    """Replace JavaScript-style comments with whitespace while preserving strings and line numbers."""
    output: list[str] = []
    index = 0
    in_string = False
    escaped = False
    while index < len(text):
        char = text[index]
        next_char = text[index + 1] if index + 1 < len(text) else ""
        if in_string:
            output.append(char)
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            index += 1
            continue
        if char == '"':
            in_string = True
            output.append(char)
            index += 1
            continue
        if char == "/" and next_char == "/":
            output.extend((" ", " "))
            index += 2
            while index < len(text) and text[index] not in "\r\n":
                output.append(" ")
                index += 1
            continue
        if char == "/" and next_char == "*":
            output.extend((" ", " "))
            index += 2
            while index < len(text):
                if index + 1 < len(text) and text[index] == "*" and text[index + 1] == "/":
                    output.extend((" ", " "))
                    index += 2
                    break
                output.append(text[index] if text[index] in "\r\n" else " ")
                index += 1
            else:
                raise ValueError("unterminated JSON block comment")
            continue
        output.append(char)
        index += 1
    if in_string:
        raise ValueError("unterminated JSON string")
    return "".join(output)


def load_jsonc(path: Path) -> dict[str, Any]:
    value = json.loads(strip_json_comments(path.read_text(encoding="utf-8")))
    if not isinstance(value, dict):
        raise ValueError(f"expected JSON object: {path}")
    return value
