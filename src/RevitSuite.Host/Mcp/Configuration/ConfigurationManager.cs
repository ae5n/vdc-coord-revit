using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using RevitSuite.Mcp.Utils;
using System;
using System.IO;

namespace RevitSuite.Mcp.Configuration
{
    public class ConfigurationManager
    {
        private readonly ILogger _logger;
        private readonly string _configPath;
        private DateTime _lastConfigLoadTime;

        public FrameworkConfig Config { get; private set; }

        public ConfigurationManager(ILogger logger)
        {
            _logger = logger;
            _configPath = PathManager.GetCommandRegistryFilePath();
        }

        public void LoadConfiguration()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    Config = JsonConvert.DeserializeObject<FrameworkConfig>(json);
                    _logger.Info("Configuration file loaded: {0}", _configPath);
                }
                else
                {
                    _logger.Error("No configuration file found.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to load configuration file: {0}", ex.Message);
            }

            _lastConfigLoadTime = DateTime.Now;
        }
    }
}
