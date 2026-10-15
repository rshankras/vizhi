#!/usr/bin/env python3
"""Stamp every layout's display name from tools/profile_names.py, touching nothing else.

Rewrites ProfileInfo.json's displayName and the inner metadata/LoupedeckPackage.yaml; every
other member — the key bindings, the preview artwork, the application binding — is copied byte
for byte. Refuses a file whose GUID is not the one the table pins: that would mean the file is
not the layout its name claims, and stamping a name on it would hide the mismatch.

Run from the repo root after any generator: python3 tools/name-profiles.py
"""

import json
import os
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import profile_names as names  # noqa: E402


def stamp(file_name: str) -> None:
    src = names.path(file_name)
    with zipfile.ZipFile(src) as zin:
        items = zin.infolist()
        files = {item.filename: zin.read(item.filename) for item in items}

    profile = json.loads(files["ProfileInfo.json"])
    if profile.get("name") != names.guid(file_name):
        raise SystemExit(
            f"{file_name}: GUID {profile.get('name')!r} is not the pinned {names.guid(file_name)} — "
            "this is not the layout its name claims; fix the file or the table, not the name."
        )

    display = names.display_name(file_name)
    before = profile.get("displayName")
    profile["displayName"] = display
    files["ProfileInfo.json"] = json.dumps(profile, indent=2).encode("utf-8")
    files["metadata/LoupedeckPackage.yaml"] = names.package_yaml(file_name)

    temporary = src + ".tmp"
    with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in items:
            zout.writestr(item, files[item.filename])
    os.replace(temporary, src)
    print(f"{file_name}: {before!r} -> {display!r}" if before != display else f"{file_name}: {display!r} (unchanged)")


def main() -> None:
    for file_name in names.GUIDS:
        stamp(file_name)


if __name__ == "__main__":
    main()
