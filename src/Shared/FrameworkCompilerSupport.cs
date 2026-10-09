#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace System
{
    internal static class StringComparisonExtensions
    {
        internal static bool Contains(this string text, string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;
        internal static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;
    }
    internal static class MathF
    {
        internal const float PI = (float)Math.PI;
        internal static float Exp(float value) => (float)Math.Exp(value);
        internal static float Pow(float value, float power) => (float)Math.Pow(value, power);
        internal static float Log10(float value) => (float)Math.Log10(value);
        internal static float Sin(float value) => (float)Math.Sin(value);
    }
}
#endif
