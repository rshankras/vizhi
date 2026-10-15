namespace Loupedeck.ClaudeConsolePlugin.Platform
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    /// <summary>
    /// The macOS backend: drives Terminal.app and iTerm2 through osascript, discovers sessions with
    /// `ps`, and keys sessions by TTY (e.g. "ttys003") — the same key the bash statusline/hook
    /// scripts derive via `ps -o tty`, which is what lets the two sides agree on a filename.
    ///
    /// This is the original 1.x behavior, moved behind IPlatformBridge unchanged, with the
    /// terminal-specific AppleScript moved once more behind <see cref="MacTerminalDriver"/> so a
    /// second terminal application could be added (#128). The AppleScript is verbatim: the focus
    /// guard, the settling delays, and the key codes are all load-bearing and pinned by tests.
    ///
    /// WHICH DRIVER. Discovery records the application that owns each session, so a press on a
    /// session key reaches that application and no other — a mixed setup with sessions in both
    /// terminals routes every press correctly. Gestures that have no session (New Tab, New Claude,
    /// Go to Project, the window keys) go to the terminal the user is using: the one in front at
    /// the last probe, or the one the keys last touched, falling back to Terminal.app.
    /// </summary>
    internal sealed class MacPlatformBridge : IPlatformBridge
    {
        public String Name => "macOS";
        private readonly Dictionary<String, String> _sessionDirectories = new();
        public IReadOnlyDictionary<String, String> SessionDirectories => _sessionDirectories;
        internal Func<Int32, String> ProcessDirectoryReader { get; set; } = MacProcessDirectory.Read;
        private readonly Dictionary<String, DateTime> _sessionStarts = new();
        public IReadOnlyDictionary<String, DateTime> SessionStartTimes => _sessionStarts;
        internal Func<Int32, DateTime?> ProcessStartReader { get; set; } = MacProcessDirectory.StartTimeUtc;

        public Boolean IsSupported => OperatingSystem.IsMacOS();

        // Measured, not assumed — see IPlatformBridge.SettingsApplyLive (#58, 2026-09-02).
        public Boolean SettingsApplyLive => true;

        // WHAT counts as a session, supplied at construction. The bridge never learns which agent
        // this describes — that is the whole point of keeping the two seams orthogonal. Defaults to
        // matching NOTHING: a bridge built before its product declares an agent must find no
        // sessions rather than another agent's.
        private readonly AgentProcessMatcher _matcher;

        // The CLI a new session runs. Data, not knowledge of an agent: the bridge opens a terminal
        // and types this, exactly as it would any other command.
        private readonly String _cliCommand;

        public MacPlatformBridge(AgentProcessMatcher matcher = null, String cliCommand = null)
        {
            this._matcher = matcher ?? AgentProcessMatcher.None;
            this._cliCommand = String.IsNullOrWhiteSpace(cliCommand) ? "claude" : cliCommand;
        }

        // ------------------------------------------------------------------------------------------
        // Terminal drivers (#128)
        // ------------------------------------------------------------------------------------------

        // The driver that reaches each discovered session, by TTY. Rebuilt on every scan; a session
        // the scan has not seen (or one with no owner, under a test's own filter) is Terminal.app's,
        // which is exactly the pre-#128 behaviour.
        private readonly Dictionary<String, MacTerminalDriver> _sessionDrivers = new(StringComparer.Ordinal);

        // The drivers whose application is RUNNING, per the last scan. The frontmost probe only asks
        // these: a script that names an application which is not even installed can raise a
        // "Where is …?" dialog while compiling, and the probe runs on the poll timer. Terminal.app is
        // part of every macOS, so it is always asked, guarded by its own "is running".
        private IReadOnlyList<MacTerminalDriver> _runningDrivers = new[] { MacTerminalDriver.Terminal };

        // The terminal the user is using, as far as the bridge can tell: set by a frontmost probe
        // that found one of them in front, and by every press that reached a session. The most
        // recent signal wins, so pressing an iTerm2 session key moves New Tab there at once rather
        // than after the next probe.
        private MacTerminalDriver _currentDriver;

        /// <summary>The driver for gestures that name no session: New Tab, New Claude, Go to Project, windows.</summary>
        internal MacTerminalDriver PreferredDriver => this._currentDriver ?? MacTerminalDriver.Terminal;

        /// <summary>The driver that reaches <paramref name="sessionKey"/>, or the preferred one for no/unknown session.</summary>
        internal MacTerminalDriver DriverFor(String sessionKey)
        {
            if (!String.IsNullOrEmpty(sessionKey) && this._sessionDrivers.TryGetValue(sessionKey, out var owner))
            {
                return owner;
            }

            return this.PreferredDriver;
        }

        /// <summary>The agent, started in <paramref name="directory"/> when the bridge chose one (#113).</summary>
        private String AgentShellCommand(String directory) =>
            String.IsNullOrWhiteSpace(directory)
                ? this._cliCommand
                : "cd " + ShellQuote(directory) + " && " + this._cliCommand;

        // Test seam: when set, replaces the real osascript invocation so the unit tests can assert
        // on the exact script + arguments a key press would send without driving the window server.
        // Null in production — nothing but the tests ever assigns it.
        internal Func<List<String>, Int32, Boolean, String> OsascriptRunner { get; set; }

        // Test seam for the process scan: lets the tests feed captured `ps` output.
        internal Func<String> PsRunner { get; set; }

        // Skip-window for the frontmost probe after it overruns (#46) — ONE PER DRIVER, so a terminal
        // that stops answering Apple Events (a quit-confirmation sheet, mid-quit) backs off its own
        // ask and nobody else's. Instance state, and the poll loop is non-overlapping, so it needs
        // no locking.
        private readonly Dictionary<MacTerminalDriver, ProbeBackoff> _frontmostBackoff = new();

        private ProbeBackoff BackoffFor(MacTerminalDriver driver)
        {
            if (!this._frontmostBackoff.TryGetValue(driver, out var backoff))
            {
                backoff = new ProbeBackoff();
                this._frontmostBackoff[driver] = backoff;
            }

            return backoff;
        }

        // ------------------------------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Which terminal tabs are running the agent right now. Bounded and killed on hang, exactly
        /// like the osascript calls — a wedged `ps` must never be able to pile up poll threads.
        /// </summary>
        public HashSet<String> DiscoverSessions()
        {
            if (!OperatingSystem.IsMacOS() && this.PsRunner == null)
            {
                return null;
            }

            var output = this.RunCapture("/bin/ps", new List<String> { "-axo", "pid=,ppid=,tty=,command=" }, 5000);
            if (output == null)
            {
                return null;
            }

            var found = AgentProcessWatcher.Discover(output, this._matcher, this.DrivableOwner);
            _sessionDirectories.Clear();
            _sessionStarts.Clear();
            // Codex may omit SessionStart until its first prompt. Ask the live process for cwd;
            // no directory scan, extra ps, or external command is needed. Its start time rides
            // along from the same syscall family: with the shared daemon routing every session's
            // hooks through one terminal, folder plus start time is how the registry puts an
            // envelope back on the terminal it belongs to.
            if (_cliCommand == "codex")
            {
                foreach (var row in AgentProcessWatcher.Parse(output, _matcher))
                {
                    if (found.Ttys.Contains(row.Tty) && Int32.TryParse(row.Pid, out var pid))
                    {
                        var directory = ProcessDirectoryReader(pid);
                        if (!String.IsNullOrWhiteSpace(directory)) { _sessionDirectories[row.Tty] = directory; }
                        var started = ProcessStartReader(pid);
                        if (started.HasValue) { _sessionStarts[row.Tty] = started.Value; }
                    }
                }
            }

            // Which driver reaches each session, and which terminal applications are running at
            // all (#128). Both come from the listing already in hand — no second scan.
            this._sessionDrivers.Clear();
            foreach (var pair in found.Owners)
            {
                var driver = MacTerminalDriver.For(pair.Value);
                if (driver != null)
                {
                    this._sessionDrivers[pair.Key] = driver;
                }
            }

            var running = new List<MacTerminalDriver>();
            foreach (var driver in MacTerminalDriver.All)
            {
                if (driver == MacTerminalDriver.Terminal || IsRunning(driver, found.AppBinaries))
                {
                    running.Add(driver);
                }
            }
            this._runningDrivers = running;

            // Say WHY a session is missing from the keys — once per session, not per poll (#27).
            // Before #29 a cmux session took a slot, the press pinned a TTY the keys could not
            // reach, and the log said nothing at all.
            foreach (var s in found.Skipped)
            {
                if (this._reportedUndrivable.Add(s.Tty + "|" + (s.Owner ?? "?")))
                {
                    PluginLog.Warning(s.Owner == null
                        ? $"MacPlatformBridge: session on {s.Tty} is not shown — no terminal application in its ancestry (tmux, screen or ssh?), so the keys cannot reach it"
                        : $"MacPlatformBridge: session on {s.Tty} is not shown — it runs in {s.Owner}, which the keys cannot drive (Terminal.app and iTerm2 only; #29, #128)");
                }
            }

            return found.Ttys;
        }

        private static Boolean IsRunning(MacTerminalDriver driver, IEnumerable<String> appBinaries)
        {
            foreach (var binary in appBinaries)
            {
                if (driver.Owns(binary))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Which terminal application's sessions the keys can actually reach (#29). Every action key
        /// finds the tab by TTY and types through that application's AppleScript, so a session takes
        /// a key only if a <see cref="MacTerminalDriver"/> owns its terminal: Terminal.app and iTerm2
        /// (#128). Ghostty, Warp and editor-integrated terminals expose nothing addressable by TTY,
        /// so a session in them can only ever be a key that does nothing. Settable so tests can
        /// switch the filter off (null) or supply their own.
        /// </summary>
        internal Func<String, Boolean> DrivableOwner { get; set; } = MacTerminalDriver.IsDrivable;

        /// <summary>Terminal.app alone — the pre-#128 filter, kept for the tests that pin it.</summary>
        internal static Boolean IsTerminalApp(String ownerCommand) => MacTerminalDriver.Terminal.Owns(ownerCommand);

        private readonly HashSet<String> _reportedUndrivable = new HashSet<String>(StringComparer.Ordinal);

        /// <summary>
        /// The TTY (e.g. "ttys003") of the frontmost terminal tab, or null if no drivable terminal
        /// is the frontmost app / is running. Matches the key the bash scripts derive from
        /// `ps -o tty`. As a side effect, remembers WHICH terminal was in front, for the gestures
        /// that name no session.
        /// </summary>
        public String QueryFrontmostSession()
        {
            if (!OperatingSystem.IsMacOS() && this.OsascriptRunner == null)
            {
                return null;
            }

            // Each running driver is asked in its OWN osascript run, the terminal the user is using
            // first, stopping at the first answer. One combined script was the first shape, and the
            // device showed why not: AppleScript loads a RUNNING application's dictionary by asking
            // the process, so while iTerm2 sat behind its quit-confirmation sheet the whole script
            // failed to compile (2026-10-15 14:31:59, "Expected ',' but found identifier" at
            // `session`), then overran seven times — and Terminal.app's tab tracking stopped with
            // it, although Terminal was answering. Separate runs mean an unresponsive terminal costs
            // only its own ask, and the common case (the user in one terminal) is still one run.
            foreach (var driver in this.ProbeOrder())
            {
                // #46: an overrun means that application is not answering Apple Events, and the next
                // probe would ask it the same question — blocking the non-overlapping poll loop for
                // another full 2000ms to do it. Skip a few opportunities instead. An EMPTY answer is
                // not a failure and must not back off: it just means that terminal isn't frontmost.
                var backoff = this.BackoffFor(driver);
                if (backoff.ShouldSkip())
                {
                    continue;
                }

                var raw = this.RunOsascriptCapture(new List<String> { "-e", driver.FrontmostQuery + "return \"\"" }, out var timedOut);
                if (timedOut)
                {
                    backoff.RecordTimeout();
                    continue;
                }

                backoff.RecordSuccess();
                var tty = this.ParseFrontmostReply(raw);
                if (tty != null)
                {
                    return tty;
                }
            }

            return null;
        }

        /// <summary>The running drivers in the order the probe asks them: the user's current terminal first.</summary>
        internal IReadOnlyList<MacTerminalDriver> ProbeOrder()
        {
            var order = new List<MacTerminalDriver>();
            var current = this.PreferredDriver;
            if (this._runningDrivers.Contains(current))
            {
                order.Add(current);
            }

            foreach (var driver in this._runningDrivers)
            {
                if (!order.Contains(driver))
                {
                    order.Add(driver);
                }
            }

            return order;
        }

        // "Terminal|/dev/ttys003" → "ttys003", remembering Terminal.app as the current terminal.
        // A bare "/dev/ttys003" (no application named) is still a tty, so a stubbed probe that
        // answers the old way keeps working.
        private String ParseFrontmostReply(String raw)
        {
            if (String.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var reply = raw.Trim();
            var bar = reply.IndexOf('|');
            if (bar >= 0)
            {
                var driver = MacTerminalDriver.Named(reply.Substring(0, bar));
                if (driver != null)
                {
                    this._currentDriver = driver;
                }

                reply = reply.Substring(bar + 1);
            }

            return NormalizeTty(reply);
        }

        /// <summary>"/dev/ttys003" (osascript) -> "ttys003"; "ttys003" (ps) stays "ttys003".</summary>
        internal static String NormalizeTty(String raw)
        {
            if (String.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            var s = raw.Trim();
            var slash = s.LastIndexOf('/');
            if (slash >= 0)
            {
                s = s.Substring(slash + 1);
            }
            return s.Length > 0 && s != "??" ? s : null;
        }

        // ------------------------------------------------------------------------------------------
        // Injection
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Type text into the target tab and optionally press Return. The text travels as an
        /// osascript ARGUMENT — no AppleScript string escaping, so quotes/backslashes in a voice
        /// transcript can't break (or extend) the script. Newlines are flattened to spaces so a
        /// multi-line transcript doesn't submit early.
        /// </summary>
        /// <summary>
        /// Deliver the text (argv item 2) through the clipboard rather than System Events'
        /// `keystroke`.
        ///
        /// WHY. `keystroke` does not send characters — it asks macOS to press the keys that WOULD
        /// PRODUCE those characters under the CURRENT input source. On a non-US layout the terminal
        /// therefore receives something else entirely, silently: reproduced on hardware 2026-08-27
        /// with the Russian layout selected, where "the quick brown fox 123" arrived as
        /// "ффф ффффф ффффф ффф 123" — every letter collapsed to ф, digits and spaces intact. Every
        /// text-carrying action was affected: prompts, git, slash commands, voice transcripts and
        /// the Yes/No badges (QA retest of 2.0.1, finding 4; #22).
        ///
        /// A clipboard paste carries the characters themselves, so it is layout-independent and
        /// Unicode-safe — the same path a human uses. The text still travels as an osascript
        /// ARGUMENT, never interpolated into the script source, so quotes and backslashes in a
        /// transcript still cannot break out. The same body serves every driver: once the guard
        /// has the right tab in front, a paste lands in it whichever terminal it is.
        ///
        /// The clipboard is saved and restored as a RECORD, which preserves every flavour the user
        /// had (an image, styled text) rather than flattening it to a string. Both halves are
        /// wrapped in try blocks: failing to save must not stop the injection, and failing to
        /// restore must not fail an injection that has already landed.
        ///
        /// The delay before restoring is not optional — the terminal reads the pasteboard when
        /// Cmd+V is handled, so restoring too early pastes the OLD clipboard.
        /// </summary>
        private const String PasteTextBody =
            "set savedClipboard to missing value\n" +
            "try\n" +
            "  set savedClipboard to (the clipboard as record)\n" +
            "end try\n" +
            "set the clipboard to (item 2 of argv)\n" +
            "delay 0.05\n" +
            "tell application \"System Events\" to key code 9 using command down\n" +   // Cmd+V
            "delay 0.2\n" +
            "if savedClipboard is not missing value then\n" +
            "  try\n" +
            "    set the clipboard to savedClipboard\n" +
            "  end try\n" +
            "end if\n";

        public InjectionOutcome InjectText(String sessionKey, String text, Boolean pressEnter)
        {
            if (String.IsNullOrEmpty(text))
            {
                return InjectionOutcome.Skipped;
            }

            if (!this.CanRun())
            {
                return InjectionOutcome.Unsupported;
            }

            var flattened = text.Replace("\r", " ").Replace("\n", " ");
            var body = PasteTextBody;
            if (pressEnter)
            {
                // A leading "/" opens Claude Code's slash-command autocomplete. Pressing Return
                // before it finishes filtering to the typed command selects whatever is highlighted
                // (often a recent command like /copy), so pause to let the menu settle first. Harmless
                // for plain text (Git/Prompts/voice) where no menu is shown.
                body += "delay 0.35\n" +
                        "tell application \"System Events\" to key code 36\n"; // Return
            }

            return this.RunGuardedInjection(sessionKey, body, flattened);
        }

        public InjectionOutcome InjectKey(String sessionKey, KeyStroke key)
        {
            if (!this.CanRun())
            {
                return InjectionOutcome.Unsupported;
            }

            return this.RunGuardedInjection(
                sessionKey, $"tell application \"System Events\" to {AppleScriptFor(key)}\n");
        }

        public InjectionOutcome InjectTabThenEnter(String sessionKey)
        {
            if (!this.CanRun())
            {
                return InjectionOutcome.Unsupported;
            }

            return this.RunGuardedInjection(
                sessionKey,
                "tell application \"System Events\" to key code 48\n" + // Tab — accept the suggestion
                "delay 0.3\n" +                                          // let the completion register
                "tell application \"System Events\" to key code 36\n"); // Return — submit
        }

        /// <summary>
        /// The neutral key vocabulary rendered as a System Events statement. These key codes are
        /// macOS virtual key codes and are load-bearing — pinned by KeyStrokeMappingTests.
        /// </summary>
        internal static String AppleScriptFor(KeyStroke stroke)
        {
            var code = stroke.Key switch
            {
                TerminalKey.Escape => 53,
                TerminalKey.Return => 36,
                TerminalKey.Tab => 48,
                TerminalKey.ArrowUp => 126,
                TerminalKey.ArrowDown => 125,
                TerminalKey.ArrowLeft => 123,
                TerminalKey.ArrowRight => 124,
                TerminalKey.PageUp => 116,
                TerminalKey.PageDown => 121,
                _ => throw new ArgumentOutOfRangeException(nameof(stroke), stroke.Key, "unmapped key"),
            };

            var spec = $"key code {code}";
            var mods = ModifierList(stroke.Modifiers);
            return mods == null ? spec : $"{spec} using {mods}";
        }

        // AppleScript modifier syntax: "using {shift down}" / "using {control down, shift down}".
        // Order is fixed (control, shift, alt, command) so the emitted script is deterministic.
        private static String ModifierList(KeyModifiers mods)
        {
            if (mods == KeyModifiers.None)
            {
                return null;
            }

            var parts = new List<String>();
            if ((mods & KeyModifiers.Control) != 0) { parts.Add("control down"); }
            if ((mods & KeyModifiers.Shift) != 0) { parts.Add("shift down"); }
            if ((mods & KeyModifiers.Alt) != 0) { parts.Add("option down"); }
            if ((mods & KeyModifiers.Command) != 0) { parts.Add("command down"); }
            return "{" + String.Join(", ", parts) + "}";
        }

        // Compose the session's driver's focus guard + injection body into one script and run it.
        // textArg (when non-null) becomes argv item 2 — passing text as an argument sidesteps
        // AppleScript escaping entirely.
        private InjectionOutcome RunGuardedInjection(String sessionKey, String injectionBody, String textArg = null)
        {
            var driver = this.DriverFor(sessionKey);
            var script = driver.FocusGuardScript + injectionBody + "return \"ok\"\n" + "end run";
            var args = new List<String>
            {
                "-e", script,
                String.IsNullOrEmpty(sessionKey) ? "" : "/dev/" + sessionKey,
            };
            if (textArg != null)
            {
                args.Add(textArg);
            }

            var result = this.RunOsascriptCore(args, 15000, wantOutput: true);
            var outcome = result switch
            {
                "ok" => InjectionOutcome.Ok,
                "no-terminal" => InjectionOutcome.NoTerminal,
                "tab-missing" => InjectionOutcome.SessionMissing,
                _ => InjectionOutcome.Failed,
            };

            if (outcome != InjectionOutcome.Ok)
            {
                PluginLog.Warning($"MacPlatformBridge: injection skipped ({result ?? "osascript failed"}) — the session's {driver.Name} tab is unavailable");
            }
            else
            {
                this._currentDriver = driver;
            }
            return outcome;
        }

        // ------------------------------------------------------------------------------------------
        // Focus and navigation
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Bring a specific session's tab to the front. Same tab-by-TTY script the injection guard
        /// uses, minus the typing.
        /// </summary>
        public void FocusSession(String sessionKey)
        {
            if (!this.CanRun())
            {
                return;
            }

            var driver = this.DriverFor(sessionKey);
            var result = this.RunOsascriptCore(
                new List<String> { "-e", driver.FocusGuardScript + "return \"ok\"\n" + "end run", "/dev/" + sessionKey },
                15000,
                wantOutput: true);
            if (result == "ok")
            {
                this._currentDriver = driver;
            }
        }

        public void Navigate(TerminalAction action, String directory = null)
        {
            // The two gestures that START the agent carry the shell command as an argument. A
            // plain New Tab stays Cmd+T: the terminal's own "new tabs open in" preference applies,
            // and typing a `cd` into a tab the user asked to be empty would be noise.
            if (action is TerminalAction.NewClaudeTab or TerminalAction.NewClaudeWindow)
            {
                if (!this.CanRun())
                {
                    return;
                }

                var launcher = this.PreferredDriver;
                var launch = action == TerminalAction.NewClaudeTab ? launcher.NewAgentTabScript : launcher.NewAgentWindowScript;
                this.RunOsascriptCore(new List<String> { "-e", launch, this.AgentShellCommand(directory) }, 15000, wantOutput: false);
                this._currentDriver = launcher;
                return;
            }

            var driver = this.PreferredDriver;
            var script = driver.NavigationScript(action);
            if (script != null)
            {
                this.RunAppleScript(script);
                this._currentDriver = driver;
            }
        }

        /// <summary>
        /// cd into the project and run the configured agent, in the terminal the user is using.
        /// Smart about where:
        ///   • no window open               → open one and run there
        ///   • current tab is an IDLE shell → reuse it (this is the "empty terminal" case)
        ///   • current tab is BUSY (claude/cmd running) → open a NEW tab, so we never type into a
        ///     live session
        /// Terminal.app's `busy` is false only at an idle shell prompt; iTerm2's `is at shell prompt`
        /// is true only with its shell integration installed, so without it the launch always opens
        /// a new tab — the safe direction.
        /// </summary>
        public void LaunchClaudeInProject(String projectDir)
        {
            if (!this.CanRun())
            {
                return;
            }

            this.LaunchShellCommand("cd " + ShellQuote(projectDir) + " && " + this._cliCommand);
        }

        // The busy-aware launch shared by LaunchClaudeInProject and LaunchAgentSession: reuse an
        // idle tab, open a new one when it is busy, never type into a live session.
        private void LaunchShellCommand(String cmd)
        {
            if (!this.CanRun())
            {
                return;
            }

            // Pass the command as an osascript argument instead of interpolating it into the
            // AppleScript source. Project names may legitimately contain quotes or backslashes.
            var driver = this.PreferredDriver;
            this.RunOsascriptCore(new List<String> { "-e", driver.LaunchShellCommandScript, cmd }, 15000, wantOutput: false);
            this._currentDriver = driver;
        }

        private static String ShellQuote(String value) =>
            "'" + (value ?? String.Empty).Replace("'", "'\"'\"'") + "'";

        /// <summary>
        /// `screencapture -i`: the system's own region/window picker (the Shift+Cmd+4 gesture).
        /// Cancelling with Esc exits without writing a file, so file-exists IS the outcome — the
        /// tool's exit code does not distinguish the cases. The generous timeout is thinking time:
        /// the user is aiming a crosshair, and killing the picker under them takes the shot anyway.
        /// </summary>
        public Boolean CaptureScreenshotInteractive(String outputPath)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            BoundedProcess.Run("/usr/sbin/screencapture", new List<String> { "-i", outputPath }, 120000, wantOutput: false);

            if (!File.Exists(outputPath))
            {
                PluginLog.Info("MacPlatformBridge.CaptureScreenshotInteractive: no file — cancelled, or the service lacks a Screen Recording grant (System Settings → Privacy & Security)");
                return false;
            }

            return true;
        }

        public void LaunchAgentSession(String[] extraArgs)
        {
            var quoted = new List<String>();
            foreach (var arg in extraArgs ?? Array.Empty<String>())
            {
                quoted.Add(ShellQuote(arg));
            }

            this.LaunchShellCommand(this._cliCommand + " " + String.Join(" ", quoted));
        }

        public void Alert() => this.RunAppleScript("beep");

        /// <summary>
        /// Run an arbitrary multi-line AppleScript via osascript — for automation richer than a
        /// single keystroke. Needs Accessibility. Internal: nothing outside this backend should
        /// be composing AppleScript.
        /// </summary>
        internal void RunAppleScript(String script)
        {
            if (!this.CanRun())
            {
                PluginLog.Info("MacPlatformBridge.RunAppleScript: non-macOS — skipped");
                return;
            }

            this.RunOsascriptCore(new List<String> { "-e", script }, 15000, wantOutput: false);
        }

        // In production this is "are we on macOS"; in tests, a stubbed runner stands in for the OS.
        private Boolean CanRun() => OperatingSystem.IsMacOS() || this.OsascriptRunner != null;

        // ------------------------------------------------------------------------------------------
        // Bounded subprocess plumbing
        // ------------------------------------------------------------------------------------------

        private String RunOsascriptCore(List<String> args, Int32 timeoutMs, Boolean wantOutput) =>
            this.RunOsascriptCore(args, timeoutMs, wantOutput, out _);

        private String RunOsascriptCore(List<String> args, Int32 timeoutMs, Boolean wantOutput, out Boolean timedOut)
        {
            var runner = this.OsascriptRunner;
            if (runner != null)
            {
                // A stubbed runner stands in for the OS and cannot overrun a budget it never waits on.
                // The backoff POLICY is covered directly by ProbeBackoffTests; this line is the seam,
                // not the behaviour.
                timedOut = false;
                return runner(args, timeoutMs, wantOutput);
            }

            return BoundedProcess.Run("osascript", args, timeoutMs, wantOutput, out timedOut);
        }

        // Like a fire-and-forget osascript but returns stdout (trimmed) — for querying state (e.g.
        // the frontmost terminal tab's TTY) on the poll timer, so it uses a short, snappy timeout.
        private String RunOsascriptCapture(List<String> args, out Boolean timedOut) =>
            this.RunOsascriptCore(args, 2000, wantOutput: true, out timedOut);

        // Run a plain capture-only subprocess (the `ps` session scan) under the same hard-timeout
        // discipline as osascript.
        private String RunCapture(String file, List<String> args, Int32 timeoutMs)
        {
            var runner = this.PsRunner;
            if (runner != null)
            {
                return runner();
            }

            return BoundedProcess.Run(file, args, timeoutMs, wantOutput: true);
        }
    }
}
