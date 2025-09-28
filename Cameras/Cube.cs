using System.Numerics;

static class Cube
{
    public const int CUBE_VERTEX_SIZE = 4 * 10; // Byte size of one cube vertex.
    public const int CUBE_POSITION_OFFSET = 0;
    public const int CUBE_COLOR_OFFSET = 4 * 4; // Byte offset of cube vertex color attribute.
    public const int CUBE_UV_OFFSET = 4 * 8;
    public const int CUBE_VERTEX_COUNT = 36;

    public static (Vector4 position, Vector4 color, Vector2 uv)[] CubeVertexArray = [
        (new(1, -1, 1, 1),  new(1, 0, 1, 1), new(0, 1)),
        (new(-1, -1, 1, 1), new(0, 0, 1, 1), new(1, 1)),
        (new(-1, -1, -1, 1),new(0, 0, 0, 1), new(1, 0)),
        (new(1, -1, -1, 1), new(1, 0, 0, 1), new(0, 0)),
        (new(1, -1, 1, 1),  new(1, 0, 1, 1), new(0, 1)),
        (new(-1, -1, -1, 1),new(0, 0, 0, 1), new(1, 0)),
        (new(1, -1, -1, 1), new(1, 0, 0, 1), new(0, 0)),

        (new(1, 1, 1, 1), new(1, 1, 1, 1), new(0, 1)),
        (new(1, -1, 1, 1), new(1, 0, 1, 1), new(1, 1)),
        (new(1, -1, -1, 1), new(1, 0, 0, 1), new(1, 0)),
        (new(1, 1, -1, 1), new(1, 1, 0, 1), new(0, 0)),
        (new(1, 1, 1, 1), new(1, 1, 1, 1), new(0, 1)),
        (new(1, -1, -1, 1), new(1, 0, 0, 1), new(1, 0)),

        (new(-1, 1, 1, 1), new(0, 1, 1, 1), new(0, 1)),
        (new(1, 1, 1, 1), new(1, 1, 1, 1), new(1, 1)),
        (new(1, 1, -1, 1), new(1, 1, 0, 1), new(1, 0)),
        (new(-1, 1, -1, 1), new(0, 1, 0, 1), new(0, 0)),
        (new(-1, 1, 1, 1), new(0, 1, 1, 1), new(0, 1)),
        (new(1, 1, -1, 1), new(1, 1, 0, 1), new(1, 0)),

        (new(-1, -1, 1, 1), new(0, 0, 1, 1), new(0, 1)),
        (new(-1, 1, 1, 1), new(0, 1, 1, 1), new(0, 1)),
        (new(1, 1, -1, 1), new(1, 1, 0, 1), new(1, 0)),
        (new(-1, -1, -1, 1), new(0, 0, 0, 1), new(1, 0)),
        (new(-1, -1, 1, 1), new(0, 0, 1, 1), new(0, 1)),
        (new(-1, 1, -1, 1), new(0, 1, 0, 1), new(0, 0)),

        (new(1, 1, 1, 1), new(1, 1, 1, 1), new(0, 1)),
        (new(-1, 1, 1, 1), new(0, 1, 1, 1), new(0, 1)),
        (new(-1, -1, 1, 1), new(0, 0, 1, 1), new(0, 1)),
        (new(-1, -1, 1, 1), new(0, 0, 1, 1), new(0, 1)),
        (new(1, -1, 1, 1), new(1, 0, 1, 1), new(1, 1)),

        (new(1, -1, -1, 1), new(1, 0, 0, 1), new(0, 1)),
        (new(-1, -1, -1, 1), new(0, 0, 0, 1), new(1, 1)),
        (new(-1, 1, -1, 1), new(0, 1, 0, 1), new(1, 0)),
        (new(1, 1, -1, 1), new(1, 1, 0, 1), new(0, 0)),
        (new(1, -1, -1, 1), new(1, 0, 0, 1), new(0, 1)),
        (new(-1, 1, -1, 1), new(0, 1, 0, 1), new(0, 0)),
    ];

}