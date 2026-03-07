using System.Numerics;
using System.Runtime.InteropServices;

namespace Cornell;

[StructLayout(LayoutKind.Sequential)]
public struct RadiosityUniforms
{
    public float AccumulationToLightmapScale;
    public float AccumulationBufferScale;
    public float LightWidth;
    public float LightHeight;
    public Vector3 LightCenter;
    private readonly float _pad0;
}