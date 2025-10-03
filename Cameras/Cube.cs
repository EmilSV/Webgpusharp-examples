using System.Numerics;

static class Cube
{
    public struct Data(Vector4 position, Vector4 color, Vector2 uv)
    {
        public Vector4 Position = position;
        public Vector4 Color = color;
        public Vector2 UV = uv;
    }

    public const int CUBE_VERTEX_SIZE = 4 * 10; // Byte size of one cube vertex.
    public const int CUBE_POSITION_OFFSET = 0;
    public const int CUBE_COLOR_OFFSET = 4 * 4; // Byte offset of cube vertex color attribute.
    public const int CUBE_UV_OFFSET = 4 * 8;
    public const int CUBE_VERTEX_COUNT = 36;

    public static Data[] CubeVertexArray = [
        new Data(new(1, -1, 1, 1),  new(1, 0, 1, 1), new(0, 1)),
        new Data(new(-1, -1, 1, 1), new(0, 0, 1, 1), new(1, 1)),
        new Data(new(-1, -1, -1, 1),new(0, 0, 0, 1), new(1, 0)),
        new Data(new(1, -1, -1, 1), new(1, 0, 0, 1), new(0, 0)),
        new Data(new(1, -1, 1, 1),  new(1, 0, 1, 1), new(0, 1)),
        new Data(new(-1, -1, -1, 1),new(0, 0, 0, 1), new(1, 0)),
        new Data(new(1, -1, -1, 1), new(1, 0, 0, 1), new(0, 0)),

        new Data(new(1, 1, 1, 1), new(1, 1, 1, 1), new(0, 1)),
        new Data(new(1, -1, 1, 1), new(1, 0, 1, 1), new(1, 1)),
        new Data(new(1, -1, -1, 1), new(1, 0, 0, 1), new(1, 0)),
        new Data(new(1, 1, -1, 1), new(1, 1, 0, 1), new(0, 0)),
        new Data(new(1, 1, 1, 1), new(1, 1, 1, 1), new(0, 1)),
        new Data(new(1, -1, -1, 1), new(1, 0, 0, 1), new(1, 0)),

        new Data(new(-1, 1, 1, 1), new(0, 1, 1, 1), new(0, 1)),
        new Data(new(1, 1, 1, 1), new(1, 1, 1, 1), new(1, 1)),
        new Data(new(1, 1, -1, 1), new(1, 1, 0, 1), new(1, 0)),
        new Data(new(-1, 1, -1, 1), new(0, 1, 0, 1), new(0, 0)),
        new Data(new(-1, 1, 1, 1), new(0, 1, 1, 1), new(0, 1)),
        new Data(new(1, 1, -1, 1), new(1, 1, 0, 1), new(1, 0)),

        new Data(new(-1, -1, 1, 1), new(0, 0, 1, 1), new(0, 1)),
        new Data(new(-1, 1, 1, 1), new(0, 1, 1, 1), new(0, 1)),
        new Data(new(1, 1, -1, 1), new(1, 1, 0, 1), new(1, 0)),
        new Data(new(-1, -1, -1, 1), new(0, 0, 0, 1), new(1, 0)),
        new Data(new(-1, -1, 1, 1), new(0, 0, 1, 1), new(0, 1)),
        new Data(new(-1, 1, -1, 1), new(0, 1, 0, 1), new(0, 0)),

        new Data(new(1, 1, 1, 1), new(1, 1, 1, 1), new(0, 1)),
        new Data(new(-1, 1, 1, 1), new(0, 1, 1, 1), new(0, 1)),
        new Data(new(-1, -1, 1, 1), new(0, 0, 1, 1), new(0, 1)),
        new Data(new(-1, -1, 1, 1), new(0, 0, 1, 1), new(0, 1)),
        new Data(new(1, -1, 1, 1), new(1, 0, 1, 1), new(1, 1)),

        new Data(new(1, -1, -1, 1), new(1, 0, 0, 1), new(0, 1)),
        new Data(new(-1, -1, -1, 1), new(0, 0, 0, 1), new(1, 1)),
        new Data(new(-1, 1, -1, 1), new(0, 1, 0, 1), new(1, 0)),
        new Data(new(1, 1, -1, 1), new(1, 1, 0, 1), new(0, 0)),
        new Data(new(1, -1, -1, 1), new(1, 0, 0, 1), new(0, 1)),
        new Data(new(-1, 1, -1, 1), new(0, 1, 0, 1), new(0, 0)),
    ];

}