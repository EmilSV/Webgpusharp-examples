using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct FormattedTextStorage
{
    public Matrix4x4 Transform;
    public Vector4 Color;
    public float Scale;
    private readonly InlineArray3<float> _pad0;
}