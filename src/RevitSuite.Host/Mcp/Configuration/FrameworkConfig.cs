using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitSuite.Mcp.Configuration
{
    public class FrameworkConfig
    {
        [JsonProperty("commands")]
        public List<CommandConfig> Commands { get; set; } = new List<CommandConfig>();

        [JsonProperty("settings")]
        public ServiceSettings Settings { get; set; } = new ServiceSettings();
    }
}
