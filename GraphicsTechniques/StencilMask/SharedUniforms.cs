using System.Numerics;

struct SharedUniforms
{
    public Matrix4x4 ViewProjection;
    public Vector3 LightDirection;

#pragma warning disable CS0169, IDE0051
    private readonly float _pad0;
#pragma warning restore CS0169, IDE0051
}
