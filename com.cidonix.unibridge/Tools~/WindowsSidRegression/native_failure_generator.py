"""Instrument the complete production listener at native/allocation boundaries.

The SID and pipe-security method bodies remain production source. The generated
variant never calls Windows APIs, so failures can be deterministic and resources
can be counted without adding test hooks to shipped code.
"""

from __future__ import annotations

import re


_NATIVE_METHODS = {
    "CreateNamedPipe",
    "ConvertStringSecurityDescriptorToSecurityDescriptor",
    "LocalFree",
    "GetCurrentProcess",
    "OpenProcessToken",
    "GetTokenInformation",
    "CloseHandle",
    "ConvertSidToStringSid",
}

_DECLARATION = re.compile(
    r"\[DllImport\([\s\S]*?\)\]\s*"
    r"(?P<signature>static\s+extern\s+(?P<result>\w+)\s+"
    r"(?P<name>\w+)\((?P<arguments>[\s\S]*?)\));"
)


def generate(source: str) -> str:
    """Return a complete listener wired to the typed NativeFailures fixture."""
    replaced: set[str] = set()

    def replace(match: re.Match[str]) -> str:
        name = match.group("name")
        if name not in _NATIVE_METHODS:
            raise ValueError(f"Unexpected production native method: {name}")
        if name in replaced:
            raise ValueError(f"Duplicate production native method: {name}")
        replaced.add(name)
        call_arguments = []
        for argument in match.group("arguments").split(","):
            words = argument.strip().split()
            if not words:
                continue
            prefix = words[0] + " " if words[0] in {"out", "ref", "in"} else ""
            call_arguments.append(prefix + words[-1])
        signature = re.sub(r"\bextern\s+", "", match.group("signature"))
        return (
            signature
            + "\n        {\n            return global::NativeFailures."
            + name
            + "("
            + ", ".join(call_arguments)
            + ");\n        }"
        )

    result = _DECLARATION.sub(replace, source)
    if replaced != _NATIVE_METHODS:
        raise ValueError("Missing production native methods: " + ", ".join(sorted(_NATIVE_METHODS - replaced)))

    for method in ("AllocHGlobal", "FreeHGlobal"):
        original = "Marshal." + method + "("
        if original not in result:
            raise ValueError("Missing production allocation boundary: " + original)
        result = result.replace(original, "global::NativeFailures." + method + "(")

    return result
