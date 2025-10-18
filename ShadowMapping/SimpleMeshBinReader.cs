using System.Buffers.Binary;
using System.Numerics;

/// <summary>
/// A simple binary mesh format reader.
/// The format is as follows:
/// - int32: number of positions
/// - float32 x, float32 y, float32 z * number of positions
/// - int32: number of triangles
/// - int32 a, int32 b, int32 c * number of triangles
/// - int32: number of normals
/// - float32 x, float32 y, float32 z * number of normals
/// - int32: number of uvs
/// - float32 u, float32 v * number of uvs
/// All numeric values are stored in little-endian format.
/// </summary>
static class SimpleMeshBinReader
{
    public class Mesh
    {
        public required Vector3[] Positions;
        public required (int X, int Y, int Z)[] Triangles;
        public required Vector3[] Normals;
        public required Vector2[] Uvs;
    }

    public static async Task<Mesh> LoadData(Stream stream)
    {
        byte[] buffer = new byte[sizeof(int) * 3];

        await stream.ReadExactlyAsync(buffer, 0, sizeof(int));
        var positionArrayLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, sizeof(int)));
        var positions = new Vector3[positionArrayLength];
        for (int i = 0; i < positions.Length; i++)
        {
            await stream.ReadExactlyAsync(buffer, 0, buffer.Length);
            float x = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sizeof(float) * 0, sizeof(float)));
            float y = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sizeof(float) * 1, sizeof(float)));
            float z = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sizeof(float) * 2, sizeof(float)));
            positions[i] = new(x, y, z);
        }

        await stream.ReadExactlyAsync(buffer, 0, sizeof(int));
        var triangleArrayLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, sizeof(int)));
        var triangles = new (int, int, int)[triangleArrayLength];
        for (int i = 0; i < triangles.Length; i++)
        {
            await stream.ReadExactlyAsync(buffer, 0, buffer.Length);
            int a = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(sizeof(int) * 0, sizeof(int)));
            int b = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(sizeof(int) * 1, sizeof(int)));
            int c = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(sizeof(int) * 2, sizeof(int)));
            triangles[i] = (a, b, c);
        }

        await stream.ReadExactlyAsync(buffer, 0, sizeof(int));
        var normalArrayLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, sizeof(int)));
        var normals = new Vector3[normalArrayLength];
        for (int i = 0; i < normals.Length; i++)
        {
            await stream.ReadExactlyAsync(buffer, 0, buffer.Length);
            float x = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sizeof(float) * 0, sizeof(float)));
            float y = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sizeof(float) * 1, sizeof(float)));
            float z = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sizeof(float) * 2, sizeof(float)));
            normals[i] = new(x, y, z);
        }

        await stream.ReadExactlyAsync(buffer, 0, sizeof(int));
        var uvArrayLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, sizeof(int)));
        var uvs = new Vector2[uvArrayLength];
        for (int i = 0; i < uvs.Length; i++)
        {
            await stream.ReadExactlyAsync(buffer, 0, sizeof(float) * 2);
            float u = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sizeof(float) * 0, sizeof(float)));
            float v = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sizeof(float) * 1, sizeof(float)));
            uvs[i] = new(u, v);
        }
        return new Mesh
        {
            Positions = positions,
            Triangles = triangles,
            Normals = normals,
            Uvs = uvs
        };
    }
}