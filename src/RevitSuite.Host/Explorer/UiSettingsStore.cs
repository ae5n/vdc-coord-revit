using System;
using System.IO;
using Newtonsoft.Json;

namespace RevitSuite.Host.Explorer
{
    /// <summary>Remembers window placement and last-used Explore options between sessions.</summary>
    internal sealed class ExplorerUiSettings
    {
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
                    return JsonConvert.DeserializeObject<ExplorerUiSettings>(File.ReadAllText(FilePath))
                           ?? new ExplorerUiSettings();
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
