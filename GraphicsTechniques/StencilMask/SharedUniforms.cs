using System.Numerics;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct SharedUniforms
{
    public Matrix4x4 ViewProjection;
    public Vector3 LightDirection;
    private readonly float _pad0;
}
