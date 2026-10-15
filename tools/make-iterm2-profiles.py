#!/usr/bin/env python3
"""Derive the iTerm2 keypad profiles from the Terminal ones (#128).

An Options+ profile is bound to one application entry. The Terminal profiles in profiles/ bind to
Options+'s own Terminal entry (com.apple.terminal); iTerm2 users need the same layout bound to
iTerm2's entry (com.googlecode.iterm2). Nothing else differs — the plugin's actions find the
session by TTY whichever terminal owns it — so this script copies each Terminal profile byte for
byte except for the places that carry the binding or the identity:

  1. ProfileInfo.json: applicationName (the host entry), name / packageName (the GUID — the
     service dedupes by it, so a shared GUID would collapse the two profiles into one), displayName.
  2. ApplicationInfo.json: name / displayName / processOrBundleName (iTerm2's bundle id) and
     defaultProfileName (the GUID).
  3. metadata/LoupedeckPackage.yaml: name (the GUID) and displayName.

The GUIDs are stable so importing an updated download refreshes the same profile rather than
leaving a duplicate in Options+.

Run from the repo root AFTER sync-default-profiles.py and make-codex-profile.py, since the iTerm2
files are derived from their output:  python3 tools/make-iterm2-profiles.py
"""

import json
import os
import zipfile

ITERM2 = {
    "name": "com.googlecode.iterm2",
    "displayName": "iTerm2",
    "processOrBundleName": "com.googlecode.iterm2",   # iTerm.app's CFBundleIdentifier, checked 2026-10-14 (3.7.4)
}

# (source Terminal profile, iTerm2 profile, display name, stable GUID)
PROFILES = (
    (
        "profiles/ClaudeConsole-Keypad.lp5",
        "profiles/ClaudeConsole-iTerm2.lp5",
        "Claude Console — Keypad (iTerm2)",
        "01F94DE125BF41D9BF030BD7B7555357",
    ),
    (
        "profiles/VizhiCodex-Keypad.lp5",
        "profiles/VizhiCodex-iTerm2.lp5",
        "Vizhi for Codex — Keypad (iTerm2)",
        "3C9F9D52FBDF49D396AA1F2D4008694C",
    ),
)


def build_profile(src: str, dst: str, display: str, guid: str) -> None:
    with zipfile.ZipFile(src) as zin:
        items = zin.infolist()
        files = {item.filename: zin.read(item.filename) for item in items}

    profile = json.loads(files["ProfileInfo.json"])
    if profile.get("applicationName") != "com.apple.terminal":
        raise SystemExit(f"{src}: expected a Terminal profile, found {profile.get('applicationName')!r}")
    profile["applicationName"] = ITERM2["name"]
    profile["name"] = guid
    if profile.get("packageName"):
        profile["packageName"] = guid
    profile["displayName"] = display
    files["ProfileInfo.json"] = json.dumps(profile, indent=2).encode("utf-8")

    application = json.loads(files["ApplicationInfo.json"])
    application.update(ITERM2)
    application["defaultProfileName"] = guid
    files["ApplicationInfo.json"] = json.dumps(application, indent=2).encode("utf-8")

    files["metadata/LoupedeckPackage.yaml"] = (
        f"type: Profile5\nname: {guid}\ndisplayName: {display}\nversion: 2.0.0.0\n"
    ).encode("utf-8")

    with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in items:
            zout.writestr(item, files[item.filename])
    print(f"wrote {dst}  ({display}, {guid})")


def main() -> None:
    for src, dst, display, guid in PROFILES:
        build_profile(src, dst, display, guid)


if __name__ == "__main__":
    os.chdir(os.path.join(os.path.dirname(__file__), ".."))
    main()
