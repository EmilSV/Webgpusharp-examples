namespace Setup;

public static class SpanExtension
{
    public static unsafe nuint GetByteLength<T>(this Span<T> span)
        where T : unmanaged
    {
        return (nuint)span.Length * (nuint)sizeof(T);
    }
}