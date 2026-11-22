#!/usr/bin/env python3
"""Static check of withheldText in src/Configuration/configPage.html.

The server sends Withheld as axis -> level -> reason. The dashboard line must print the axis, the
level and the server's own reason for each, and must not carry a reason of its own (the ONNX
levels are withheld for a different reason than the NVIDIA-only ones).
"""
import pathlib
import re
import sys

PAGE = pathlib.Path(__file__).resolve().parent.parent / "src" / "Configuration" / "configPage.html"


def extract(source, name):
    start = source.find("function " + name + "(")
    if start < 0:
        return None
    brace = source.index("{", start)
    depth = 0
    for i in range(brace, len(source)):
        if source[i] == "{":
            depth += 1
        elif source[i] == "}":
            depth -= 1
            if depth == 0:
                return source[start:i + 1]
    return None


def main():
    body = extract(PAGE.read_text(encoding="utf-8"), "withheldText")
    failures = []
    if body is None:
        failures.append("withheldText not found in configPage.html")
    else:
        code = re.sub(r"//[^\n]*", "", body)
        if re.search(r"needs\s+NVIDIA", code, re.IGNORECASE):
            failures.append("withheldText hard-codes a 'needs NVIDIA' reason")
        if not re.search(r"w\[axis\]\[level\]|byLevel\[level\]|\[level\]", code):
            failures.append("withheldText does not read the reason from the server's axis -> level map")
        if not re.search(r"\baxis\b[^;]*\+|\+[^;]*\baxis\b", code):
            failures.append("withheldText does not print the axis")
        if "' ('" not in code and '" ("' not in code:
            failures.append("withheldText does not render 'Axis level (reason)'")

    for f in failures:
        print("FAIL:", f)
    if failures:
        return 1
    print("ok: withheldText prints axis, level and the server's reason")
    return 0


if __name__ == "__main__":
    sys.exit(main())
