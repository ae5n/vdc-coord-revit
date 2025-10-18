using System;
using System.IO;
using System.Threading;

namespace RevitSuite.Host.Logging
{
    internal static class LogManager
    {
        private static readonly object Sync = new object();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitSuite",
            "logs");

        private static string LogFilePath =>
            Path.Combine(LogDirectory, $"host-{DateTime.UtcNow:yyyyMMdd}.log");

        public static void Info(string correlationId, string message) =>
            Write("INFO", correlationId, message);

        public static void Warn(string correlationId, string message) =>
            Write("WARN", correlationId, message);

        public static void Error(string correlationId, string message, Exception? exception = null) =>
            Write("ERROR", correlationId, message, exception);

        private static void Write(string level, string correlationId, string message, Exception? exception = null)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var payload = $"{DateTime.UtcNow:o} [{level}] ({correlationId}) {message}";
                if (exception != null)
                {
                    payload += Environment.NewLine + exception;
                }

                lock (Sync)
                {
                    File.AppendAllText(LogFilePath, payload + Environment.NewLine);
                }
            }
            catch
            {
                // Deliberately swallow logging failures to avoid impacting Revit.
                Thread.Sleep(0);
            }
        }
    }
}
