namespace Loupedeck.ClaudeConsolePlugin.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.ClaudeConsolePlugin.Platform;

    using Xunit;

    /// <summary>
    /// iTerm2 as a second terminal driver on macOS (#128). The contract is the one #29 set:
    /// a session takes a key only if the keys can reach its tab, and a press reaches THAT tab or
    /// nothing. With two drivers the new questions are routing — which application a press goes
    /// to — and the gestures that name no session (New Tab, New Claude, Go to Project), which
    /// must follow the terminal the user is actually using.
    ///
    /// Every assertion is on the osascript invocation a press would send, captured by the same
    /// seam the Terminal.app tests use; the AppleScript verbs themselves (`tty of session`,
    /// `select`, `create tab with default profile`, `write text`, `is at shell prompt`) were
    /// checked against iTerm2 3.7.4's dictionary on 2026-10-14.
    /// </summary>
    public class ITerm2DriverTests
    {
        // Real process shapes: claude → zsh → login → Terminal.app, the same under iTerm2, and a
        // Ghostty session the keys cannot reach.
        private const String MixedListing = @"
    1     0 ??       /sbin/launchd
 2164     1 ??       /System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal
 2269  2164 ttys000  login -pf ravi
 2287  2269 ttys000  -zsh
 3701  2287 ttys000  claude
 4000     1 ??       /Applications/iTerm.app/Contents/MacOS/iTerm2
 4010  4000 ttys004  login -fp ravi
 4020  4010 ttys004  -zsh
 4030  4020 ttys004  claude
 5000     1 ??       /Applications/Ghostty.app/Contents/MacOS/ghostty
 5010  5000 ttys007  -zsh
 5020  5010 ttys007  claude
";

        private const String TerminalOnlyListing = @"
    1     0 ??       /sbin/launchd
 2164     1 ??       /System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal
 2269  2164 ttys000  login -pf ravi
 2287  2269 ttys000  -zsh
 3701  2287 ttys000  claude
";

        private static MacPlatformBridge Bridge(String listing, List<List<String>> calls, Func<List<String>, String> reply = null) =>
            new MacPlatformBridge(AgentProcessMatcher.ClaudeCode)
            {
                PsRunner = () => listing,
                OsascriptRunner = (args, timeout, wantOutput) =>
                {
                    calls.Add(args);
                    return reply?.Invoke(args) ?? "ok";
                },
            };

        private static String Script(List<String> args) => args[1];

        private static Int32 GuardAt(String script, String app) =>
            script.IndexOf("tell application \"" + app + "\"", StringComparison.Ordinal);

        private static Int32 BodyAt(String script) =>
            script.IndexOf("tell application \"System Events\"", StringComparison.Ordinal);

        // ---------------------------------------------------------------------------------------
        // Eligibility and routing
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Sessions_in_both_terminals_take_keys_and_the_unreachable_one_does_not()
        {
            var mac = Bridge(MixedListing, new List<List<String>>());

            var sessions = mac.DiscoverSessions();

            Assert.Equal(new[] { "ttys000", "ttys004" }, sessions.OrderBy(s => s).ToArray());
        }

        [Fact]
        public void A_press_reaches_the_application_that_owns_the_session()
        {
            // THE routing property: with sessions in both terminals, the iTerm2 session's press
            // goes through iTerm2's guard and the Terminal.app session's through Terminal's —
            // never the other way round, and never "whichever is in front".
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls);
            mac.DiscoverSessions();

            mac.InjectKey("ttys004", KeyStroke.Return);
            var iterm = Script(calls.Last());
            Assert.Contains("if application \"iTerm2\" is not running then", iterm);
            Assert.Contains("if (tty of s as text) is targetTty then", iterm);
            Assert.Contains("select s", iterm);
            Assert.DoesNotContain("tell application \"Terminal\"", iterm);
            Assert.Equal("/dev/ttys004", calls.Last()[2]);

            mac.InjectKey("ttys000", KeyStroke.Return);
            var terminal = Script(calls.Last());
            Assert.Contains("set selected tab of terminalWindow to terminalTab", terminal);
            Assert.DoesNotContain("iTerm2", terminal);
            Assert.Equal("/dev/ttys000", calls.Last()[2]);
        }

        [Theory]
        [InlineData("text")]
        [InlineData("keystroke")]
        [InlineData("tabenter")]
        public void An_iTerm2_injection_keeps_the_guard_shape(String kind)
        {
            // Same safety property as InjectionGuardTests, through the other driver: one osascript
            // run, the app checked for running, the tab selected by TTY, bail without typing when
            // either fails — and the guard strictly BEFORE any System Events typing.
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls);
            mac.DiscoverSessions();
            calls.Clear();

            switch (kind)
            {
                case "text": mac.InjectText("ttys004", "hello", pressEnter: false); break;
                case "keystroke": mac.InjectKey("ttys004", KeyStroke.Escape); break;
                case "tabenter": mac.InjectTabThenEnter("ttys004"); break;
            }

            var script = Script(Assert.Single(calls));
            Assert.Contains("beep", script);
            Assert.Contains("return \"no-terminal\"", script);
            Assert.Contains("return \"tab-missing\"", script);
            Assert.Contains("activate", script);
            Assert.InRange(GuardAt(script, "iTerm2"), 0, BodyAt(script) - 1);
        }

        [Fact]
        public void Text_into_iTerm2_still_travels_as_an_argument_and_pastes()
        {
            // The clipboard path is driver-neutral (#22): once the guard has the tab in front, the
            // paste lands in it. The text must never enter the script source.
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls);
            mac.DiscoverSessions();
            var nasty = "say \"hi\" \\ && osascript -e 'beep'";

            mac.InjectText("ttys004", nasty, pressEnter: true);

            var call = calls.Last();
            Assert.Equal(nasty, call[3]);
            Assert.DoesNotContain(nasty, Script(call));
            Assert.Contains("set the clipboard to (item 2 of argv)", Script(call));
            Assert.Contains("key code 36", Script(call));
        }

        [Fact]
        public void A_session_discovery_has_not_seen_still_goes_to_Terminal_app()
        {
            // Pre-#128 behaviour, exactly: with no scan (every existing test) or an unknown key,
            // the press is Terminal.app's.
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls);

            mac.InjectKey("ttys009", KeyStroke.Return);

            Assert.Contains("tell application \"Terminal\"", Script(calls.Single()));
            Assert.DoesNotContain("iTerm2", Script(calls.Single()));
        }

        [Fact]
        public void A_rescan_that_no_longer_shows_the_session_drops_its_driver()
        {
            var calls = new List<List<String>>();
            var listing = MixedListing;
            var mac = new MacPlatformBridge(AgentProcessMatcher.ClaudeCode)
            {
                PsRunner = () => listing,
                OsascriptRunner = (args, timeout, wantOutput) => { calls.Add(args); return "ok"; },
            };
            mac.DiscoverSessions();
            Assert.Same(MacTerminalDriver.ITerm2, mac.DriverFor("ttys004"));

            listing = TerminalOnlyListing;
            mac.DiscoverSessions();

            Assert.Same(MacTerminalDriver.Terminal, mac.DriverFor("ttys004"));
        }

        // ---------------------------------------------------------------------------------------
        // The frontmost probe
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void The_probe_asks_iTerm2_only_while_it_is_running()
        {
            // A script that names an application which is not installed can raise a "Where is…?"
            // dialog while compiling, and the probe runs on the poll timer. iTerm2 is asked only
            // once the scan has seen its binary — Terminal.app, part of every macOS, always is.
            var calls = new List<List<String>>();
            var mac = Bridge(TerminalOnlyListing, calls);
            mac.DiscoverSessions();
            mac.QueryFrontmostSession();
            Assert.Contains("if application \"Terminal\" is running then", Script(calls.Last()));
            Assert.DoesNotContain("iTerm2", Script(calls.Last()));

            var both = Bridge(MixedListing, calls);
            both.DiscoverSessions();
            both.QueryFrontmostSession();
            var script = Script(calls.Last());
            Assert.Contains("if application \"Terminal\" is running then", script);
            Assert.Contains("if application \"iTerm2\" is running then", script);
            Assert.Contains("tty of current session of current tab of current window", script);
            Assert.EndsWith("return \"\"", script);
        }

        [Fact]
        public void Before_any_scan_the_probe_asks_Terminal_app_alone()
        {
            var calls = new List<List<String>>();

            Bridge(MixedListing, calls).QueryFrontmostSession();

            Assert.Contains("if application \"Terminal\" is running then", Script(calls.Single()));
            Assert.DoesNotContain("iTerm2", Script(calls.Single()));
        }

        [Theory]
        [InlineData("iTerm2|/dev/ttys004", "ttys004")]
        [InlineData("Terminal|/dev/ttys000", "ttys000")]
        [InlineData("/dev/ttys005", "ttys005")]   // a reply with no application named is still a tty
        [InlineData("", null)]
        public void A_probe_reply_yields_the_session_id(String reply, String expected)
        {
            var mac = Bridge(MixedListing, new List<List<String>>(), _ => reply);

            Assert.Equal(expected, mac.QueryFrontmostSession());
        }

        [Fact]
        public void The_terminal_in_front_is_where_a_new_tab_opens()
        {
            // New Tab names no session. It goes to the terminal the probe last saw in front.
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls, args => Script(args).Contains("frontmost") ? "iTerm2|/dev/ttys004" : "ok");
            mac.DiscoverSessions();
            mac.QueryFrontmostSession();
            calls.Clear();

            mac.Navigate(TerminalAction.NewTab);

            var script = Script(Assert.Single(calls));
            Assert.StartsWith("tell application \"iTerm2\" to activate", script);
            Assert.Contains("key code 17 using {command down}", script);
        }

        [Fact]
        public void An_empty_probe_reply_keeps_the_last_known_terminal()
        {
            // Glancing at a browser must not reset New Tab to Terminal.app — same rule as ActiveTty.
            var calls = new List<List<String>>();
            var reply = "iTerm2|/dev/ttys004";
            var mac = Bridge(MixedListing, calls, args => Script(args).Contains("frontmost") ? reply : "ok");
            mac.DiscoverSessions();
            mac.QueryFrontmostSession();
            reply = "";
            Assert.Null(mac.QueryFrontmostSession());

            Assert.Same(MacTerminalDriver.ITerm2, mac.PreferredDriver);
        }

        // ---------------------------------------------------------------------------------------
        // Gestures that name no session follow the keys
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Pressing_an_iTerm2_session_key_moves_navigation_there_at_once()
        {
            // Without waiting for the next probe: the press IS the freshest signal of where the
            // user is working.
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls);
            mac.DiscoverSessions();
            mac.FocusSession("ttys004");
            calls.Clear();

            mac.Navigate(TerminalAction.NextTab);
            Assert.Contains("tell application \"iTerm2\" to activate", Script(calls.Last()));
            Assert.Contains("key code 30 using {command down, shift down}", Script(calls.Last()));   // Cmd+Shift+] — iTerm2's Select Next Tab

            mac.Navigate(TerminalAction.PreviousTab);
            Assert.Contains("key code 33 using {command down, shift down}", Script(calls.Last()));   // Cmd+Shift+[

            // And back: a press on the Terminal.app session moves it again.
            mac.InjectKey("ttys000", KeyStroke.Return);
            mac.Navigate(TerminalAction.NextTab);
            Assert.Contains("tell application \"Terminal\" to activate", Script(calls.Last()));
            Assert.Contains("key code 48 using {control down}", Script(calls.Last()));
        }

        [Fact]
        public void A_focus_that_did_not_land_does_not_move_navigation()
        {
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls, args => Script(args).Contains("targetTty") ? "tab-missing" : "ok");
            mac.DiscoverSessions();

            mac.FocusSession("ttys004");

            Assert.Same(MacTerminalDriver.Terminal, mac.PreferredDriver);
        }

        [Fact]
        public void Every_navigation_action_has_an_iTerm2_script()
        {
            // The same guard MacPlatformBridgeTests keeps for Terminal.app: a TerminalAction with
            // no iTerm2 case would silently do nothing on the hardware once iTerm2 is current.
            foreach (var action in Enum.GetValues<TerminalAction>())
            {
                var calls = new List<List<String>>();
                var mac = Bridge(MixedListing, calls);
                mac.DiscoverSessions();
                mac.FocusSession("ttys004");
                calls.Clear();

                mac.Navigate(action);

                var script = Script(Assert.Single(calls));
                Assert.Contains("iTerm2", script);
                Assert.DoesNotContain("\"Terminal\"", script);
            }
        }

        [Theory]
        [InlineData(TerminalAction.NewClaudeTab, "create tab with default profile")]
        [InlineData(TerminalAction.NewClaudeWindow, "create window with default profile")]
        public void Starting_a_session_in_iTerm2_writes_the_command_to_the_new_session(TerminalAction action, String creates)
        {
            // iTerm2's `write text` is its `do script`: the command reaches the new session's pty
            // atomically, and it travels as an osascript argument (it may carry a cd, #113).
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls);
            mac.DiscoverSessions();
            mac.FocusSession("ttys004");
            calls.Clear();

            mac.Navigate(action, "/Users/me/Work/O'Brien's app");

            var call = Assert.Single(calls);
            Assert.Contains("set shellCommand to item 1 of argv", Script(call));
            Assert.Contains(creates, Script(call));
            Assert.Contains("write text shellCommand", Script(call));
            Assert.DoesNotContain("do script", Script(call));
            Assert.Equal("cd '/Users/me/Work/O'\"'\"'Brien'\"'\"'s app' && claude", call[2]);
        }

        [Fact]
        public void A_new_iTerm2_tab_with_no_window_open_creates_a_window_instead()
        {
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls);
            mac.DiscoverSessions();
            mac.FocusSession("ttys004");
            calls.Clear();

            mac.Navigate(TerminalAction.NewClaudeTab);

            var script = Script(Assert.Single(calls));
            Assert.Contains("if (count of windows) is 0 then", script);
            Assert.Contains("create window with default profile", script);
        }

        [Fact]
        public void Opening_a_project_in_iTerm2_never_types_into_a_busy_session()
        {
            // The idle test is `is at shell prompt`, which is only ever true with iTerm2's shell
            // integration installed. Without it the launch always opens a new tab: the safe side.
            var calls = new List<List<String>>();
            var mac = Bridge(MixedListing, calls);
            mac.DiscoverSessions();
            mac.FocusSession("ttys004");
            calls.Clear();

            mac.LaunchClaudeInProject("/Users/me/my project");

            var call = Assert.Single(calls);
            var script = Script(call);
            Assert.Contains("set isIdle to (is at shell prompt of current session of current tab of current window)", script);
            Assert.Contains("create tab with default profile", script);
            Assert.DoesNotContain("/Users/me/my project", script);
            Assert.Equal("cd '/Users/me/my project' && claude", call[2]);
        }

        [Fact]
        public void A_screenshot_launch_follows_the_same_terminal()
        {
            var calls = new List<List<String>>();
            var codex = new MacPlatformBridge(AgentProcessMatcher.ClaudeCode, cliCommand: "codex")
            {
                PsRunner = () => MixedListing,
                OsascriptRunner = (args, timeout, wantOutput) => { calls.Add(args); return "ok"; },
                // The codex scan asks each live process for its folder; these pids are not real.
                ProcessDirectoryReader = _ => null,
                ProcessStartReader = _ => null,
            };
            codex.DiscoverSessions();
            codex.FocusSession("ttys004");
            calls.Clear();

            codex.LaunchAgentSession(new[] { "-i", "/tmp/shot 1.png" });

            var call = Assert.Single(calls);
            Assert.Contains("tell application \"iTerm2\"", Script(call));
            Assert.Equal("codex '-i' '/tmp/shot 1.png'", call[2]);
        }

        // ---------------------------------------------------------------------------------------
        // The drivers themselves
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal", "Terminal.app")]
        [InlineData("/Applications/iTerm.app/Contents/MacOS/iTerm2", "iTerm2")]
        [InlineData("/Users/me/Applications/iTerm.app/Contents/MacOS/iTerm2 -psn_0_123", "iTerm2")]   // a user-folder install, with args
        [InlineData("/Applications/Ghostty.app/Contents/MacOS/ghostty", null)]
        [InlineData("/Applications/Warp.app/Contents/MacOS/stable", null)]
        [InlineData("/Applications/Visual Studio Code.app/Contents/MacOS/Electron", null)]
        [InlineData(null, null)]
        public void A_driver_is_recognised_by_its_bundle_path(String owner, String driverName)
        {
            Assert.Equal(driverName, MacTerminalDriver.For(owner)?.Name);
            Assert.Equal(driverName != null, MacTerminalDriver.IsDrivable(owner));
        }

        [Fact]
        public void Each_driver_has_its_own_focus_guard_and_launch_scripts()
        {
            foreach (var driver in MacTerminalDriver.All)
            {
                var app = "tell application \"" + driver.ScriptName + "\"";
                Assert.StartsWith("on run argv\nset targetTty to item 1 of argv\n", driver.FocusGuardScript);
                Assert.Contains("if application \"" + driver.ScriptName + "\" is not running then", driver.FocusGuardScript);
                Assert.EndsWith("delay 0.05\n", driver.FocusGuardScript);
                Assert.Contains("if application \"" + driver.ScriptName + "\" is running then", driver.FrontmostQuery);
                Assert.Contains(driver.ScriptName + "|", driver.FrontmostQuery);
                foreach (var launch in new[] { driver.NewAgentTabScript, driver.NewAgentWindowScript, driver.LaunchShellCommandScript })
                {
                    Assert.StartsWith("on run argv\nset shellCommand to item 1 of argv\n", launch);
                    Assert.Contains(app, launch);
                    Assert.EndsWith("end run", launch);
                }
            }
        }

        [Fact]
        public void The_probe_names_drivers_the_bridge_can_look_up()
        {
            Assert.Same(MacTerminalDriver.Terminal, MacTerminalDriver.Named("Terminal"));
            Assert.Same(MacTerminalDriver.ITerm2, MacTerminalDriver.Named("iTerm2"));
            Assert.Null(MacTerminalDriver.Named("Ghostty"));
        }
    }
}
