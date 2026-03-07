using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct TriangleIndices
{
    public uint A;
    public uint B;
    public uint C;
}