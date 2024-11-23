using System.Numerics;

namespace Setup;

public static class MatrixExtension
{
    public static void Rotate(this ref Matrix4x4 matrix, Vector3 axis, float angle)
    {
        var normalizeAxis = Vector3.Normalize(axis);

        var x = normalizeAxis.X;
        var y = normalizeAxis.Y;
        var z = normalizeAxis.Z;

        var squared = normalizeAxis * normalizeAxis;
        var xx = squared.X;
        var yy = squared.Y;
        var zz = squared.Z;

        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        var oneMinusCosine = 1.0f - c;

        var r00 = xx + (1 - xx) * c;
        var r01 = x * y * oneMinusCosine + z * s;
        var r02 = x * z * oneMinusCosine - y * s;
        var r10 = x * y * oneMinusCosine - z * s;
        var r11 = yy + (1 - yy) * c;
        var r12 = y * z * oneMinusCosine + x * s;
        var r20 = x * z * oneMinusCosine + y * s;
        var r21 = y * z * oneMinusCosine - x * s;
        var r22 = zz + (1 - zz) * c;

        var m00 = matrix.M11;
        var m01 = matrix.M12;
        var m02 = matrix.M13;
        var m03 = matrix.M14;
        var m10 = matrix.M21;
        var m11 = matrix.M22;
        var m12 = matrix.M23;
        var m13 = matrix.M24;
        var m20 = matrix.M31;
        var m21 = matrix.M32;
        var m22 = matrix.M33;
        var m23 = matrix.M34;

        matrix.M11 = r00 * m00 + r01 * m10 + r02 * m20;
        matrix.M12 = r00 * m01 + r01 * m11 + r02 * m21;
        matrix.M13 = r00 * m02 + r01 * m12 + r02 * m22;
        matrix.M14 = r00 * m03 + r01 * m13 + r02 * m23;
        matrix.M21 = r10 * m00 + r11 * m10 + r12 * m20;
        matrix.M22 = r10 * m01 + r11 * m11 + r12 * m21;
        matrix.M23 = r10 * m02 + r11 * m12 + r12 * m22;
        matrix.M24 = r10 * m03 + r11 * m13 + r12 * m23;
        matrix.M31 = r20 * m00 + r21 * m10 + r22 * m20;
        matrix.M32 = r20 * m01 + r21 * m11 + r22 * m21;
        matrix.M33 = r20 * m02 + r21 * m12 + r22 * m22;
        matrix.M34 = r20 * m03 + r21 * m13 + r22 * m23;
    }
}
