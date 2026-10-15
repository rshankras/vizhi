#!/usr/bin/env python3
"""Derive the iTerm2 keypad layouts from the Terminal ones (#128).

An Options+ profile is bound to one application entry. The Terminal layouts in profiles/ bind to
Options+'s own Terminal entry (com.apple.terminal); iTerm2 users need the same layout bound to
iTerm2's entry (com.googlecode.iterm2). Nothing else differs — the plugin's actions find the
session by TTY whichever terminal owns it — so this script copies each Terminal layout byte for
byte except for the places that carry the binding or the identity:

  1. ProfileInfo.json: applicationName (the host entry), name / packageName (the GUID — the
     service dedupes by it, so a shared GUID would collapse the two profiles into one), displayName.
  2. ApplicationInfo.json: name / displayName / processOrBundleName (iTerm2's bundle id) and
     defaultProfileName (the GUID).
  3. metadata/LoupedeckPackage.yaml: name (the GUID) and displayName.

Names and GUIDs come from tools/profile_names.py, so a re-import refreshes the same profile rather
than leaving a duplicate in Options+.

Run from the repo root AFTER sync-default-profiles.py and make-codex-profile.py, since the iTerm2
files are derived from their output:  python3 tools/make-iterm2-profiles.py
"""

import json
import os
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import profile_names as names  # noqa: E402

ITERM2 = {
    "name": "com.googlecode.iterm2",
    "displayName": "iTerm2",
    "processOrBundleName": "com.googlecode.iterm2",   # iTerm.app's CFBundleIdentifier, checked 2026-10-14 (3.7.4)
}

# (source Terminal layout, iTerm2 layout)
PROFILES = (
    ("VizhiClaudeCode-Terminal.lp5", "VizhiClaudeCode-iTerm2.lp5"),
    ("VizhiCodex-Terminal.lp5", "VizhiCodex-iTerm2.lp5"),
)


def build_profile(src_name: str, dst_name: str) -> None:
    guid = names.guid(dst_name)
    display = names.display_name(dst_name)
    with zipfile.ZipFile(names.path(src_name)) as zin:
        items = zin.infolist()
        files = {item.filename: zin.read(item.filename) for item in items}

    profile = json.loads(files["ProfileInfo.json"])
    if profile.get("applicationName") != "com.apple.terminal":
        raise SystemExit(f"{src_name}: expected a Terminal layout, found {profile.get('applicationName')!r}")
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

    files["metadata/LoupedeckPackage.yaml"] = names.package_yaml(dst_name)

    with zipfile.ZipFile(names.path(dst_name), "w", zipfile.ZIP_DEFLATED) as zout:
        for item in items:
            zout.writestr(item, files[item.filename])
    print(f"wrote profiles/{dst_name}  ({display}, {guid})")


def main() -> None:
    for src, dst in PROFILES:
        build_profile(src, dst)


if __name__ == "__main__":
    main()
