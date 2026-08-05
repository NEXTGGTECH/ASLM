# Copyright NEXTGGTECH. Apache License 2.0.

#!/usr/bin/env python3
# Stamp the release version into every app's csproj.
# Usage: stamp-version.py <display G.S.Z> <appversion W> <full G.S.Z.W>
import re
import sys

# MAUI apps: ApplicationDisplayVersion (G.S.Z) + ApplicationVersion (W).
MAUI_PROJECTS = [
    "ASLM/ASLM.csproj",
    "ASLM/Patcher/Patcher/ASLM.Patcher.csproj",
    "ASLM/Installer/Installer/ASLM.Installer.csproj",
]
# Non-MAUI console/WinExe apps: standard assembly version fields (full G.S.Z.W).
NONMAUI_PROJECTS = [
    "ASLM/Launcher/ASLM.Launcher.csproj",
    "ASLM/Installer/Installer-Bootstrapper/ASLM.Installer.Bootstrapper.csproj",
    "ASLM/Patcher/Patcher-CLI/ASLM.Patcher.CLI.csproj",
]


def set_elem(text, name, value):
    pat = re.compile(rf"(<{name}>).*?(</{name}>)", re.S)
    if pat.search(text):
        return pat.sub(rf"\g<1>{value}\g<2>", text, count=1)
    # Insert into the first PropertyGroup when the element is absent.
    return re.sub(r"(\n\t*</PropertyGroup>)", rf"\n\t\t<{name}>{value}</{name}>\1", text, count=1)


def stamp(path, elems):
    with open(path, "r", encoding="utf-8") as f:
        original = f.read()
    text = original
    for name, value in elems:
        text = set_elem(text, name, value)
    if text != original:
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        print(f"stamped {path}")
    else:
        print(f"unchanged {path}")


def main():
    if len(sys.argv) != 4:
        print("usage: stamp-version.py <display> <appversion> <full>", file=sys.stderr)
        return 2
    display, appversion, full = sys.argv[1], sys.argv[2], sys.argv[3]
    for p in MAUI_PROJECTS:
        stamp(p, [("ApplicationDisplayVersion", display), ("ApplicationVersion", appversion)])
    for p in NONMAUI_PROJECTS:
        stamp(p, [("Version", full), ("FileVersion", full), ("InformationalVersion", full)])
    return 0


if __name__ == "__main__":
    sys.exit(main())
