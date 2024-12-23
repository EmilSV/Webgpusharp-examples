using System.Numerics;
using System.Runtime.InteropServices;

struct SphereMesh
{
    public struct Vertex
    {
        public static readonly ulong PositionsOffset = (ulong)Marshal.OffsetOf<Vertex>(nameof(Position));
        public static readonly ulong NormalOffset = (ulong)Marshal.OffsetOf<Vertex>(nameof(Normal));
        public static readonly ulong UvOffset = (ulong)Marshal.OffsetOf<Vertex>(nameof(Uv));
        public static readonly ulong VertexStride = (ulong)Marshal.SizeOf<Vertex>();

        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Uv;
    }

    public Vertex[] Vertices;
    public ushort[] Indices;

    public static SphereMesh Create(
        float radius,
        int widthSegments = 32,
        int heightSegments = 16,
        float randomness = 0)
    {
        var randomGen = Random.Shared;
        var vertices = new List<Vertex>();
        var indices = new List<ushort>();

        widthSegments = Math.Max(3, widthSegments);
        heightSegments = Math.Max(2, heightSegments);

        Vector3 firstVertex = new();
        Vector3 vertex = new();
        int index = 0;
        var grid = new List<List<int>>();

        // generate vertices, normals and uvs
        for (int iy = 0; iy <= heightSegments; iy++)
        {
            var verticesRow = new List<int>();
            var v = iy / (float)heightSegments;

            // special case for the poles
            float uOffset = 0;
            if (iy == 0)
            {
                uOffset = 0.5f / widthSegments;
            }
            else if (iy == heightSegments)
            {
                uOffset = -0.5f / widthSegments;
            }

            for (int ix = 0; ix <= widthSegments; ix++)
            {
                var u = ix / (float)widthSegments;

                // Poles should just use the same position all the way around.
                if (ix == widthSegments)
                {
                    firstVertex = vertex;
                }
                else if (ix == 0 || (iy != 0 && iy != heightSegments))
                {
                    var rr = radius + (randomGen.NextSingle() - 0.5f) * 2 * randomness * radius;

                    // vertex
                    vertex.X = -rr * MathF.Cos(u * MathF.PI * 2) * MathF.Sin(v * MathF.PI);
                    vertex.Y = rr * MathF.Cos(v * MathF.PI);
                    vertex.Z = rr * MathF.Sin(u * MathF.PI * 2) * MathF.Sin(v * MathF.PI);

                    if (ix == 0)
                    {
                        vertex = firstVertex;
                    }
                }

                vertices.Add(new()
                {
                    Position = vertex,
                    Normal = Vector3.Normalize(vertex),
                    Uv = new Vector2(u + uOffset, 1 - v),
                });
                verticesRow.Add(index++);
            }

            grid.Add(verticesRow);
        }

        // indices
        for (int iy = 0; iy < heightSegments; iy++)
        {
            for (int ix = 0; ix < widthSegments; ix++)
            {
                var a = grid[iy][ix + 1];
                var b = grid[iy][ix];
                var c = grid[iy + 1][ix];
                var d = grid[iy + 1][ix + 1];

                if (iy != 0)
                {
                    indices.Add((ushort)a);
                    indices.Add((ushort)b);
                    indices.Add((ushort)d);
                }
                if (iy != 0 || ix != widthSegments - 1)
                {
                    indices.Add((ushort)b);
                    indices.Add((ushort)c);
                    indices.Add((ushort)d);
                }
            }
        }

        return new SphereMesh
        {
            Vertices = [.. vertices],
            Indices = [.. indices],
        };
    }
}


