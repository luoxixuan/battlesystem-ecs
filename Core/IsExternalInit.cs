// Polyfill for init-only properties on netstandard2.1
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
