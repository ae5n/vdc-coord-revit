using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitSuite.Host.Explorer
{
    internal static class ExplorerPaths
    {
        private static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitSuite",
            "Explorer");

        public static string FiltersDirectory => EnsureDirectory(Path.Combine(Root, "filters"));
        public static string RulesDirectory => EnsureDirectory(Path.Combine(Root, "rules"));
        public static string WarningRankingsFile => Path.Combine(EnsureDirectory(Root), "warning-rankings.json");

        public static string WarningSnapshotsDirectory(string modelIdentity) =>
            EnsureDirectory(Path.Combine(Root, "snapshots", "warnings", modelIdentity));

        public static string AuditSnapshotsDirectory(string modelIdentity) =>
            EnsureDirectory(Path.Combine(Root, "snapshots", "audit", modelIdentity));

        /// <summary>
        /// Stable, filesystem-safe identity for a model so snapshots of different projects
        /// never mix. Hash of the central/user path when available, else the title.
        /// </summary>
        public static string GetModelIdentity(Document document)
        {
            var source = string.IsNullOrWhiteSpace(document.PathName) ? document.Title : document.PathName;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source.ToLowerInvariant()));
            var builder = new StringBuilder(16);
            for (var i = 0; i < 8; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        private static string EnsureDirectory(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
