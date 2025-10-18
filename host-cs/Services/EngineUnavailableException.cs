using System;

namespace RevitSuite.Host.Services
{
    public class EngineUnavailableException : Exception
    {
        public EngineUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
