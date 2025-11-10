using System.Numerics;

namespace Cornell;

public readonly struct Quad
{
    public Vector3 Center { get; init; }
    public Vector3 Right { get; init; }
    public Vector3 Up { get; init; }
    public Vector3 Color { get; init; }
    public float Emissive { get; init; }
}