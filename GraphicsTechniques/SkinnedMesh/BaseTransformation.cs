using System.Numerics;
using Setup;

public class BaseTransformation
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }

    public BaseTransformation(Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null)
    {
        Position = position ?? Vector3.Zero;
        Rotation = rotation ?? Quaternion.Identity;
        Scale = scale ?? Vector3.One;
    }

    public Matrix4x4 GetMatrix()
    {
        // Analagous to let transformationMatrix: mat4x4f = translation * rotation * scale;
        var dst = Matrix4x4.Identity;
        // Scale the transformation Matrix
        dst.Scale(Scale);
        // Calculate the rotationMatrix from the quaternion
        var rotationMatrix = Matrix4x4.CreateFromQuaternion(Rotation);
        // Apply the rotation Matrix to the scaleMatrix ( scaleMat * rotMat)
        dst = rotationMatrix * dst;
        // Translate the transformationMatrix
        dst.Translate(Position);
        return dst;
    }
}
