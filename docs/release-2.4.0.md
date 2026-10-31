# Release 2.4.0 — Vizhi for Claude Code (formerly Claude Console), with iTerm2

Readiness checklist for the first release under the new name. Two PRs feed it: #162 (iTerm2,
`feat/128-iterm2`) and the rename (`feat/rename-vizhi-for-claude-code`, stacked on #162). Vizhi for
Codex ships the iTerm2 driver in the same cycle as 1.7.0.

## What changed for users

- Sessions in **iTerm2** take Session keys and every key reaches them (#128). Ready-made iTerm2
  layouts: `VizhiClaudeCode-iTerm2.lp5`, `VizhiCodex-iTerm2.lp5`.
- **Claude Console is now Vizhi for Claude Code.** Display name only: the internal name
  `ClaudeConsole`, the namespace, the runtime home `~/.claude/claude-console/`, the hook and helper
  names, the backup suffix, the voice helper bundle id and every layout GUID are unchanged, and
  `ProductIdentityTests` pins each of them. An update keeps layouts, bindings, wiring, the
  microphone grant and settings.

## Verified before cutting

- [x] Full suite green on both branches (`bash tests/run-all.sh`), both products compile.
- [x] iTerm2 device pass on the owner's Mac for both products (PR #162 lists the items). Skipped
      by choice: quit-and-restore, press-while-quit, the window keys (not on any shipped layout).
- [x] Rename seen live: the 2.4.0 package installed on the owner's Mac 2026-10-28, log tag `[Vizhi for Claude
      Code]`, voice helper installed from the package with the new name; dictation and session keys used
      2026-10-29 (the #165 / #167 reproductions ran on it).
- [x] **Repack needed:** #165 (session bar follows the tab, pin frame) and #166 (docs) merged on
      2026-10-29 after the first pack. The CHANGELOG now dates 2.4.0 / 1.7.0 as 2026-10-29 and includes
      them. #167 (silent recordings, failure faces; #169) and #170 are 2.4.1.

## Device pass on the repacked builds (owner's Mac, 2026-10-29)

Packages from main `bf2edb9` (#171), installed through Options+ at 07:35 (Claude Console, upgrade)
and 07:39 (Vizhi for Codex, fresh install — a fresh install needs the owner's click in the Options+
prompt; an upgrade installs on `open`):

- **Vizhi for Claude Code 2.4.0** — `ClaudeConsole_2.4.0.lplug4`
  `5de73cb7b92b6afb787fab67dff8135124f9bb227ba67c0276d3f3e2c7fffc32`: installed DLL matches the
  package; Dictate, Yes and No into a Terminal session; live status survived the upgrade.
- **Vizhi for Codex 1.7.0** — `VizhiCodex_1.7.0.lplug4`
  `f3149199928c83c96c043c8ef2607e143b0ad678b8eddefd8f852e4ecb8ce781`: Codex sessions in iTerm2
  (AlertWala, ChantFlow) discovered and keyed; hooks trusted, two approvals answered by key; with
  the pin released the blue bar followed a tab switch on its own, and the pinned key showed its
  frame (#165 on the shipped build).
- Layout import of the renamed files; Options+ keeps every import as its own profile (#172), and
  each terminal entry shows one layout at a time — picked per app in Options+ (FAQ line on the
  site branch).
- Draft GitHub releases `v2.4.0` and `vizhi-codex/v1.7.0` exist with the packages, the six layouts
  and checksums; tags are created on publish.

Windows smoke on the laptop done 2026-10-29 (owner), both packages. Not run: the clean-machine Mac pass.

## Cut

1. Merge #162 into main, retarget the rename PR to main, merge it.
2. Versions bumped (Claude Console `2.4.0`, Vizhi for Codex `1.7.0`) and the CHANGELOG cut:
   `## [2.4.0] — 2026-10-29` and `## Vizhi for Codex [1.7.0] — 2026-10-29` (release/2.4.0-cut).
3. ~~Remove the dev link first~~ (done 2026-10-15; no link since): `Plugins/ClaudeConsolePlugin.link` is live on the packing Mac;
   the service refuses a package while a link exists ("already loaded"). `pack-release.sh` deletes
   it whichever product it packs — and the #73 liveness check then unwires live status (seen
   2026-10-14); turn it back on with a live key afterwards.
4. Pack per SUBMISSION.md: Certum session for the Windows helpers (expires in 1–2 h, log in right
   before), sign + notarize the voice helper, `DOTNET_ROLL_FORWARD=LatestMajor` for
   `logiplugintool`. Both products.
5. **Voice helper:** the pack builds, signs and notarizes it from source per product (#140's fix is
   on main), so the microphone prompt reads "Vizhi for Claude Code transcribes your speech…". Check
   it on the clean-machine install, then close #140 with the release.
6. Clean-machine install of each package (Options+ click), then step 5 of the release flow.
7. Tag `v2.4.0` and `vizhi-codex/v1.7.0`, publish the GitHub releases, attach the six layouts.

## After the cut

- **Logitech:** both packages submitted 2026-10-31 (owner, marketplace.logi.com/contribute). Ask the PM to rename the **listing** to Vizhi for Claude
  Code and say so in the mail — QA's reports all say Claude Console. Listing copy
  (`docs/marketplace-listing.md`) names Apple Terminal, iTerm2 and Windows Terminal now.
- **vizhi.dev — READY, not deployed:** branch `release/2.4.0-iterm2-and-rename` in
  `~/Work/MyApps/vizhi-site` has the new name on every page (URLs and anchors unchanged), iTerm2 in
  the requirements and FAQ, the six renamed layouts with fresh SHA-256s (old files kept downloadable),
  the system-profile FAQ entry and the 2.4.0 / 1.7.0 What's new. On release day: merge it to main and
  `firebase deploy --only hosting` from that folder (needs `firebase login`). The plugin README now
  links to vizhi.dev/layouts/; the older rshankar.com/keypad-profiles page still lists the old files
  — update it or point it at vizhi.dev (its repo is not checked out on this Mac).
- Close #128 with the release link.
- Re-import `VizhiCodex-Terminal.lp5` on the owner's Mac (its Terminal profile was deleted during
  the pass).

## Known noise, not blocking (file as issues)

- New Tab logs "new session opens in the home folder" although it never uses the folder.
- Vizhi logs Claude Console's live-status state ("Live status: Off") — engine refresh not gated on
  `LiveStatusApplies`.
- `pack-release.sh` removing the dev `.link` (step 3 above).
