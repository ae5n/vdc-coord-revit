#if !NET5_0_OR_GREATER
// Enables C# 9 records/init-only setters when targeting .NET Framework 4.8.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
