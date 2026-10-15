namespace Loupedeck.ClaudeConsolePlugin
{
    using System;

    // A helper class that enables logging from the plugin code.

    internal static class PluginLog
    {
        private static PluginLogFile _pluginLogFile;
        private static String _productName = "Plugin";

        public static void Init(PluginLogFile pluginLogFile, String productName)
        {
            pluginLogFile.CheckNullArgument(nameof(pluginLogFile));
            productName.CheckNullArgument(nameof(productName));
            PluginLog._pluginLogFile = pluginLogFile;
            PluginLog._productName = productName;
        }

        /// <summary>
        /// The product's display name, as its plugin constructor declared it (Vizhi for Claude
        /// Code, Vizhi for Codex). The engine's user-facing text — the settings.json notices, the
        /// on-screen Turn on dialog, the private-file errors — names the product through this, so
        /// src/Core carries no product name of its own (the rule PR #104 set) and a rename is one
        /// string in the product.
        /// </summary>
        internal static String ProductName => PluginLog._productName;

        internal static String Format(String productName, String text) => $"[{productName}] {text}";

        private static String Format(String text) => Format(PluginLog._productName, text);

        public static void Verbose(String text) => PluginLog._pluginLogFile?.Verbose(Format(text));

        public static void Verbose(Exception ex, String text) => PluginLog._pluginLogFile?.Verbose(ex, Format(text));

        public static void Info(String text) => PluginLog._pluginLogFile?.Info(Format(text));

        public static void Info(Exception ex, String text) => PluginLog._pluginLogFile?.Info(ex, Format(text));

        public static void Warning(String text) => PluginLog._pluginLogFile?.Warning(Format(text));

        public static void Warning(Exception ex, String text) => PluginLog._pluginLogFile?.Warning(ex, Format(text));

        public static void Error(String text) => PluginLog._pluginLogFile?.Error(Format(text));

        public static void Error(Exception ex, String text) => PluginLog._pluginLogFile?.Error(ex, Format(text));
    }
}
