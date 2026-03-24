using Newtonsoft.Json;

namespace RevitSuite.Mcp.Configuration
{
    public class ServiceSettings
    {
        [JsonProperty("logLevel")]
        public string LogLevel { get; set; } = "Info";

        [JsonProperty("port")]
        public int Port { get; set; } = 8080;
    }
}
