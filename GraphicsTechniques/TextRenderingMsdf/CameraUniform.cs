using System.Numerics;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct CameraUniform
{
    public Matrix4x4 Projection;
    public Matrix4x4 View;
}