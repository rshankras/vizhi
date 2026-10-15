#!/usr/bin/env python3
"""The one table of layout file names, display names and GUIDs (2.4.0).

A layout file is named <Product>-<Host>.lp5 and its profile reads "<Product> — <Host>", so the
download page and the Options+ profile list say the same two things: which product, which
terminal. The GUID is the profile's identity — Options+ dedupes imports by it — so a renamed or
regenerated file keeps its GUID, and importing it REPLACES the earlier profile instead of adding
a second one. (Before 2.4.0 the files were ClaudeConsole-Keypad / -Windows / -iTerm2 and
VizhiCodex-Keypad / -Windows / -iTerm2, with the same GUIDs.)

Every tool that writes a layout takes its names from here: sync-default-profiles.py (the Claude
Terminal and Windows Terminal files), make-codex-profile.py (the Codex ones, derived from them),
make-iterm2-profiles.py (the iTerm2 ones), name-profiles.py (display names across all six).
tests/ProductIdentityTests.cs pins the same GUIDs and names from the C# side.
"""

import os

# File prefix → the plugin whose actions the layout binds ("$<plugin>___…" in every key). The
# Claude product's plugin keeps its internal name, ClaudeConsole — an install contract, since it
# sits inside every key binding of every imported layout — so only the DOWNLOAD carries the
# product's name.
PLUGIN = {
    "VizhiClaudeCode": "ClaudeConsole",
    "VizhiCodex": "VizhiCodex",
}

PRODUCT_DISPLAY = {
    "VizhiClaudeCode": "Vizhi for Claude Code",
    "VizhiCodex": "Vizhi for Codex",
}

HOST_DISPLAY = {
    "Terminal": "Terminal",
    "iTerm2": "iTerm2",
    "WindowsTerminal": "Windows Terminal",
}

# File name → stable GUID. Never mint a new one for an existing layout.
GUIDS = {
    "VizhiClaudeCode-Terminal.lp5": "4146981DFFCD4C4EBCCE554873013DE8",
    "VizhiClaudeCode-iTerm2.lp5": "01F94DE125BF41D9BF030BD7B7555357",
    "VizhiClaudeCode-WindowsTerminal.lp5": "9556D49413DC49B1968CA44D3C3C3303",
    "VizhiCodex-Terminal.lp5": "7B2C4E9A15D8436FA0C3E17D5B84962F",
    "VizhiCodex-iTerm2.lp5": "3C9F9D52FBDF49D396AA1F2D4008694C",
    "VizhiCodex-WindowsTerminal.lp5": "90CC0B6D7FE0405AB63B311DCA7476B7",
}

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def split(file_name: str) -> tuple[str, str]:
    """'VizhiClaudeCode-iTerm2.lp5' → ('VizhiClaudeCode', 'iTerm2')."""
    stem = file_name[:-4] if file_name.endswith(".lp5") else file_name
    product, _, host = stem.partition("-")
    if product not in PLUGIN or host not in HOST_DISPLAY:
        raise KeyError(f"not a layout file name: {file_name!r}")
    return product, host


def plugin(file_name: str) -> str:
    return PLUGIN[split(file_name)[0]]


def display_name(file_name: str) -> str:
    product, host = split(file_name)
    return f"{PRODUCT_DISPLAY[product]} — {HOST_DISPLAY[host]}"


def guid(file_name: str) -> str:
    return GUIDS[file_name]


def path(file_name: str) -> str:
    return os.path.join(ROOT, "profiles", file_name)


def package_yaml(file_name: str) -> bytes:
    """The layout's inner metadata/LoupedeckPackage.yaml, identity and name from the table."""
    return (
        f"type: Profile5\nname: {guid(file_name)}\ndisplayName: {display_name(file_name)}\nversion: 2.0.0.0\n"
    ).encode("utf-8")
