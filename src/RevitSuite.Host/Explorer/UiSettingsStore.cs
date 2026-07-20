using System;
using System.IO;
using Newtonsoft.Json;

namespace RevitSuite.Host.Explorer
{
    /// <summary>Remembers window placement and last-used Explore options between sessions.</summary>
    internal sealed class ExplorerUiSettings
    {
        /// <summary>Bumped when a saved setting needs a one-time migration (see Load).</summary>
        public const int CurrentVersion = 5;

        public int SettingsVersion { get; set; }
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public int ScopeIndex { get; set; }
        public int GroupingIndex { get; set; }
        public bool IncludeLinks { get; set; } = true;
        public bool IncludeUncategorized { get; set; }

        private static string FilePath => Path.Combine(
            Path.GetDirectoryName(ExplorerPaths.WarningRankingsFile)!,
            "ui-settings.json");

        public static ExplorerUiSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var settings = JsonConvert.DeserializeObject<ExplorerUiSettings>(File.ReadAllText(FilePath))
                                   ?? new ExplorerUiSettings();

                    // v1 files silently persisted "Include linked models" off, which read as a
                    // broken feature. One-time migration turns it back on; from v2 onward an
                    // explicit user choice sticks.
                    if (settings.SettingsVersion < 2)
                    {
                        settings.IncludeLinks = true;
                    }

                    // v3: Active View became the default scope (hidden indicators live there).
                    // One-time switch; from v3 onward the user's scope choice sticks.
                    if (settings.SettingsVersion < 3)
                    {
                        settings.ScopeIndex = 1;
                    }

                    // v4/v5: the default window height changed (v4 shrank it, v5 settled on
                    // 700). Drop the remembered height once so the new default applies;
                    // explicit resizes from then on stick.
                    if (settings.SettingsVersion < 5)
                    {
                        settings.WindowHeight = null;
                        settings.SettingsVersion = CurrentVersion;
                    }

                    return settings;
                }
            }
            catch
            {
                // Corrupt settings fall back to defaults.
            }

            return new ExplorerUiSettings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch
            {
                // Never let settings persistence break window close.
            }
        }
    }
}
