using System.Numerics;

namespace StencilMask;


public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Texcoord;
}

public struct TriangleIndices
{
    public ushort A;
    public ushort B;
    public ushort C;
}

/// <summary>
/// Vertex data consisting of interleaved position, normal, and texcoord.
/// </summary>
public struct VertexData
{
    public Vertex[] Vertices;
    public TriangleIndices[] Indices;
}


/// <summary>
/// Primitive geometry generation utilities adapted from webgpu-utils.
/// Each vertex consists of 8 floats: position (3), normal (3), texcoord (2).
/// </summary>
public static class Primitives
{
    /// <summary>
    /// Creates XZ plane vertices with position, normal, and texcoord data.
    /// </summary>
    public static VertexData CreatePlaneVertices(
        float width = 1f,
        float depth = 1f,
        int subdivisionsWidth = 1,
        int subdivisionsDepth = 1)
    {
        var numVertices = (subdivisionsWidth + 1) * (subdivisionsDepth + 1);
        var vertices = new Vertex[numVertices];

        var cursor = 0;
        for (int z = 0; z <= subdivisionsDepth; z++)
        {
            for (int x = 0; x <= subdivisionsWidth; x++)
            {
                var u = (float)x / subdivisionsWidth;
                var v = (float)z / subdivisionsDepth;

                vertices[cursor++] = new Vertex
                {
                    Position = new(width * u - width * 0.5f, 0, depth * v - depth * 0.5f),
                    Normal = new(0, 1, 0),
                    Texcoord = new(u, v)
                };
            }
        }

        var numVertsAcross = subdivisionsWidth + 1;
        var indices = new TriangleIndices[subdivisionsWidth * subdivisionsDepth * 2];

        cursor = 0;
        for (int z = 0; z < subdivisionsDepth; z++)
        {
            for (int x = 0; x < subdivisionsWidth; x++)
            {
                // Make triangle 1 of quad.
                indices[cursor++] = new TriangleIndices
                {
                    A = (ushort)((z + 0) * numVertsAcross + x),
                    B = (ushort)((z + 1) * numVertsAcross + x),
                    C = (ushort)((z + 0) * numVertsAcross + x + 1)
                };

                // Make triangle 2 of quad.
                indices[cursor++] = new TriangleIndices
                {
                    A = (ushort)((z + 1) * numVertsAcross + x),
                    B = (ushort)((z + 1) * numVertsAcross + x + 1),
                    C = (ushort)((z + 0) * numVertsAcross + x + 1)
                };
            }
        }

        return new VertexData { Vertices = vertices, Indices = indices };
    }

    /// <summary>
    /// Creates sphere vertices with position, normal, and texcoord data.
    /// </summary>
    public static VertexData CreateSphereVertices(
        float radius = 1f,
        int subdivisionsAxis = 24,
        int subdivisionsHeight = 12,
        float startLatitudeInRadians = 0f,
        float endLatitudeInRadians = MathF.PI,
        float startLongitudeInRadians = 0f,
        float endLongitudeInRadians = MathF.PI * 2f)
    {
        if (subdivisionsAxis <= 0 || subdivisionsHeight <= 0)
        {
            throw new ArgumentException("subdivisionsAxis and subdivisionsHeight must be > 0");
        }

        var latRange = endLatitudeInRadians - startLatitudeInRadians;
        var longRange = endLongitudeInRadians - startLongitudeInRadians;

        var numVertices = (subdivisionsAxis + 1) * (subdivisionsHeight + 1);
        var vertices = new Vertex[numVertices];

        var cursor = 0;
        for (int y = 0; y <= subdivisionsHeight; y++)
        {
            for (int x = 0; x <= subdivisionsAxis; x++)
            {
                var u = (float)x / subdivisionsAxis;
                var v = (float)y / subdivisionsHeight;
                var theta = longRange * u + startLongitudeInRadians;
                var phi = latRange * v + startLatitudeInRadians;
                var sinTheta = MathF.Sin(theta);
                var cosTheta = MathF.Cos(theta);
                var sinPhi = MathF.Sin(phi);
                var cosPhi = MathF.Cos(phi);
                var ux = cosTheta * sinPhi;
                var uy = cosPhi;
                var uz = sinTheta * sinPhi;

                // position
                vertices[cursor++] = new Vertex
                {
                    Position = new(radius * ux, radius * uy, radius * uz),
                    Normal = new(ux, uy, uz),
                    Texcoord = new(1 - u, v)
                };
            }
        }

        var numVertsAround = subdivisionsAxis + 1;
        var indices = new TriangleIndices[subdivisionsAxis * subdivisionsHeight * 2];
        cursor = 0;
        for (int x = 0; x < subdivisionsAxis; x++)
        {
            for (int y = 0; y < subdivisionsHeight; y++)
            {
                // Make triangle 1 of quad.
                indices[cursor++] = new TriangleIndices
                {
                    A = (ushort)((y + 0) * numVertsAround + x),
                    B = (ushort)((y + 0) * numVertsAround + x + 1),
                    C = (ushort)((y + 1) * numVertsAround + x)
                };

                // Make triangle 2 of quad.
                indices[cursor++] = new TriangleIndices
                {
                    A = (ushort)((y + 1) * numVertsAround + x),
                    B = (ushort)((y + 0) * numVertsAround + x + 1),
                    C = (ushort)((y + 1) * numVertsAround + x + 1)
                };
            }
        }

        return new VertexData { Vertices = vertices, Indices = indices };
    }

    /// <summary>
    /// Array of the indices of corners of each face of a cube.
    /// </summary>
    private static readonly int[][] CubeFaceIndices =
    [
        [3, 7, 5, 1], // right
        [6, 2, 0, 4], // left
        [6, 7, 3, 2], // top
        [0, 1, 5, 4], // bottom
        [7, 6, 4, 5], // front
        [2, 3, 1, 0], // back
    ];

    /// <summary>
    /// Creates the vertices and indices for a cube centered at the origin.
    /// </summary>
    public static VertexData CreateCubeVertices(float size = 1f)
    {
        var k = size / 2f;

        Vector3[] cornerVertices =
        [
            new(-k, -k, -k),
            new(+k, -k, -k),
            new(-k, +k, -k),
            new(+k, +k, -k),
            new(-k, -k, +k),
            new(+k, -k, +k),
            new(-k, +k, +k),
            new(+k, +k, +k),
        ];

        Vector3[] faceNormals =
        [
            new(+1, +0, +0),
            new(-1, +0, +0),
            new(+0, +1, +0),
            new(+0, -1, +0),
            new(+0, +0, +1),
            new(+0, +0, -1),
        ];

        Vector2[] uvCoords =
        [
            new(1, 0),
            new(0, 0),
            new(0, 1),
            new(1, 1),
        ];

        const int numVertices = 6 * 4;
        var vertices = new Vertex[numVertices];
        var indices = new TriangleIndices[6 * 2];

        var vCursor = 0;
        var iCursor = 0;
        for (int f = 0; f < 6; ++f)
        {
            var faceIndices = CubeFaceIndices[f];
            for (int v = 0; v < 4; ++v)
            {
                var position = cornerVertices[faceIndices[v]];
                var normal = faceNormals[f];
                var uv = uvCoords[v];

                // Each face needs all four vertices because the normals and texture
                // coordinates are not all the same.
                vertices[vCursor++] = new Vertex
                {
                    Position = position,
                    Normal = normal,
                    Texcoord = uv
                };
            }
            // Two triangles make a square face.
            var offset = 4 * f;
            indices[iCursor++] = new TriangleIndices
            {
                A = (ushort)(offset + 0),
                B = (ushort)(offset + 1),
                C = (ushort)(offset + 2)
            };
            indices[iCursor++] = new TriangleIndices
            {
                A = (ushort)(offset + 0),
                B = (ushort)(offset + 2),
                C = (ushort)(offset + 3)
            };
        }

        return new VertexData { Vertices = vertices, Indices = indices };
    }

    /// <summary>
    /// Creates vertices for a truncated cone, which can also create cylinders and regular cones.
    /// </summary>
    public static VertexData CreateTruncatedConeVertices(
        float bottomRadius = 1f,
        float topRadius = 0f,
        float height = 1f,
        int radialSubdivisions = 24,
        int verticalSubdivisions = 1,
        bool topCap = true,
        bool bottomCap = true)
    {
        if (radialSubdivisions < 3)
        {
            throw new ArgumentException("radialSubdivisions must be 3 or greater");
        }

        if (verticalSubdivisions < 1)
        {
            throw new ArgumentException("verticalSubdivisions must be 1 or greater");
        }

        var extra = (topCap ? 2 : 0) + (bottomCap ? 2 : 0);

        var numVertices = (radialSubdivisions + 1) * (verticalSubdivisions + 1 + extra);
        var vertices = new Vertex[numVertices];
        var indices = new TriangleIndices[radialSubdivisions * (verticalSubdivisions + extra / 2) * 2];

        var vertsAroundEdge = radialSubdivisions + 1;

        // The slant of the cone is constant across its surface
        var slant = MathF.Atan2(bottomRadius - topRadius, height);
        var cosSlant = MathF.Cos(slant);
        var sinSlant = MathF.Sin(slant);

        var start = topCap ? -2 : 0;
        var end = verticalSubdivisions + (bottomCap ? 2 : 0);

        var cursor = 0;
        for (int yy = start; yy <= end; ++yy)
        {
            var v = (float)yy / verticalSubdivisions;
            var y = height * v;
            float ringRadius;
            if (yy < 0)
            {
                y = 0;
                v = 1;
                ringRadius = bottomRadius;
            }
            else if (yy > verticalSubdivisions)
            {
                y = height;
                v = 1;
                ringRadius = topRadius;
            }
            else
            {
                ringRadius = bottomRadius + (topRadius - bottomRadius) * ((float)yy / verticalSubdivisions);
            }
            if (yy == -2 || yy == verticalSubdivisions + 2)
            {
                ringRadius = 0;
                v = 0;
            }
            y -= height / 2;
            for (int ii = 0; ii < vertsAroundEdge; ++ii)
            {
                var sin = MathF.Sin(ii * MathF.PI * 2 / radialSubdivisions);
                var cos = MathF.Cos(ii * MathF.PI * 2 / radialSubdivisions);

                Vector3 position = new(sin * ringRadius, y, cos * ringRadius);
                Vector3 normal;

                // normal
                if (yy < 0)
                {
                    normal = new(0, -1, 0);
                }
                else if (yy > verticalSubdivisions)
                {
                    normal = new(0, 1, 0);
                }
                else if (ringRadius == 0.0f)
                {
                    normal = new(0, 0, 0);
                }
                else
                {
                    normal = new Vector3(sin * cosSlant, sinSlant, cos * cosSlant);
                }

                vertices[cursor++] = new Vertex
                {
                    Position = position,
                    Normal = normal,
                    Texcoord = new((float)ii / radialSubdivisions, 1 - v)
                };
            }
        }

        cursor = 0;
        for (int yy = 0; yy < verticalSubdivisions + extra; ++yy)
        {
            if ((yy == 1 && topCap) || (yy == verticalSubdivisions + extra - 2 && bottomCap))
            {
                continue;
            }
            for (int ii = 0; ii < radialSubdivisions; ++ii)
            {
                indices[cursor++] = new TriangleIndices
                {
                    A = (ushort)(vertsAroundEdge * (yy + 0) + 0 + ii),
                    B = (ushort)(vertsAroundEdge * (yy + 0) + 1 + ii),
                    C = (ushort)(vertsAroundEdge * (yy + 1) + 1 + ii)
                };

                indices[cursor++] = new TriangleIndices
                {
                    A = (ushort)(vertsAroundEdge * (yy + 0) + 0 + ii),
                    B = (ushort)(vertsAroundEdge * (yy + 1) + 1 + ii),
                    C = (ushort)(vertsAroundEdge * (yy + 1) + 0 + ii)
                };
            }
        }

        return new VertexData { Vertices = vertices, Indices = indices };
    }

    /// <summary>
    /// Creates cylinder vertices.
    /// </summary>
    public static VertexData CreateCylinderVertices(
        float radius = 1f,
        float height = 1f,
        int radialSubdivisions = 24,
        int verticalSubdivisions = 1,
        bool topCap = true,
        bool bottomCap = true)
    {
        return CreateTruncatedConeVertices(
            bottomRadius: radius,
            topRadius: radius,
            height: height,
            radialSubdivisions: radialSubdivisions,
            verticalSubdivisions: verticalSubdivisions,
            topCap: topCap,
            bottomCap: bottomCap);
    }

    /// <summary>
    /// Creates vertices for a torus.
    /// </summary>
    public static VertexData CreateTorusVertices(
        float radius = 1f,
        float thickness = 0.24f,
        int radialSubdivisions = 24,
        int bodySubdivisions = 12,
        float startAngle = 0f,
        float endAngle = MathF.PI * 2f)
    {
        if (radialSubdivisions < 3)
        {
            throw new ArgumentException("radialSubdivisions must be 3 or greater");
        }

        if (bodySubdivisions < 3)
        {
            throw new ArgumentException("bodySubdivisions must be 3 or greater");
        }

        var range = endAngle - startAngle;

        var radialParts = radialSubdivisions + 1;
        var bodyParts = bodySubdivisions + 1;
        var numVertices = radialParts * bodyParts;
        var vertices = new Vertex[numVertices];
        var indices = new TriangleIndices[radialSubdivisions * bodySubdivisions * 2];

        var cursor = 0;
        for (int slice = 0; slice < bodyParts; ++slice)
        {
            var v = (float)slice / bodySubdivisions;
            var sliceAngle = v * MathF.PI * 2;
            var sliceSin = MathF.Sin(sliceAngle);
            var ringRadius = radius + sliceSin * thickness;
            var ny = MathF.Cos(sliceAngle);
            var y = ny * thickness;
            for (int ring = 0; ring < radialParts; ++ring)
            {
                var u = (float)ring / radialSubdivisions;
                var ringAngle = startAngle + u * range;
                var xSin = MathF.Sin(ringAngle);
                var zCos = MathF.Cos(ringAngle);
                var x = xSin * ringRadius;
                var z = zCos * ringRadius;
                var nx = xSin * sliceSin;
                var nz = zCos * sliceSin;

                vertices[cursor++] = new Vertex
                {
                    Position = new(x, y, z),
                    Normal = new(nx, ny, nz),
                    Texcoord = new(u, 1 - v)
                };
            }
        }

        cursor = 0;
        for (int slice = 0; slice < bodySubdivisions; ++slice)
        {
            for (int ring = 0; ring < radialSubdivisions; ++ring)
            {
                var nextRingIndex = 1 + ring;
                var nextSliceIndex = 1 + slice;

                indices[cursor++] = new TriangleIndices
                {
                    A = (ushort)(radialParts * slice + ring),
                    B = (ushort)(radialParts * nextSliceIndex + ring),
                    C = (ushort)(radialParts * slice + nextRingIndex)
                };

                indices[cursor++] = new TriangleIndices
                {
                    A = (ushort)(radialParts * nextSliceIndex + ring),
                    B = (ushort)(radialParts * nextSliceIndex + nextRingIndex),
                    C = (ushort)(radialParts * slice + nextRingIndex)
                };
            }
        }

        return new VertexData { Vertices = vertices, Indices = indices };
    }

    /// <summary>
    /// Given indexed vertices creates a new set of vertices un-indexed by expanding the vertices by index.
    /// </summary>
    public static VertexData Deindex(VertexData src)
    {
        var numTriangles = src.Indices.Length;
        var numVertices = numTriangles * 3;
        var vertices = new Vertex[numVertices];
        var indices = new TriangleIndices[numTriangles];

        for (int i = 0; i < numTriangles; ++i)
        {
            var tri = src.Indices[i];
            var baseIdx = i * 3;
            vertices[baseIdx + 0] = src.Vertices[tri.A];
            vertices[baseIdx + 1] = src.Vertices[tri.B];
            vertices[baseIdx + 2] = src.Vertices[tri.C];
            indices[i] = new TriangleIndices
            {
                A = (ushort)(baseIdx + 0),
                B = (ushort)(baseIdx + 1),
                C = (ushort)(baseIdx + 2)
            };
        }

        return new VertexData { Vertices = vertices, Indices = indices };
    }

    /// <summary>
    /// Generate triangle normals from positions.
    /// Assumes every 3 vertices come from the same triangle.
    /// </summary>
    public static VertexData GenerateTriangleNormalsInPlace(VertexData data)
    {
        for (int ii = 0; ii < data.Vertices.Length; ii += 3)
        {
            // pull out the 3 positions for this triangle
            ref var vertex1 = ref data.Vertices[ii + 0];
            ref var vertex2 = ref data.Vertices[ii + 1];
            ref var vertex3 = ref data.Vertices[ii + 2];


            var p0 = vertex1.Position;
            var p1 = vertex2.Position;
            var p2 = vertex3.Position;

            var n0 = Vector3.Normalize(p0 - p1);
            var n1 = Vector3.Normalize(p0 - p2);
            var n = Vector3.Cross(n0, n1);

            // copy them back in
            vertex1.Normal = n;
            vertex2.Normal = n;
            vertex3.Normal = n;
        }

        return data;
    }

    /// <summary>
    /// Converts vertex data so each triangle has normals perpendicular to the triangle.
    /// </summary>
    public static VertexData Facet(VertexData vertexData)
    {
        var newData = Deindex(vertexData);
        GenerateTriangleNormalsInPlace(newData);
        return newData;
    }

    /// <summary>
    /// Reorients the vertex data by the given matrix.
    /// </summary>
    public static VertexData ReorientInPlace(VertexData vertexData, Matrix4x4 matrix)
    {
        var vertices = vertexData.Vertices;
        for (int i = 0; i < vertices.Length; i += 1)
        {
            // reorient position
            var pos = vertices[i].Position;
            var transformedPos = Vector3.Transform(pos, matrix);
            vertices[i].Position = transformedPos;
            // reorient normal (using the upper 3x3 part of the matrix)
            var normal = vertices[i].Normal;
            var transformedNormal = Vector3.TransformNormal(normal, matrix);
            vertices[i].Normal = transformedNormal;
        }

        return vertexData;
    }
}
