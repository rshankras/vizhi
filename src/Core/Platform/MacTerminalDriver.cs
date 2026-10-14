namespace Loupedeck.ClaudeConsolePlugin.Platform
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One terminal application the macOS bridge can drive (#29, #128).
    ///
    /// The bridge keys sessions by TTY and types only after focusing the exact tab that owns it,
    /// or types nothing. That needs a terminal whose AppleScript dictionary addresses a tab by its
    /// TTY: Terminal.app (`tty of tab`) and iTerm2 (`tty of session`) do; Ghostty, Warp and the
    /// editor-embedded terminals do not, so a session in them can only ever be a key that does
    /// nothing, and discovery leaves it off the keys instead (AgentProcessWatcher.Discover).
    ///
    /// Everything a driver knows is AppleScript text. The bridge composes it — the focus guard
    /// ahead of a typing body, the launch scripts with the shell command as an osascript ARGUMENT
    /// — and runs it; the driver never touches a process. Terminal.app's scripts are the 1.x
    /// originals moved here verbatim: the focus guard, the settling delays and the key codes are
    /// load-bearing and pinned by tests.
    /// </summary>
    internal abstract class MacTerminalDriver
    {
        public static readonly MacTerminalDriver Terminal = new AppleTerminalDriver();
        public static readonly MacTerminalDriver ITerm2 = new ITerm2Driver();

        /// <summary>Every driver, in the order the frontmost probe asks them.</summary>
        public static readonly IReadOnlyList<MacTerminalDriver> All = new[] { Terminal, ITerm2 };

        /// <summary>What the user calls it, for logs and faces: "Terminal.app", "iTerm2".</summary>
        public abstract String Name { get; }

        /// <summary>The name AppleScript addresses it by: `tell application "…"`.</summary>
        public abstract String ScriptName { get; }

        /// <summary>
        /// Whether this driver reaches sessions owned by the application whose binary is
        /// <paramref name="ownerCommand"/> — the first `.app/Contents/MacOS/` ancestor of the
        /// agent process, as AgentProcessWatcher.Discover finds it.
        /// </summary>
        public abstract Boolean Owns(String ownerCommand);

        /// <summary>
        /// The prelude of every guarded script: `on run argv`, then focus the tab whose TTY is argv
        /// item 1 ("/dev/ttysNNN", or "" for the application's current tab), returning
        /// "no-terminal" when the application is not running and "tab-missing" when no tab has
        /// that TTY — typing NOTHING in either case. Falls through, settled, when the tab is in
        /// front. The bridge appends the typing body and `end run`.
        /// </summary>
        public abstract String FocusGuardScript { get; }

        /// <summary>
        /// A fragment for the frontmost probe: when this application is running AND frontmost,
        /// returns "&lt;ScriptName&gt;|&lt;tty of its current tab&gt;"; otherwise falls through
        /// without launching anything.
        /// </summary>
        public abstract String FrontmostQuery { get; }

        /// <summary>The script for a navigation gesture, or null for the two that start the agent.</summary>
        public abstract String NavigationScript(TerminalAction action);

        /// <summary>Open a new tab (or the first window) and run argv item 1 in it.</summary>
        public abstract String NewAgentTabScript { get; }

        /// <summary>Open a new window and run argv item 1 in it.</summary>
        public abstract String NewAgentWindowScript { get; }

        /// <summary>
        /// Run argv item 1 in an IDLE current tab, or in a new tab when the current one is busy —
        /// never in a live session.
        /// </summary>
        public abstract String LaunchShellCommandScript { get; }

        /// <summary>The driver for the application that owns a session, or null if none can drive it.</summary>
        public static MacTerminalDriver For(String ownerCommand)
        {
            foreach (var driver in All)
            {
                if (driver.Owns(ownerCommand))
                {
                    return driver;
                }
            }

            return null;
        }

        /// <summary>The default session filter: a session takes a key only if some driver reaches it.</summary>
        public static Boolean IsDrivable(String ownerCommand) => For(ownerCommand) != null;

        /// <summary>The driver a frontmost-probe reply names, or null.</summary>
        public static MacTerminalDriver Named(String scriptName)
        {
            foreach (var driver in All)
            {
                if (String.Equals(driver.ScriptName, scriptName, StringComparison.Ordinal))
                {
                    return driver;
                }
            }

            return null;
        }

        // Bring the application to the front, then send a key chord to it.
        protected String ActivateThen =>
            "tell application \"" + this.ScriptName + "\" to activate\n" +
            "delay 0.05\n" +
            "tell application \"System Events\" to ";

        public override String ToString() => this.Name;
    }

    /// <summary>
    /// Apple's Terminal.app. Tabs carry a `tty`; `do script` runs a command in a tab atomically;
    /// `busy` is false only at an idle shell prompt.
    /// </summary>
    internal sealed class AppleTerminalDriver : MacTerminalDriver
    {
        public override String Name => "Terminal.app";
        public override String ScriptName => "Terminal";

        public override Boolean Owns(String ownerCommand) =>
            ownerCommand != null && ownerCommand.Contains("/Terminal.app/Contents/MacOS/Terminal", StringComparison.Ordinal);

        // argv item 1 = target tty ("/dev/ttysNNN"), or "" to use Terminal's front tab.
        internal const String FocusGuard =
            "on run argv\n" +
            "set targetTty to item 1 of argv\n" +
            "if application \"Terminal\" is not running then\n" +  // never auto-launch Terminal
            "  beep\n" +
            "  return \"no-terminal\"\n" +
            "end if\n" +
            "tell application \"Terminal\"\n" +
            "  activate\n" +
            "  if targetTty is not \"\" then\n" +
            "    set found to false\n" +
            "    repeat with terminalWindow in windows\n" +
            // Not every Terminal window has tabs — a settings/inspector window raises
            // "Can't get every tab of item N of every window" (-1728), which aborted the whole
            // script and left the plugin unable to focus OR type. Skip such windows instead.
            "      try\n" +
            "        repeat with terminalTab in tabs of terminalWindow\n" +
            "          if (tty of terminalTab as text) is targetTty then\n" +
            "            set selected tab of terminalWindow to terminalTab\n" +
            "            set index of terminalWindow to 1\n" +
            "            set found to true\n" +
            "            exit repeat\n" +
            "          end if\n" +
            "        end repeat\n" +
            "      end try\n" +
            "      if found then exit repeat\n" +
            "    end repeat\n" +
            "    if not found then\n" +
            "      beep\n" +
            "      return \"tab-missing\"\n" +
            "    end if\n" +
            "  end if\n" +
            "end tell\n" +
            "delay 0.05\n";  // let activation settle before System Events types

        public override String FocusGuardScript => FocusGuard;

        // "... is running" avoids auto-LAUNCHING Terminal just to query it.
        public override String FrontmostQuery =>
            "if application \"Terminal\" is running then\n" +
            "  tell application \"Terminal\"\n" +
            "    try\n" +
            "      if frontmost then return \"Terminal|\" & (tty of selected tab of front window as text)\n" +
            "    end try\n" +
            "  end tell\n" +
            "end if\n";

        public override String NavigationScript(TerminalAction action) => action switch
        {
            TerminalAction.Activate => "tell application \"Terminal\" to activate",
            TerminalAction.NewTab => this.ActivateThen + "key code 17 using {command down}",                // Cmd+T (key code, not "t" — see KeyCodeT)
            TerminalAction.NextTab => this.ActivateThen + "key code 48 using {control down}",              // Ctrl+Tab
            TerminalAction.PreviousTab => this.ActivateThen + "key code 48 using {control down, shift down}",
            TerminalAction.NextWindow => this.ActivateThen + "key code 50 using {command down}",           // Cmd+`
            TerminalAction.PreviousWindow => this.ActivateThen + "key code 50 using {command down, shift down}",
            _ => null,
        };

        // Open a new tab, then run the agent's cli in it via `do script` (reliable command send —
        // no per-character keystroke timing). delay lets the new tab become front first. The shell
        // command travels as an osascript ARGUMENT (item 1 of argv), not interpolated into the
        // source: since #113 it may carry a `cd` to a project folder, and folder names
        // legitimately contain quotes and backslashes — the same discipline the launch uses.
        public override String NewAgentTabScript =>
            "on run argv\n" +
            "set shellCommand to item 1 of argv\n" +
            "tell application \"Terminal\"\n" +
            "  activate\n" +
            "  tell application \"System Events\" to key code 17 using command down\n" +
            "  delay 0.5\n" +
            "  do script shellCommand in front window\n" +
            "end tell\n" +
            "end run";

        // New WINDOW running the agent: `do script` with no "in" target opens a fresh window.
        public override String NewAgentWindowScript =>
            "on run argv\n" +
            "set shellCommand to item 1 of argv\n" +
            "tell application \"Terminal\"\n" +
            "  activate\n" +
            "  do script shellCommand\n" + // no target → new window
            "end tell\n" +
            "end run";

        // Smart about where:
        //   • no Terminal window open      → open one and run there
        //   • front tab is an IDLE shell   → reuse it (this is the "empty terminal" case)
        //   • front tab is BUSY (claude/cmd running) → open a NEW tab, so we never type into a
        //     live session
        // Terminal's `busy` is false only at an idle shell prompt, which is exactly the signal we want.
        public override String LaunchShellCommandScript =>
            "on run argv\n" +
            "set shellCommand to item 1 of argv\n" +
            "tell application \"Terminal\"\n" +
            "  activate\n" +
            "  if (count of windows) is 0 then\n" +
            "    do script shellCommand\n" +
            "  else\n" +
            "    set isIdle to false\n" +
            "    try\n" +
            "      set isIdle to (busy of selected tab of front window is false)\n" +
            "    end try\n" +
            "    if isIdle then\n" +
            "      do script shellCommand in front window\n" +   // reuses the idle tab (NOT 'selected tab of' — that form no-ops)
            "    else\n" +
            "      tell application \"System Events\" to key code 17 using command down\n" +
            "      delay 0.5\n" +
            "      do script shellCommand in front window\n" +
            "    end if\n" +
            "  end if\n" +
            "end tell\n" +
            "end run";
    }

    /// <summary>
    /// iTerm2 (#128). Sessions — one per split pane — carry a `tty`; `select` makes a session, its
    /// tab and its window current; `write text` runs a command in a session; `is at shell prompt`
    /// is true at an idle prompt WHEN iTerm2's shell integration is installed, and false otherwise,
    /// which errs on the side of a new tab. Windows, tabs and sessions are walked by index: iTerm2
    /// resolves `tab j of w` but not every property of a `repeat with t in tabs` reference.
    ///
    /// Nothing here is ever compiled on a machine where iTerm2 is not RUNNING: the bridge only
    /// hands a session to this driver after discovery has seen iTerm2's binary in the process
    /// listing, and only adds its frontmost query while it is there. Compiling
    /// `application "iTerm2"` where the app is absent could otherwise raise a "Where is iTerm2?"
    /// dialog on a poll.
    /// </summary>
    internal sealed class ITerm2Driver : MacTerminalDriver
    {
        public override String Name => "iTerm2";
        public override String ScriptName => "iTerm2";

        // The real ancestry of an iTerm2 session (this Mac, 2026-10-14, iTerm2 3.7.4):
        //   claude → -zsh → /usr/bin/login -fpl user …/iTerm.app/Contents/MacOS/ShellLauncher --launch_shell
        //          → ~/Library/Application Support/iTerm2/iTermServer-3.7.4 … → …/iTerm.app/Contents/MacOS/iTerm2
        // The first row that names the bundle is `login` (via ShellLauncher), so any path inside the
        // bundle counts, not just the iTerm2 binary — matching that alone skipped every real session
        // on the first device load. The session server lives OUTSIDE the bundle and, once iTerm2 has
        // been quit and relaunched, is the only iTerm2 process left above a restored session; a
        // session started with a custom command has no login row at all. So it counts too.
        public override Boolean Owns(String ownerCommand) =>
            ownerCommand != null
            && (ownerCommand.Contains("/iTerm.app/Contents/MacOS/", StringComparison.Ordinal)
                || ownerCommand.Contains("/iTermServer-", StringComparison.Ordinal));

        // argv item 1 = target tty ("/dev/ttysNNN"), or "" to use iTerm2's current session.
        internal const String FocusGuard =
            "on run argv\n" +
            "set targetTty to item 1 of argv\n" +
            "if application \"iTerm2\" is not running then\n" +  // never auto-launch iTerm2
            "  beep\n" +
            "  return \"no-terminal\"\n" +
            "end if\n" +
            "tell application \"iTerm2\"\n" +
            "  activate\n" +
            "  if targetTty is not \"\" then\n" +
            "    set found to false\n" +
            "    repeat with i from 1 to (count of windows)\n" +
            "      set w to window i\n" +
            "      try\n" +
            "        repeat with j from 1 to (count of tabs of w)\n" +
            "          set t to tab j of w\n" +
            "          repeat with k from 1 to (count of sessions of t)\n" +
            "            set s to session k of t\n" +
            "            if (tty of s as text) is targetTty then\n" +
            "              select s\n" +
            "              select t\n" +
            "              select w\n" +
            "              set found to true\n" +
            "              exit repeat\n" +
            "            end if\n" +
            "          end repeat\n" +
            "          if found then exit repeat\n" +
            "        end repeat\n" +
            "      end try\n" +
            "      if found then exit repeat\n" +
            "    end repeat\n" +
            "    if not found then\n" +
            "      beep\n" +
            "      return \"tab-missing\"\n" +
            "    end if\n" +
            "  end if\n" +
            "end tell\n" +
            "delay 0.05\n";  // let activation settle before System Events types

        public override String FocusGuardScript => FocusGuard;

        public override String FrontmostQuery =>
            "if application \"iTerm2\" is running then\n" +
            "  tell application \"iTerm2\"\n" +
            "    try\n" +
            "      if frontmost then return \"iTerm2|\" & (tty of current session of current tab of current window as text)\n" +
            "    end try\n" +
            "  end tell\n" +
            "end if\n";

        // iTerm2's own defaults: Cmd+T new tab, Cmd+Shift+] / Cmd+Shift+[ next / previous tab
        // (the Window menu's "Select Next Tab" / "Select Previous Tab"), Cmd+` cycles windows.
        public override String NavigationScript(TerminalAction action) => action switch
        {
            TerminalAction.Activate => "tell application \"iTerm2\" to activate",
            TerminalAction.NewTab => this.ActivateThen + "key code 17 using {command down}",
            TerminalAction.NextTab => this.ActivateThen + "key code 30 using {command down, shift down}",
            TerminalAction.PreviousTab => this.ActivateThen + "key code 33 using {command down, shift down}",
            TerminalAction.NextWindow => this.ActivateThen + "key code 50 using {command down}",
            TerminalAction.PreviousWindow => this.ActivateThen + "key code 50 using {command down, shift down}",
            _ => null,
        };

        // `write text` queues the command on the new session's pty, so the shell runs it as soon as
        // it is up — no settling delay, no per-character keystrokes. With no window open the "tab"
        // has nowhere to go, so a window is created instead.
        public override String NewAgentTabScript =>
            "on run argv\n" +
            "set shellCommand to item 1 of argv\n" +
            "tell application \"iTerm2\"\n" +
            "  activate\n" +
            "  if (count of windows) is 0 then\n" +
            "    set w to (create window with default profile)\n" +
            "    tell current session of w to write text shellCommand\n" +
            "  else\n" +
            "    tell current window\n" +
            "      set t to (create tab with default profile)\n" +
            "      tell current session of t to write text shellCommand\n" +
            "    end tell\n" +
            "  end if\n" +
            "end tell\n" +
            "end run";

        public override String NewAgentWindowScript =>
            "on run argv\n" +
            "set shellCommand to item 1 of argv\n" +
            "tell application \"iTerm2\"\n" +
            "  activate\n" +
            "  set w to (create window with default profile)\n" +
            "  tell current session of w to write text shellCommand\n" +
            "end tell\n" +
            "end run";

        // `is at shell prompt` is only ever true with shell integration installed; without it the
        // current session always looks busy and the command goes to a new tab — the safe direction.
        public override String LaunchShellCommandScript =>
            "on run argv\n" +
            "set shellCommand to item 1 of argv\n" +
            "tell application \"iTerm2\"\n" +
            "  activate\n" +
            "  if (count of windows) is 0 then\n" +
            "    set w to (create window with default profile)\n" +
            "    tell current session of w to write text shellCommand\n" +
            "  else\n" +
            "    set isIdle to false\n" +
            "    try\n" +
            "      set isIdle to (is at shell prompt of current session of current tab of current window)\n" +
            "    end try\n" +
            "    if isIdle then\n" +
            "      tell current session of current tab of current window to write text shellCommand\n" +
            "    else\n" +
            "      tell current window\n" +
            "        set t to (create tab with default profile)\n" +
            "        tell current session of t to write text shellCommand\n" +
            "      end tell\n" +
            "    end if\n" +
            "  end if\n" +
            "end tell\n" +
            "end run";
    }
}
