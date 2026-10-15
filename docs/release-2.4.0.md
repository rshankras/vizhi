# Release 2.4.0 — Vizhi for Claude Code (formerly Claude Console), with iTerm2

Readiness checklist for the first release under the new name. Two PRs feed it: #162 (iTerm2,
`feat/128-iterm2`) and the rename (`feat/rename-vizhi-for-claude-code`, stacked on #162). Vizhi for
Codex ships the iTerm2 driver in the same cycle as 1.7.0.

## What changed for users

- Sessions in **iTerm2** take Session keys and every key reaches them (#128). Ready-made iTerm2
  layouts: `ClaudeConsole-iTerm2.lp5`, `VizhiCodex-iTerm2.lp5`.
- **Claude Console is now Vizhi for Claude Code.** Display name only: the internal name
  `ClaudeConsole`, the namespace, the runtime home `~/.claude/claude-console/`, the hook and helper
  names, the backup suffix, the voice helper bundle id and every layout GUID are unchanged, and
  `ProductIdentityTests` pins each of them. An update keeps layouts, bindings, wiring, the
  microphone grant and settings.

## Verified before cutting

- [x] Full suite green on both branches (`bash tests/run-all.sh`), both products compile.
- [x] iTerm2 device pass on the owner's Mac for both products (PR #162 lists the items). Skipped
      by choice: quit-and-restore, press-while-quit, the window keys (not on any shipped layout).
- [ ] Rename seen live: dev build loaded, log tag reads `[Vizhi for Claude Code]`, Options+ shows
      the new plugin name and the re-imported layout reads "Vizhi for Claude Code — Keypad".

## Cut

1. Merge #162 into main, retarget the rename PR to main, merge it.
2. Versions are already bumped in the rename PR: Claude Console `2.4.0`, Vizhi for Codex `1.7.0`
   (csproj + yaml each, `ProductVersionTests`). Turn `## [Unreleased]` into `## [2.4.0] — <date>`
   and add a `## Vizhi for Codex [1.7.0] — <date>` entry for the iTerm2 driver.
3. **Remove the dev link first**: `Plugins/ClaudeConsolePlugin.link` is live on the packing Mac;
   the service refuses a package while a link exists ("already loaded"). `pack-release.sh` deletes
   it whichever product it packs — and the #73 liveness check then unwires live status (seen
   2026-10-14); turn it back on with a live key afterwards.
4. Pack per SUBMISSION.md: Certum session for the Windows helpers (expires in 1–2 h, log in right
   before), sign + notarize the voice helper, `DOTNET_ROLL_FORWARD=LatestMajor` for
   `logiplugintool`. Both products.
5. **Voice helper (#140, open P1):** the packed helper comes from the packer's runtime home and its
   microphone prompt still says "Claude Console". The rename does not fix that; #140 does. Say so
   in the release notes if #140 is not in this cut.
6. Clean-machine install of each package (Options+ click), then step 5 of the release flow.
7. Tag `v2.4.0` and `vizhi-codex/v1.7.0`, publish the GitHub releases, attach the six layouts.

## After the cut

- **Logitech:** submit both packages. Ask the PM to rename the **listing** to Vizhi for Claude
  Code and say so in the mail — QA's reports all say Claude Console. Listing copy
  (`docs/marketplace-listing.md`) names Apple Terminal, iTerm2 and Windows Terminal now.
- **vizhi.dev:** new product name on `/claude-console/` (URL and anchors stay — they are frozen and
  the DLL links to them), the two iTerm2 layouts on the layouts page, FAQ line for "deleting a
  terminal's last profile drops the keypad to the system profile".
- Close #128 with the release link.
- Re-import `VizhiCodex-Keypad.lp5` on the owner's Mac (its Terminal profile was deleted during
  the pass).

## Known noise, not blocking (file as issues)

- New Tab logs "new session opens in the home folder" although it never uses the folder.
- Vizhi logs Claude Console's live-status state ("Live status: Off") — engine refresh not gated on
  `LiveStatusApplies`.
- `pack-release.sh` removing the dev `.link` (step 3 above).
