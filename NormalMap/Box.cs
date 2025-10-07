







static class Box
{
    public static Mesh CreateBoxMeshWithTangents(
        float width,
        float height,
        float depth
    )
    {
        //    __________
        //   /         /|      y
        //  /   +y    / |      ^
        // /_________/  |      |
        // |         |+x|      +---> x
        // |   +z    |  |     /
        // |         | /     z
        // |_________|/
        //
        const int pX = 0; // +x
        const int nX = 1; // -x
        const int pY = 2; // +y
        const int nY = 3; // -y
        const int pZ = 4; // +z
        const int nZ = 5; // -z
        ReadOnlySpan<(int tangent, int bitangent, int normal)> faces =
        [
            ( tangent : nZ, bitangent : pY, normal : pX ),
            ( tangent : pZ, bitangent : pY, normal : nX ),
            ( tangent : pX, bitangent : nZ, normal : pY ),
            ( tangent : pX, bitangent : pZ, normal : nY ),
            ( tangent : pX, bitangent : pY, normal : pZ ),
            ( tangent : nX, bitangent : pY, normal : nZ ),
        ];
        const int verticesPerSide = 4;
        const int indicesPerSize = 6;
        const int f32sPerVertex = 14; // position : vec3f, tangent : vec3f, bitangent : vec3f, normal : vec3f, uv :vec2f
        int vertexStride = f32sPerVertex * 4;
        var vertices = new float[faces.Length * verticesPerSide * f32sPerVertex];
        var indicesU16 = new ushort[faces.Length * indicesPerSize];
        int vertexOffset = 0;
        int indexOffset = 0;
        var halfVecs = new float[][]
        {
            [+width / 2, 0, 0], // +x
            [-width / 2, 0, 0], // -x
            [0, +height / 2, 0], // +y
            [0, -height / 2, 0], // -
            [0, 0, +depth / 2], // +z
            [0, 0, -depth / 2], // -z
        };

        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            var face = faces[faceIndex];
            var tangent = halfVecs[face.tangent];
            var bitangent = halfVecs[face.bitangent];
            var normal = halfVecs[face.normal];
            for (int u = 0; u < 2; u++)
            {
                for (int v = 0; v < 2; v++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        vertices[vertexOffset++] =
                          normal[i] +
                          (u == 0 ? -1 : 1) * tangent[i] +
                          (v == 0 ? -1 : 1) * bitangent[i];
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        vertices[vertexOffset++] = normal[i];
                    }
                    vertices[vertexOffset++] = u;
                    vertices[vertexOffset++] = v;
                    for (int i = 0; i < 3; i++)
                    {
                        vertices[vertexOffset++] = tangent[i];
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        vertices[vertexOffset++] = bitangent[i];
                    }
                }
            }
            checked
            {
                indicesU16[indexOffset++] = (ushort)(faceIndex * verticesPerSide + 0);
                indicesU16[indexOffset++] = (ushort)(faceIndex * verticesPerSide + 2);
                indicesU16[indexOffset++] = (ushort)(faceIndex * verticesPerSide + 1);

                indicesU16[indexOffset++] = (ushort)(faceIndex * verticesPerSide + 2);
                indicesU16[indexOffset++] = (ushort)(faceIndex * verticesPerSide + 3);
                indicesU16[indexOffset++] = (ushort)(faceIndex * verticesPerSide + 1);
            }
        }

        return new Mesh
        {
            Vertices = vertices,
            IndicesU16 = indicesU16,
            VertexStride = vertexStride
        };
    }
}