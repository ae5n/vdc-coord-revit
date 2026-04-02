using System.Diagnostics;
using System.Reflection;

namespace RevitSuite.Host.Versioning
{
    internal static class ProductVersionInfo
    {
        public static string ProductName => "RevitSuite";

        public static string DisplayVersion
        {
            get
            {
                var assembly = Assembly.GetExecutingAssembly();
                var informationalVersion = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informationalVersion))
                {
                    var plusIndex = informationalVersion.IndexOf('+');
                    return plusIndex >= 0
                        ? informationalVersion.Substring(0, plusIndex)
                        : informationalVersion;
                }

                return FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion ?? "unknown";
            }
        }
    }
}
