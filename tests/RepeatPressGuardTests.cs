namespace Loupedeck.ClaudeConsolePlugin.Tests
{
    using System;

    using Loupedeck.ClaudeConsolePlugin.Platform;

    using Xunit;

    /// <summary>
    /// #89: three short presses on one typing key within 250 ms each spawned their own injection,
    /// and the console interleaved them into "/cost/cost/cost" with a single Return. The same text
    /// pressed again inside the repeat window is dropped; anything else goes through untouched.
    /// The clock is injected so no test sleeps.
    /// </summary>
    public class RepeatPressGuardTests
    {
        private static (BridgeManager Bridge, PlatformSeamTests.FakePlatformBridge Fake, Func<Int64> Now) Rig()
        {
            var fake = new PlatformSeamTests.FakePlatformBridge();
            var now = 10_000L;
            var bridge = new BridgeManager(fake) { ActiveTty = "ttys001", RepeatClock = () => now };
            return (bridge, fake, () => now);
        }

        [Fact]
        public void The_same_text_pressed_three_times_at_once_is_typed_once()
        {
            var (bridge, fake, _) = Rig();

            bridge.SendPrompt("/cost");
            bridge.SendPrompt("/cost");
            bridge.SendPrompt("/cost");

            var call = Assert.Single(fake.Texts);
            Assert.Equal("/cost", call.Text);
            Assert.True(call.Enter);
        }

        [Fact]
        public void Different_text_is_never_held_back()
        {
            var (bridge, fake, _) = Rig();

            bridge.SendPrompt("/cost");
            bridge.SendPrompt("/context");

            Assert.Equal(2, fake.Texts.Count);
        }

        /// <summary>A draft and a send of the same words are two different requests.</summary>
        [Fact]
        public void The_same_words_as_a_draft_and_then_as_a_send_are_both_typed()
        {
            var (bridge, fake, _) = Rig();

            bridge.InjectText("fix the bug", pressEnter: false);
            bridge.InjectText("fix the bug", pressEnter: true);

            Assert.Equal(2, fake.Texts.Count);
        }

        [Fact]
        public void The_same_text_once_the_window_has_passed_is_typed_again()
        {
            var fake = new PlatformSeamTests.FakePlatformBridge();
            var now = 10_000L;
            var bridge = new BridgeManager(fake) { ActiveTty = "ttys001", RepeatClock = () => now };

            bridge.SendPrompt("/cost");
            now += bridge.RepeatPressWindowMs;          // exactly at the boundary counts as elapsed
            bridge.SendPrompt("/cost");

            Assert.Equal(2, fake.Texts.Count);
        }

        /// <summary>
        /// A dropped repeat must not restart the window, or a user who pressed twice by accident
        /// and then meant the third press would have it swallowed too.
        /// </summary>
        [Fact]
        public void A_dropped_repeat_does_not_extend_the_window()
        {
            var fake = new PlatformSeamTests.FakePlatformBridge();
            var now = 10_000L;
            var bridge = new BridgeManager(fake) { ActiveTty = "ttys001", RepeatClock = () => now };

            bridge.SendPrompt("/cost");      // lands
            now += 200;
            bridge.SendPrompt("/cost");      // dropped: 200 ms after the one that landed
            now += 200;
            bridge.SendPrompt("/cost");      // lands: 400 ms after the one that landed

            Assert.Equal(2, fake.Texts.Count);
        }

        /// <summary>Two Escapes in a row are a real gesture; keys are not text and are not guarded.</summary>
        [Fact]
        public void Keys_are_not_guarded()
        {
            var (bridge, fake, _) = Rig();

            bridge.InjectKey(KeyStroke.Escape);
            bridge.InjectKey(KeyStroke.Escape);

            Assert.Equal(2, fake.Keys.Count);
        }

        [Fact]
        public void A_zero_window_turns_the_guard_off()
        {
            var (bridge, fake, _) = Rig();
            bridge.RepeatPressWindowMs = 0;

            bridge.SendPrompt("/cost");
            bridge.SendPrompt("/cost");

            Assert.Equal(2, fake.Texts.Count);
        }
    }
}
