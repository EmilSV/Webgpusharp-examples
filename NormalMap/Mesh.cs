
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

sealed class Renderable
{
    public required GPUBuffer VertexBuffer { get; init; }
    public required GPUBuffer IndexBuffer { get; init; }
    public required int IndexCount { get; init; }
    public GPUBuffer? BindGroup { get; init; }
}

sealed class Mesh
{
    public required float[] Vertices { get; init; }
    public ushort[]? IndicesU16 { get; init; }
    public uint[]? IndicesU32 { get; init; }
    public required int VertexStride { get; init; }

    public static Renderable CreateMeshRenderable(
        Device device,
        Mesh mesh,
        bool storeVertices = false,
        bool storeIndices = false
    )
    {
        var vertexBufferUsage = storeVertices
            ? BufferUsage.Vertex | BufferUsage.Storage
            : BufferUsage.Vertex;
        var indexBufferUsage = storeIndices
            ? BufferUsage.Index | BufferUsage.Storage
            : BufferUsage.Index;

        // Create vertex and index buffers
        var vertexBuffer = device.CreateBuffer(new BufferDescriptor
        {
            Size = mesh.Vertices.GetSizeInBytes(),
            Usage = vertexBufferUsage,
            MappedAtCreation = true
        });
        vertexBuffer.GetMappedRange<float>(data => mesh.Vertices.AsSpan().CopyTo(data));
        vertexBuffer.Unmap();

        if (mesh.IndicesU16 != null && mesh.IndicesU32 != null)
            throw new Exception("Mesh cannot have both IndicesU16 and IndicesU32 set.");
        if (mesh.IndicesU16 == null && mesh.IndicesU32 == null)
            throw new Exception("Mesh must have either IndicesU16 or IndicesU32 set.");

        ulong indicesSizeInBytes = (mesh.IndicesU16 != null)
                ? mesh.IndicesU16.GetSizeInBytes()
                : mesh.IndicesU32!.GetSizeInBytes();
        int indicesCount = (mesh.IndicesU16 != null)
                ? mesh.IndicesU16.Length
                : mesh.IndicesU32!.Length;


        var indexBuffer = device.CreateBuffer(new BufferDescriptor
        {
            Size = indicesSizeInBytes,
            Usage = indexBufferUsage,
            MappedAtCreation = true
        });

        if (mesh.IndicesU16 != null)
        {
            indexBuffer.GetMappedRange<ushort>(data => mesh.IndicesU16.AsSpan().CopyTo(data));
        }
        else
        {
            indexBuffer.GetMappedRange<uint>(data => mesh.IndicesU32!.AsSpan().CopyTo(data));
        }
        indexBuffer.Unmap();

        return new Renderable
        {
            VertexBuffer = vertexBuffer,
            IndexBuffer = indexBuffer,
            IndexCount = indicesCount
        };
    }
}