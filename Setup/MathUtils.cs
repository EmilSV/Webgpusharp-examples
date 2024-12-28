using System.Numerics;

namespace Setup;

public static class MathUtils
{
    public static ulong RoundToNextMultipleOfFour(ulong value)
    {
        return (value + 3ul) & ~0x03ul;
    }
}
