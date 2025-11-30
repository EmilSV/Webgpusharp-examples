using System.Net.Http.Headers;
using System.Numerics;
using Setup;

// Common interface for camera implementations
interface ICamera
{
    // update updates the camera using the user-input and returns the view matrix.
    ref readonly Matrix4x4 Update(float deltaTime, Input input);

    // The camera matrix.
    // This is the inverse of the view matrix.
    ref readonly Matrix4x4 GetMatrix();
    ref readonly Matrix4x4 SetMatrix(in Matrix4x4 matrix);

    ref readonly Matrix4x4 View { get; }

    // Alias to column vector 0 of the camera matrix.
    Vector3 Right { get; set; }
    // Alias to column vector 1 of the camera matrix.
    Vector3 Up { get; set; }
    // Alias to column vector 2 of the camera matrix.
    Vector3 Back { get; set; }
    // Alias to column vector 3 of the camera matrix.
    Vector3 Position { get; set; }
}

abstract class BaseCamera : ICamera
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
    public Vector3 Right
    {
        get => new(_matrix.M11, _matrix.M12, _matrix.M13);
        set => (_matrix.M11, _matrix.M12, _matrix.M13) = (value.X, value.Y, value.Z);
    }

    // Returns column vector 1 of the camera matrix
    public Vector3 Up
    {
        get => new(_matrix.M21, _matrix.M22, _matrix.M23);
        set => (_matrix.M21, _matrix.M22, _matrix.M23) = (value.X, value.Y, value.Z);
    }
    // Returns column vector 2 of the camera matrix
    public Vector3 Back
    {
        get => new(_matrix.M31, _matrix.M32, _matrix.M33);
        set => (_matrix.M31, _matrix.M32, _matrix.M33) = (value.X, value.Y, value.Z);
    }
    // Returns column vector 3 of the camera matrix
    public Vector3 Position
    {
        get => new(_matrix.M41, _matrix.M42, _matrix.M43);
        set => (_matrix.M41, _matrix.M42, _matrix.M43) = (value.X, value.Y, value.Z);
    }

    public abstract ref readonly Matrix4x4 Update(float deltaTime, Input input);

    // Returns `x` clamped between [`min` .. `max`]
    protected static float Clamp(float value, float min, float max) =>
        MathF.Min(MathF.Max(value, min), max);

    // Returns `x` float-modulo `div`
    protected static float Mod(float x, float div) =>
        x - MathF.Floor(MathF.Abs(x) / div) * div * MathF.Sign(x);

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

    public WASDCamera(Vector3? position = null, Vector3? target = null)
    {
        if (position.HasValue || target.HasValue)
        {
            Vector3 positionVar = position ?? new Vector3(0, 0, 5);
            Vector3 targetVar = target ?? new Vector3(0, 0, 0);
            Vector3 back = Vector3.Normalize(positionVar - targetVar);
            RecalculateAngles(back);
            Position = positionVar;
        }
    }

    public override ref readonly Matrix4x4 SetMatrix(in Matrix4x4 matrix)
    {
        ref readonly var result = ref base.SetMatrix(matrix);
        RecalculateAngles(Back);
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
        Vector3 position = Position;

        // Reconstruct the camera's rotation, and store into the camera matrix.
        var matrix = Matrix4x4.CreateRotationY(_yaw);
        base.SetMatrix(matrix.RotateX(_pitch));

        // Calculate the new target velocity
        var digital = input.Digital;
        var deltaRight = Sign(digital.Right, digital.Left);
        var deltaUp = Sign(digital.Up, digital.Down);
        var targetVelocity = new Vector3();
        var deltaBack = Sign(digital.Backward, digital.Forward);
        targetVelocity += Right * deltaRight;
        targetVelocity += Up * deltaUp;
        targetVelocity += Back * deltaBack;
        targetVelocity += targetVelocity.LengthSquared() > 0 ? Vector3.Normalize(targetVelocity) : targetVelocity;
        targetVelocity *= MovementSpeed;

        // Mix new target velocity
        Velocity = Lerp(targetVelocity, Velocity, MathF.Pow(1 - FrictionCoefficient, deltaTime));

        Position = position + Velocity * deltaTime;

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

// ArcballCamera implements a basic orbiting camera around the world origin
class ArcballCamera : BaseCamera
{
    // The camera distance from the target
    private float _distance;
    // The current angular velocity
    private float _angularVelocity = 0;

    // The current rotation axis
    public Vector3 Axis = new Vector3();


    // Speed multiplier for camera rotation
    public float RotationSpeed = 1f;

    // Speed multiplier for camera zoom
    public float ZoomSpeed = 0.1f;

    // Rotation velocity drag coeffient [0 .. 1]
    // 0: Spins forever
    // 1: Instantly stops spinning
    public float frictionCoefficient = 0.999f;

    public ArcballCamera(Vector3? position)
    {
        if (position.HasValue)
        {
            Position = position.Value;
            _distance = Position.Length();
            Back = Vector3.Normalize(Position);
            RecalculateRight();
            RecalculateUp();
        }
    }

    public override ref readonly Matrix4x4 SetMatrix(in Matrix4x4 matrix)
    {
        ref readonly var result = ref base.SetMatrix(matrix);
        _distance = Position.Length();
        return ref result;
    }


    public override ref readonly Matrix4x4 Update(float deltaTime, Input input)
    {
        const float EPSILON = 0.0000001f;
        if (input.Analog.Touching)
        {
            // Currently being dragged.
            _angularVelocity = 0;
        }
        else
        {
            // Dampen any existing angular velocity
            _angularVelocity *= MathF.Pow(1 - frictionCoefficient, deltaTime);
        }

        // Calculate the movement vector
        var movement = new Vector3();
        movement += Right * input.Analog.X;
        movement += Up * -input.Analog.Y;

        // Cross the movement vector with the view direction to calculate the rotation axis x magnitude
        var crossProduct = Vector3.Cross(movement, Back);

        // Calculate the magnitude of the drag
        var magnitude = crossProduct.Length();

        if (magnitude > EPSILON)
        {
            // Normalize the crossProduct to get the rotation axis
            Axis = crossProduct * 1 / magnitude;

            // Remember the current angular velocity. This is used when the touch is released for a fling.
            _angularVelocity = magnitude * RotationSpeed;
        }

        // The rotation around this.axis to apply to the camera matrix this update 
        var rotationAngle = _angularVelocity * deltaTime;
        if (rotationAngle > EPSILON)
        {
            // Rotate the matrix around axis
            // Note: The rotation is not done as a matrix-matrix multiply as the repeated multiplications
            // will quickly introduce substantial error into the matrix.
            Back = Vector3.Normalize(Rotate(Back, Axis, rotationAngle));
            RecalculateRight();
            RecalculateUp();
        }

        // recalculate `this.position` from `this.back` considering zoom
        if (input.Analog.Zoom != 0)
        {
            _distance *= 1 + input.Analog.Zoom * ZoomSpeed;
        }
        Position = Back * _distance;

        // Invert the camera matrix to build the view matrix
        _view = Matrix4x4.Invert(GetMatrix(), out var result) ? result : Matrix4x4.Identity;
        return ref _view;
    }


    // Assigns `Right` with the cross product of `Up` and `Back`
    public void RecalculateRight()
    {
        Right = Vector3.Normalize(Vector3.Cross(Up, Back));
    }

    // Assigns `Up` with the cross product of `Back` and `Right`
    public void RecalculateUp()
    {
        Up = Vector3.Normalize(Vector3.Cross(Back, Right));
    }
}