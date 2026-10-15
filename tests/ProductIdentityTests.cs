namespace Loupedeck.ClaudeConsolePlugin.Tests
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text.Json.Nodes;
    using System.Text.RegularExpressions;

    using Xunit;

    /// <summary>
    /// The products' INSTALL CONTRACTS — every identifier an existing install depends on — pinned
    /// so that a display-name change can never become an identity change by accident.
    ///
    /// Claude Console became Vizhi for Claude Code in 2.4.0 the way Vizhi Desktop became Vizhi for
    /// ChatGPT: the name users see changed, nothing they installed did. What an install depends on:
    ///   • the plugin's internal name and the shared namespace — both sit inside EVERY key binding
    ///     of every imported layout ("$ClaudeConsole___Loupedeck.ClaudeConsolePlugin.Actions.…");
    ///     change either and every key silently does nothing after the update;
    ///   • the runtime home under ~/.claude/claude-console/, which users' settings.json hooks point
    ///     at, the hook/helper executable names, and the settings backup suffix;
    ///   • the voice helper's bundle id, which holds the Microphone grant;
    ///   • each layout's GUID, which is what lets a re-import replace rather than duplicate.
    /// </summary>
    public class ProductIdentityTests
    {
        private static String RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !(Directory.Exists(Path.Combine(dir.FullName, "profiles")) && Directory.Exists(Path.Combine(dir.FullName, "src"))))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (profiles/ + src/) not found above " + AppContext.BaseDirectory);
        }

        private static String RepoFile(params String[] parts) => Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());

        private static String ZipText(String lp5, String entry)
        {
            using var zip = ZipFile.OpenRead(lp5);
            using var reader = new StreamReader(zip.GetEntry(entry).Open());
            return reader.ReadToEnd();
        }

        // ---------------------------------------------------------------------------------------
        // The package
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("ClaudeConsole", "Vizhi for Claude Code", "ClaudeConsolePlugin")]
        [InlineData("VizhiCodex", "Vizhi for Codex", "VizhiCodexPlugin")]
        public void The_internal_name_is_fixed_and_the_display_name_is_the_products(String product, String display, String assembly)
        {
            var yaml = File.ReadAllText(RepoFile("src", "Products", product, "package", "metadata", "LoupedeckPackage.yaml"));
            Assert.Matches($@"(?m)^name: {product}\s*$", yaml);
            Assert.Matches($@"(?m)^displayName: {Regex.Escape(display)}\s*$", yaml);

            // Options+ reads the display name from the configuration too; the two must agree.
            var configuration = File.ReadAllText(RepoFile("src", "Products", product, "PluginConfiguration.xml"));
            Assert.Contains($"displayName=\"{display}\"", configuration);

            // One namespace for every product: it is part of every action id in every layout.
            var csproj = File.ReadAllText(RepoFile("src", "Products", product, product + "Plugin.csproj"));
            Assert.Contains("<RootNamespace>Loupedeck.ClaudeConsolePlugin</RootNamespace>", csproj);
            var assemblyName = Regex.Match(csproj, "<AssemblyName>(.*?)</AssemblyName>");
            Assert.Equal(assembly, assemblyName.Success ? assemblyName.Groups[1].Value : product + "Plugin");
        }

        [Theory]
        [InlineData("ClaudeConsole", "Vizhi for Claude Code")]
        [InlineData("VizhiCodex", "Vizhi for Codex")]
        public void The_plugin_declares_its_display_name_to_the_engine_through_the_logger(String product, String display)
        {
            // The engine names the product only through PluginLog.ProductName (the settings.json
            // notices, the on-screen dialog, the private-file errors), so the constructor's Init
            // call IS the product's name.
            var plugin = File.ReadAllText(RepoFile("src", "Products", product, product + "Plugin.cs"));
            Assert.Contains($"PluginLog.Init(this.Log, \"{display}\")", plugin);
        }

        [Fact]
        public void The_engine_carries_no_product_name_of_its_own()
        {
            // Owner's rule (PR #104): identity comes from the product. A literal in Core would also
            // be the one place a rename could miss.
            foreach (var file in Directory.GetFiles(RepoFile("src", "Core"), "*.cs", SearchOption.AllDirectories))
            {
                var code = File.ReadAllText(file);
                Assert.DoesNotContain("\"Claude Console", code);
                Assert.DoesNotContain("\"Vizhi for Claude Code", code);
                Assert.DoesNotContain("\"Vizhi for Codex", code);
            }
        }

        // ---------------------------------------------------------------------------------------
        // The layouts
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Every_layout_binding_names_its_plugin_and_the_shared_namespace()
        {
            var layouts = Directory.GetFiles(RepoFile("profiles"), "*.lp5");
            Assert.NotEmpty(layouts);
            foreach (var lp5 in layouts)
            {
                // The download is named after the PRODUCT; the bindings inside name the PLUGIN,
                // whose internal name is the install contract (tools/profile_names.py has the map).
                var plugin = Path.GetFileName(lp5).Split('-')[0] switch
                {
                    "VizhiClaudeCode" => "ClaudeConsole",
                    "VizhiCodex" => "VizhiCodex",
                    var other => throw new Xunit.Sdk.XunitException($"{Path.GetFileName(lp5)}: unknown product prefix '{other}' — name it <Product>-<Host>.lp5"),
                };
                var prefix = $"${plugin}___Loupedeck.ClaudeConsolePlugin.Actions.";
                var profile = JsonNode.Parse(ZipText(lp5, "ProfileInfo.json"));
                var bound = 0;
                foreach (var page in (JsonArray)profile["layout"]["layoutModes"][0]["workspaces"][0]["pressPages"])
                {
                    foreach (var control in (JsonArray)page["controls"])
                    {
                        var action = (String)control["pressAction"];
                        if (String.IsNullOrEmpty(action))
                        {
                            continue;
                        }

                        Assert.StartsWith(prefix, action);
                        bound++;
                    }
                }

                Assert.True(bound > 0, Path.GetFileName(lp5) + " binds no keys");
            }
        }

        [Theory]
        // <Product>-<Host>.lp5 reads "<Product> — <Host>"; the GUIDs are the ones the files carried
        // under their pre-2.4.0 names (ClaudeConsole-Keypad / -Windows / -iTerm2, VizhiCodex-…).
        [InlineData("VizhiClaudeCode-Terminal.lp5", "4146981DFFCD4C4EBCCE554873013DE8", "Vizhi for Claude Code — Terminal")]
        [InlineData("VizhiClaudeCode-iTerm2.lp5", "01F94DE125BF41D9BF030BD7B7555357", "Vizhi for Claude Code — iTerm2")]
        [InlineData("VizhiClaudeCode-WindowsTerminal.lp5", "9556D49413DC49B1968CA44D3C3C3303", "Vizhi for Claude Code — Windows Terminal")]
        [InlineData("VizhiCodex-Terminal.lp5", "7B2C4E9A15D8436FA0C3E17D5B84962F", "Vizhi for Codex — Terminal")]
        [InlineData("VizhiCodex-iTerm2.lp5", "3C9F9D52FBDF49D396AA1F2D4008694C", "Vizhi for Codex — iTerm2")]
        [InlineData("VizhiCodex-WindowsTerminal.lp5", "90CC0B6D7FE0405AB63B311DCA7476B7", "Vizhi for Codex — Windows Terminal")]
        public void A_layouts_guid_never_changes_and_its_display_name_is_the_products(String file, String guid, String display)
        {
            var lp5 = RepoFile("profiles", file);
            var profile = JsonNode.Parse(ZipText(lp5, "ProfileInfo.json"));
            Assert.Equal(guid, (String)profile["name"]);
            Assert.Equal(guid, (String)profile["packageName"]);
            Assert.Equal(display, (String)profile["displayName"]);

            var application = JsonNode.Parse(ZipText(lp5, "ApplicationInfo.json"));
            Assert.Equal(guid, (String)application["defaultProfileName"]);

            var yaml = ZipText(lp5, "metadata/LoupedeckPackage.yaml");
            Assert.Matches($@"(?m)^name: {guid}\s*$", yaml);
            Assert.Matches($@"(?m)^displayName: {Regex.Escape(display)}\s*$", yaml);
        }

        // ---------------------------------------------------------------------------------------
        // What is on users' disks
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void The_on_disk_names_keep_their_claude_console_spelling()
        {
            // Pinned as source text, the way AgentBridgeStatusTests pins the log tag: these literals
            // ARE the contract. Each one is a path or a name that an existing install already uses.
            var bridge = File.ReadAllText(RepoFile("src", "Core", "BridgeManager.cs"));
            Assert.Contains("Path.Combine(ClaudeDir, \"claude-console\")", bridge);          // the runtime home
            Assert.Contains("\"claude-console-hook.exe\"", bridge);                            // the Windows hook helper

            var settings = File.ReadAllText(RepoFile("src", "Core", "ClaudeSettingsStore.cs"));
            Assert.Contains("\".claude-console.bak\"", settings);                              // the rolling backup

            var wiring = File.ReadAllText(RepoFile("src", "Core", "Platform", "BridgeWiring.cs"));
            Assert.Contains("WindowsMarker = \"claude-console-hook\"", wiring);                // how our hooks are recognised

            var roots = File.ReadAllText(RepoFile("src", "Core", "ProjectDiscovery.cs"));
            Assert.Contains("\"claude-console\", \"project-roots\"", roots);                  // the user's roots file

            var helper = File.ReadAllText(RepoFile("tools", "voice", "Info.plist"));
            Assert.Contains("com.rshankar.claudeconsole.voicehelper", helper);                // the Microphone grant

            // The helper built at pack time takes its name and bundle id from this table: the name
            // follows the product, the id never changes (it is what the Microphone grant is keyed on).
            var table = File.ReadAllText(RepoFile("tools", "voice", "helper-metadata.py"));
            Assert.Contains("\"ClaudeConsole\": (\"Vizhi for Claude Code\", \"com.rshankar.claudeconsole.voicehelper\")", table);
            Assert.Contains("\"VizhiCodex\": (\"Vizhi for Codex\", \"com.rshankar.vizhicodex.voicehelper\")", table);
        }
    }
}
