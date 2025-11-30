using System.Numerics;

namespace Cornell;


public struct RadiosityUniforms
{
    public float AccumulationToLightmapScale;
    public float AccumulationBufferScale;
    public float LightWidth;
    public float LightHeight;
    public Vector3 LightCenter;
    private float _pad0;
}