using System.Runtime.CompilerServices;

namespace Setup;

public static class ReadonlyListExtension
{
    public static ulong GetSizeInBytes<T>(this IReadOnlyList<T> list) where T : struct
    {
        return (ulong)(list.Count * Unsafe.SizeOf<T>());
    }
}