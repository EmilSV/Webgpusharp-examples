using System.Net.Http.Headers;
using System.Numerics;
using Setup;

// Common interface for camera implementations
interface Camera
{
    // update updates the camera using the user-input and returns the view matrix.
    ref readonly Matrix4x4 Update(float deltaTime, Input input);

    // The camera matrix.
    // This is the inverse of the view matrix.
    ref readonly Matrix4x4 GetMatrix();
    ref readonly Matrix4x4 SetMatrix(in Matrix4x4 matrix);

    ref readonly Matrix4x4 View { get; }

    // Alias to column vector 0 of the camera matrix.
    Vector4 Right { get; set; }
    // Alias to column vector 1 of the camera matrix.
    Vector4 Up { get; set; }
    // Alias to column vector 2 of the camera matrix.
    Vector4 Back { get; set; }
    // Alias to column vector 3 of the camera matrix.
    Vector4 Position { get; set; }
}

abstract class BaseCamera : Camera
{
    // The camera matrix
    private Matrix4x4 _matrix = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1
    );

    // The calculated view matrix
    protected Matrix4x4 _view = new();

    public ref readonly Matrix4x4 View
    {
        get
        {
            return ref _view;
        }
    }

    // Returns the camera matrix
    public virtual ref readonly Matrix4x4 GetMatrix()
    {
        return ref _matrix;
    }

    public virtual ref readonly Matrix4x4 SetMatrix(in Matrix4x4 matrix)
    {
        _matrix = matrix;
        return ref _matrix;
    }

    // Returns column vector 0 of the camera matrix
    public Vector4 Right
    {
        get => new(_matrix.M11, _matrix.M12, _matrix.M13, _matrix.M14);
        set => (_matrix.M11, _matrix.M12, _matrix.M13, _matrix.M14) = (value.X, value.Y, value.Z, value.W);
    }

    // Returns column vector 1 of the camera matrix
    public Vector4 Up
    {
        get => new(_matrix.M21, _matrix.M22, _matrix.M23, _matrix.M24);
        set => (_matrix.M21, _matrix.M22, _matrix.M23, _matrix.M24) = (value.X, value.Y, value.Z, value.W);
    }
    // Returns column vector 2 of the camera matrix
    public Vector4 Back
    {
        get => new(_matrix.M31, _matrix.M32, _matrix.M33, _matrix.M34);
        set => (_matrix.M31, _matrix.M32, _matrix.M33, _matrix.M34) = (value.X, value.Y, value.Z, value.W);
    }
    // Returns column vector 3 of the camera matrix
    public Vector4 Position
    {
        get => new(_matrix.M41, _matrix.M42, _matrix.M43, _matrix.M44);
        set => (_matrix.M41, _matrix.M42, _matrix.M43, _matrix.M44) = (value.X, value.Y, value.Z, value.W);
    }

    public abstract ref readonly Matrix4x4 Update(float deltaTime, Input input);

    // Returns `x` clamped between [`min` .. `max`]
    protected static float Clamp(float value, float min, float max) =>
        MathF.Min(MathF.Max(value, min), max);

    // Returns `x` float-modulo `div`
    protected static float Mod(float x, float div) =>
        x - div * MathF.Floor(MathF.Abs(x) / div) * div * MathF.Sign(x);

    // Returns `vec` rotated `angle` radians around `axis`
    protected static Vector3 Rotate(Vector3 vec, Vector3 axis, float angle)
    {
        return TransformMat4Upper3x3(vec, Matrix4x4.CreateFromAxisAngle(axis, angle));
    }

    // Returns the linear interpolation between 'a' and 'b' using 's'
    protected static Vector3 Lerp(Vector3 a, Vector3 b, float s) =>
        a + (b - a) * s;


    private static Vector3 TransformMat4Upper3x3(Vector3 vec, in Matrix4x4 rot)
    {
        return new Vector3(
            vec.X * rot.M11 + vec.Y * rot.M21 + vec.Z * rot.M31,
            vec.X * rot.M12 + vec.Y * rot.M22 + vec.Z * rot.M32,
            vec.X * rot.M13 + vec.Y * rot.M23 + vec.Z * rot.M33
        );
    }


}

class WASDCamera : BaseCamera
{
    // The camera absolute pitch angle
    private float _pitch;
    // The camera absolute yaw angle
    private float _yaw;

    // The movement velocity
    public Vector3 Velocity = new Vector3(0, 0, 0);

    // Speed multiplier for camera movement
    public float MovementSpeed = 10.0f;

    // Speed multiplier for camera rotation
    public float RotationSpeed = 1f;

    // Movement velocity drag coeffient [0 .. 1]
    // 0: Continues forever
    // 1: Instantly stops moving
    public float FrictionCoefficient = 0.99f;

    public WASDCamera(Vector3? position, Vector3? target)
    {
        if (position.HasValue || target.HasValue)
        {
            Vector3 positionVar = position ?? new Vector3(0, 0, 5);
            Vector3 targetVar = target ?? new Vector3(0, 0, 0);
            Vector3 back = Vector3.Normalize(positionVar - targetVar);
            RecalculateAngles(back);
            Position = new Vector4(positionVar, 1);
        }
    }

    public override ref readonly Matrix4x4 SetMatrix(in Matrix4x4 matrix)
    {
        ref readonly var result = ref base.SetMatrix(matrix);
        RecalculateAngles(Back.AsVector3());
        return ref result;
    }

    public override ref readonly Matrix4x4 Update(float deltaTime, Input input)
    {
        static float Sign(bool positive, bool negative) =>
            (positive ? 1f : 0f) - (negative ? 1f : 0f);


        // Apply the delta rotation to the pitch and yaw angles
        _yaw -= input.Analog.X * deltaTime * RotationSpeed;
        _pitch -= input.Analog.Y * deltaTime * RotationSpeed;

        // Wrap yaw between [0° .. 360°], just to prevent large accumulation.
        _yaw = Mod(_yaw, MathF.PI * 2);
        // Clamp pitch between [-90° .. +90°] to prevent somersaults.
        _pitch = Clamp(_pitch, -MathF.PI / 2, MathF.PI / 2);
        // Save the current position, as we're about to rebuild the camera matrix.
        Vector3 position = Position.AsVector3();

        // Reconstruct the camera's rotation, and store into the camera matrix.
        var matrix = Matrix4x4.CreateRotationY(_yaw);
        base.SetMatrix(matrix.RotateX(_pitch));

        // Calculate the new target velocity
        var digital = input.Digital;
        var deltaRight = Sign(digital.Right, digital.Left);
        var deltaUp = Sign(digital.Up, digital.Down);
        var targetVelocity = new Vector3();
        var deltaBack = Sign(digital.Forward, digital.Backward);
        targetVelocity += Right.AsVector3() * deltaRight;
        targetVelocity += Up.AsVector3() * deltaUp;
        targetVelocity += Back.AsVector3() * deltaBack;
        targetVelocity = Vector3.Normalize(targetVelocity);
        targetVelocity *= MovementSpeed;

        // Mix new target velocity
        Velocity = Lerp(targetVelocity, Velocity, MathF.Pow(1 - FrictionCoefficient, deltaTime));

        Position = new Vector4(Position.AsVector3() + Velocity * deltaTime, 1);

        _view = Matrix4x4.Invert(GetMatrix(), out var result) ? result : Matrix4x4.Identity;
        return ref _view;
    }


    // Recalculates the yaw and pitch values from a directional vector
    public void RecalculateAngles(Vector3 dir)
    {
        _yaw = MathF.Atan2(dir.X, dir.Z);
        _pitch = -MathF.Asin(dir.Y);
    }
}