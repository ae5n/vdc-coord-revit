using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;

namespace RevitSuite.Mcp.Configuration
{
    public class CommandConfig
    {
        [JsonProperty("commandName")]
        public string CommandName { get; set; }

        [JsonProperty("assemblyPath")]
        public string AssemblyPath { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("supportedRevitVersions")]
        public string[] SupportedRevitVersions { get; set; } = new string[0];

        [JsonProperty("developer")]
        public DeveloperInfo Developer { get; set; } = new DeveloperInfo();

        [JsonProperty("description")]
        public string Description { get; set; } = "";
    }
}
