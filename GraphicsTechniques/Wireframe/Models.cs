using System.Numerics;
using Setup;

class Models
{
    public ModelGeometry Teapot { get; private set; }
    public ModelGeometry Sphere { get; private set; }
    public ModelGeometry Jewel { get; private set; }
    public ModelGeometry Rock { get; private set; }


    private Models() { }


    static ModelGeometry ConvertMeshToTypedArrays(SimpleMeshBinReader.Mesh mesh, float scale, Vector3? offset = null)
    {
        var offsetValue = offset ?? Vector3.Zero;
        var vertices = new Vertex[mesh.Positions.Length];
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            vertices[i] = new Vertex
            {
                Position = mesh.Positions[i] * scale + offsetValue,
                Normal = mesh.Normals[i]
            };
        }

        var indices = new uint[mesh.Triangles.Length * 3];
        for (int i = 0; i < mesh.Triangles.Length; i++)
        {
            var (X, Y, Z) = mesh.Triangles[i];
            indices[i * 3 + 0] = (uint)X;
            indices[i * 3 + 1] = (uint)Y;
            indices[i * 3 + 2] = (uint)Z;
        }

        return new ModelGeometry
        {
            Vertices = vertices,
            Indices = indices,
        };
    }
    static ModelGeometry CreateSphereTypedArrays(float radius, int widthSegments = 32, int heightSegments = 16, float randomness = 0)
    {
        var sphere = SphereMesh.Create(radius, widthSegments, heightSegments, randomness);
        var vertices = new Vertex[sphere.Vertices.Length];
        for (int i = 0; i < sphere.Vertices.Length; i++)
        {
            vertices[i] = new Vertex
            {
                Position = sphere.Vertices[i].Position,
                Normal = sphere.Vertices[i].Normal,
            };
        }

        var indices = new uint[sphere.Indices.Length];
        for (int i = 0; i < sphere.Indices.Length; i++)
        {
            indices[i] = sphere.Indices[i];
        }

        return new ModelGeometry
        {
            Vertices = vertices,
            Indices = indices,
        };
    }
    static ModelGeometry FlattenNormals(ModelGeometry mesh)
    {
        var newVertices = new Vertex[mesh.Indices.Length];
        var newIndices = new uint[mesh.Indices.Length];
        Span<Vector3> positions = stackalloc Vector3[3];
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            for (int j = 0; j < 3; ++j)
            {
                var index = (int)mesh.Indices[i + j];
                var dst = i + j;
                var p = mesh.Vertices[index].Position;
                positions[j] = p;
                newVertices[dst] = new()
                {
                    Position = p,
                    Normal = Vector3.Zero
                };
                newIndices[i + j] = (uint)(i + j);
            }

            var normal = Vector3.Normalize(
                Vector3.Cross(
                    Vector3.Normalize(positions[1] - positions[0]),
                    Vector3.Normalize(positions[2] - positions[1])
                )
            );

            for (int j = 0; j < 3; ++j)
            {
                var dst = i + j;
                newVertices[dst].Normal = normal;
            }
        }

        return new ModelGeometry
        {
            Vertices = newVertices,
            Indices = newIndices,
        };
    }

    public static Lazy<Task<Models>> ModelData = new(async () =>
    {
        var teapotMesh = await Setup.Teapot.LoadMeshAsync();
        return new Models
        {
            Teapot = ConvertMeshToTypedArrays(teapotMesh, 1.5f),
            Sphere = CreateSphereTypedArrays(20),
            Jewel = FlattenNormals(CreateSphereTypedArrays(20, 5, 3)),
            Rock = FlattenNormals(CreateSphereTypedArrays(20, 32, 16, 0.1f)),
        };
    });
}