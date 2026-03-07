using System.Numerics;

namespace Cornell;


public struct RadiosityUniforms
{
    public float AccumulationToLightmapScale;
    public float AccumulationBufferScale;
    public float LightWidth;
    public float LightHeight;
    public Vector3 LightCenter;
#pragma warning disable CS0169
    private float _pad0;
#pragma warning restore CS0169
}