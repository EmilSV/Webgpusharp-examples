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
    public uint[] Indices;

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
            var v = iy / (double)heightSegments;

            // special case for the poles
            double uOffset = 0;
            if (iy == 0)
            {
                uOffset = 0.5 / (double)widthSegments;
            }
            else if (iy == heightSegments)
            {
                uOffset = -0.5 / (double)widthSegments;
            }

            for (int ix = 0; ix <= widthSegments; ix++)
            {
                var u = ix / (double)widthSegments;

                // Poles should just use the same position all the way around.
                if (ix == widthSegments)
                {
                    vertex = firstVertex;
                }
                else if (ix == 0 || (iy != 0 && iy != heightSegments))
                {
                    var rr = radius + (randomGen.NextSingle() - 0.5) * 2.0 * randomness * radius;

                    // vertex
                    vertex.X = (float)(-rr * Math.Cos(u * Math.PI * 2.0) * Math.Sin(v * Math.PI));
                    vertex.Y = (float)(rr * Math.Cos(v * Math.PI));
                    vertex.Z = (float)(rr * Math.Sin(u * Math.PI * 2.0) * Math.Sin(v * Math.PI));

                    if (ix == 0)
                    {
                        firstVertex = vertex;
                    }
                }

                vertices.Add(new()
                {
                    Position = vertex,
                    Normal = Vector3.Normalize(vertex),
                    Uv = new((float)(u + uOffset), (float)(1.0 - v)),
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
                if (iy != heightSegments - 1)
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