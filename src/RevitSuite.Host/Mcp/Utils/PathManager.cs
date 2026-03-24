using Newtonsoft.Json;
using System;
using System.IO;

namespace RevitSuite.Mcp.Utils
{
    public static class PathManager
    {
        public static string GetAppDataDirectoryPath()
        {
            string applicationPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            return Path.GetDirectoryName(applicationPath);
        }

        public static string GetCommandsDirectoryPath()
        {
            string commandsDirectory = Path.Combine(GetAppDataDirectoryPath(), "Commands");
            EnsureDirectoryExists(commandsDirectory);
            return commandsDirectory;
        }

        public static string GetLogsDirectoryPath()
        {
            string logsDirectory = Path.Combine(GetAppDataDirectoryPath(), "Logs");
            EnsureDirectoryExists(logsDirectory);
            return logsDirectory;
        }

        public static string GetCommandRegistryFilePath(bool createIfNotExists = true)
        {
            string registryFilePath = Path.Combine(GetCommandsDirectoryPath(), "commandRegistry.json");

            if (createIfNotExists && !File.Exists(registryFilePath))
                CreateDefaultCommandRegistryFile(registryFilePath);

            return registryFilePath;
        }

        private static void CreateDefaultCommandRegistryFile(string filePath)
        {
            try
            {
                var defaultRegistry = new { commands = new object[] { } };
                File.WriteAllText(filePath, JsonConvert.SerializeObject(defaultRegistry, Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating default command registry file: {ex.Message}");
            }
        }

        private static void EnsureDirectoryExists(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                Directory.CreateDirectory(directoryPath);
        }
    }
}
