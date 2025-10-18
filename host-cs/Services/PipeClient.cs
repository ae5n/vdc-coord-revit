using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Services
{
    public class PipeClient
    {
        private readonly string _pipeName;
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _responseTimeout;
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public PipeClient(string pipeName, TimeSpan? timeout = null, TimeSpan? responseTimeout = null)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _timeout = timeout ?? TimeSpan.FromSeconds(2);
            _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(60);
        }

        public T? Call<T>(string method, object payload, string? correlationId = null)
        {
            correlationId ??= Guid.NewGuid().ToString("N");

            try
            {
                using (var client = new NamedPipeClientStream(
                           ".",
                           _pipeName,
                           PipeDirection.InOut,
                           PipeOptions.Asynchronous))
                {
                    LogManager.Info(correlationId, $"Connecting to pipe '{_pipeName}' for method '{method}'.");
                    client.Connect((int)_timeout.TotalMilliseconds);
                    client.ReadMode = PipeTransmissionMode.Message;

                    var request = new PipeRequest
                    {
                        Method = method,
                        Payload = payload,
                        CorrelationId = correlationId
                    };

                    var requestJson = JsonConvert.SerializeObject(request, SerializerSettings);
                    var requestBytes = Encoding.UTF8.GetBytes(requestJson);
                    client.Write(requestBytes, 0, requestBytes.Length);
                    client.Flush();
                    LogManager.Info(correlationId, $"Request sent: {requestJson}");

                    var responseJson = ReadMessageWithTimeout(client, _responseTimeout, correlationId);
                    LogManager.Info(correlationId, $"Response received: {responseJson}");

                    var response = JsonConvert.DeserializeObject<PipeResponseEnvelope>(responseJson);
                    if (response == null)
                    {
                        throw new InvalidOperationException("Engine returned malformed JSON.");
                    }

                    if (response.Ok)
                    {
                        if (response.Result == null || response.Result.Type == JTokenType.Null)
                        {
                            return default;
                        }

                        return response.Result.ToObject<T>();
                    }

                    var errorMessage = string.IsNullOrWhiteSpace(response.Error)
                        ? "Unknown error"
                        : response.Error!;

                    throw new InvalidOperationException("Engine error: " + errorMessage);
                }
            }
            catch (TimeoutException tex)
            {
                LogManager.Error(correlationId, "Pipe connection timed out.", tex);
                throw new EngineUnavailableException(GetOfflineMessage(), tex);
            }
            catch (IOException ioex) when (ioex.Message.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LogManager.Error(correlationId, "Pipe IO failure.", ioex);
                throw new EngineUnavailableException(GetOfflineMessage(), ioex);
            }
            catch (Exception ex)
            {
                LogManager.Error(correlationId, $"Unhandled pipe exception for method '{method}'.", ex);
                throw;
            }
        }

        private string GetOfflineMessage() =>
            $"Unable to reach the RevitSuite engine (pipe '{_pipeName}'). Start it via VS Code task 'engine:run' or run 'python engine-py/server.py'.";

        private static string ReadMessageWithTimeout(NamedPipeClientStream client, TimeSpan timeout, string correlationId)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            var deadline = DateTime.UtcNow + timeout;

            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    var timeoutMessage =
                        $"Engine did not respond within {timeout.TotalSeconds:N0} seconds. " +
                        "Ensure the Python engine window is running and responsive.";
                    throw new EngineUnavailableException(timeoutMessage, new TimeoutException(timeoutMessage));
                }

                var readTask = client.ReadAsync(buffer, 0, buffer.Length);
                if (!readTask.Wait(remaining))
                {
                    var timeoutMessage =
                        $"Engine did not respond within {timeout.TotalSeconds:N0} seconds. " +
                        "Ensure the Python engine window is running and responsive.";
                    throw new EngineUnavailableException(timeoutMessage, new TimeoutException(timeoutMessage));
                }

                var bytesRead = readTask.Result;
                if (bytesRead == 0)
                {
                    throw new InvalidOperationException("No response from engine.");
                }

                ms.Write(buffer, 0, bytesRead);

                if (client.IsMessageComplete)
                {
                    break;
                }
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private class PipeRequest
        {
            [JsonProperty("method")]
            public string Method { get; set; } = string.Empty;

            [JsonProperty("payload")]
            public object? Payload { get; set; }

            [JsonProperty("correlationId")]
            public string CorrelationId { get; set; } = string.Empty;
        }

        private class PipeResponseEnvelope
        {
            [JsonProperty("ok")]
            public bool Ok { get; set; }

            [JsonProperty("result")]
            public JToken? Result { get; set; }

            [JsonProperty("error")]
            public string? Error { get; set; }

            [JsonProperty("traceback")]
            public string? Traceback { get; set; }
        }
    }
}
