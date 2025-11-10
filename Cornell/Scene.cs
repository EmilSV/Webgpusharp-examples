using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

namespace Cornell;

/// <summary>
/// Describes the Cornell box geometry and uploads the buffers required by the GPU pipelines.
/// </summary>
public sealed class Scene
{
	private const int QuadFloatCount = 16;
	private const int VerticesPerQuad = 4;
	private const int IndicesPerQuad = 6;
	private const int VertexFloatStride = 10;

	private static readonly Vector3 LightCenterPosition = new(0f, 9.95f, 0f);
	private static readonly Vector3 LightRight = new(1f, 0f, 0f);
	private static readonly Vector3 LightUp = new(0f, 0f, 1f);
	private static readonly Vector3 LightColor = new(5f, 5f, 5f);

	public List<Quad> Quads { get; private set; }

	public Scene(Device device)
	{
		Quads = BuildScene();

		Span<float> quadData = stackalloc float[Quads.Count * QuadFloatCount];
		Span<float> vertexData = stackalloc float[Quads.Count * VerticesPerQuad * VertexFloatStride];
		Span<ushort> indexData = stackalloc ushort[Quads.Count * IndicesPerQuad];
		FillQuadData(Quads, quadData);
		FillVertexAndIndexData(Quads, vertexData, indexData, out uint vertexCount, out uint indexCount);

		var quadArray = quadData.ToArray();
		var vertexArray = vertexData.ToArray();
		var indexArray = indexData.ToArray();

		QuadBuffer = CreateBuffer(device, "Scene.quadBuffer", quadArray, BufferUsage.Storage);
		Vertices = CreateBuffer(device, "Scene.vertices", vertexArray, BufferUsage.Vertex);
		Indices = CreateBuffer(device, "Scene.indices", indexArray, BufferUsage.Index);

		VertexCount = vertexCount;
		IndexCount = indexCount;

		VertexBufferLayout =
		[
			new VertexBufferLayout
			{
				ArrayStride = (ulong)(VertexFloatStride * sizeof(float)),
				Attributes =
				[
					new VertexAttribute
					{
						ShaderLocation = 0,
						Offset = 0,
						Format = VertexFormat.Float32x4,
					},
					new VertexAttribute
					{
						ShaderLocation = 1,
						Offset = 4 * sizeof(float),
						Format = VertexFormat.Float32x3,
					},
					new VertexAttribute
					{
						ShaderLocation = 2,
						Offset = 7 * sizeof(float),
						Format = VertexFormat.Float32x3,
					},
				],
			},
		];

		LightCenter = LightCenterPosition;
		LightWidth = LightRight.Length() * 2f;
		LightHeight = LightUp.Length() * 2f;
	}

	public GPUBuffer QuadBuffer { get; }
	public GPUBuffer Vertices { get; }
	public GPUBuffer Indices { get; }
	public VertexBufferLayout[] VertexBufferLayout { get; }
	public uint VertexCount { get; }
	public uint IndexCount { get; }
	public Vector3 LightCenter { get; }
	public float LightWidth { get; }
	public float LightHeight { get; }

	private static GPUBuffer CreateBuffer<T>(Device device, string label, T[] data, BufferUsage usage)
		where T : unmanaged
	{
		var buffer = device.CreateBuffer(new()
		{
			Label = label,
			Size = (ulong)(data.Length * Unsafe.SizeOf<T>()),
			Usage = usage,
			MappedAtCreation = true,
		});

		buffer.GetMappedRange<T>(span => data.CopyTo(span));
		buffer.Unmap();
		return buffer;
	}

	private static void FillQuadData(IReadOnlyList<Quad> quads, Span<float> quadData)
	{
		int offset = 0;
		foreach (var quad in quads)
		{
			var normal = Vector3.Normalize(Vector3.Cross(quad.Right, quad.Up));

			quadData[offset++] = normal.X;
			quadData[offset++] = normal.Y;
			quadData[offset++] = normal.Z;
			quadData[offset++] = -Vector3.Dot(normal, quad.Center);

			var invRight = Reciprocal(quad.Right);
			quadData[offset++] = invRight.X;
			quadData[offset++] = invRight.Y;
			quadData[offset++] = invRight.Z;
			quadData[offset++] = -Vector3.Dot(invRight, quad.Center);

			var invUp = Reciprocal(quad.Up);
			quadData[offset++] = invUp.X;
			quadData[offset++] = invUp.Y;
			quadData[offset++] = invUp.Z;
			quadData[offset++] = -Vector3.Dot(invUp, quad.Center);

			quadData[offset++] = quad.Color.X;
			quadData[offset++] = quad.Color.Y;
			quadData[offset++] = quad.Color.Z;
			quadData[offset++] = quad.Emissive;
		}
	}

	private static void FillVertexAndIndexData(IReadOnlyList<Quad> quads, Span<float> vertexData, Span<ushort> indexData, out uint vertexCount, out uint indexCount)
	{
		vertexCount = 0;
		indexCount = 0;
		int vertexOffset = 0;
		int indexOffset = 0;

		for (int quadIndex = 0; quadIndex < quads.Count; quadIndex++)
		{
			var quad = quads[quadIndex];

			var a = Vector3.Add(Vector3.Subtract(quad.Center, quad.Right), quad.Up);
			var b = Vector3.Add(Vector3.Add(quad.Center, quad.Right), quad.Up);
			var c = Vector3.Subtract(Vector3.Subtract(quad.Center, quad.Right), quad.Up);
			var d = Vector3.Subtract(Vector3.Add(quad.Center, quad.Right), quad.Up);

			float emissiveR = quad.Color.X * quad.Emissive;
			float emissiveG = quad.Color.Y * quad.Emissive;
			float emissiveB = quad.Color.Z * quad.Emissive;

			WriteVertex(vertexData, ref vertexOffset, a, new Vector2(0f, 1f), quadIndex, emissiveR, emissiveG, emissiveB);
			WriteVertex(vertexData, ref vertexOffset, b, new Vector2(1f, 1f), quadIndex, emissiveR, emissiveG, emissiveB);
			WriteVertex(vertexData, ref vertexOffset, c, new Vector2(0f, 0f), quadIndex, emissiveR, emissiveG, emissiveB);
			WriteVertex(vertexData, ref vertexOffset, d, new Vector2(1f, 0f), quadIndex, emissiveR, emissiveG, emissiveB);

			ushort baseIndex = (ushort)vertexCount;
			indexData[indexOffset++] = baseIndex;
			indexData[indexOffset++] = (ushort)(baseIndex + 2);
			indexData[indexOffset++] = (ushort)(baseIndex + 1);
			indexData[indexOffset++] = (ushort)(baseIndex + 1);
			indexData[indexOffset++] = (ushort)(baseIndex + 2);
			indexData[indexOffset++] = (ushort)(baseIndex + 3);

			vertexCount += VerticesPerQuad;
			indexCount += IndicesPerQuad;
		}
	}

	private static void WriteVertex(Span<float> vertexData, ref int offset, Vector3 position, Vector2 uv, int quadIndex, float emissiveR, float emissiveG, float emissiveB)
	{
		vertexData[offset++] = position.X;
		vertexData[offset++] = position.Y;
		vertexData[offset++] = position.Z;
		vertexData[offset++] = 1f;
		vertexData[offset++] = uv.X;
		vertexData[offset++] = uv.Y;
		vertexData[offset++] = quadIndex;
		vertexData[offset++] = emissiveR;
		vertexData[offset++] = emissiveG;
		vertexData[offset++] = emissiveB;
	}

	private static Vector3 Reciprocal(Vector3 v)
	{
		float lenSq = v.LengthSquared();
		if (lenSq == 0f)
		{
			return Vector3.Zero;
		}
		float scale = 1f / lenSq;
		return v * scale;
	}

	private static List<Quad> BuildScene()
	{
		var quads = new List<Quad>();

		quads.AddRange(CreateBox(
			center: new Vector3(0f, 5f, 0f),
			width: 10f,
			height: 10f,
			depth: 10f,
			rotation: 0f,
			colors: new[]
			{
				new Vector3(0.0f, 0.5f, 0.0f),
				new Vector3(0.5f, 0.5f, 0.5f),
				new Vector3(0.5f, 0.5f, 0.5f),
				new Vector3(0.5f, 0.0f, 0.0f),
				new Vector3(0.5f, 0.5f, 0.5f),
				new Vector3(0.5f, 0.5f, 0.5f),
			},
			concave: true));

		quads.AddRange(CreateBox(
			center: new Vector3(1.5f, 1.5f, 1f),
			width: 3f,
			height: 3f,
			depth: 3f,
			rotation: 0.3f,
			colors: new[] { new Vector3(0.8f, 0.8f, 0.8f) },
			concave: false));

		quads.AddRange(CreateBox(
			center: new Vector3(-2f, 3f, -2f),
			width: 3f,
			height: 6f,
			depth: 3f,
			rotation: -0.4f,
			colors: new[] { new Vector3(0.8f, 0.8f, 0.8f) },
			concave: false));

		quads.Add(new Quad
		{
			Center = LightCenterPosition,
			Right = LightRight,
			Up = LightUp,
			Color = LightColor,
			Emissive = 1f,
		});

		return quads;
	}

	private static IEnumerable<Quad> CreateBox(Vector3 center, float width, float height, float depth, float rotation, IReadOnlyList<Vector3> colors, bool concave)
	{
		var colorArray = colors.Count == 6
			? colors
			: new[] { colors[0], colors[0], colors[0], colors[0], colors[0], colors[0] };

		var halfWidth = width / 2f;
		var halfHeight = height / 2f;
		var halfDepth = depth / 2f;

		var x = new Vector3(MathF.Cos(rotation) * halfWidth, 0f, MathF.Sin(rotation) * halfDepth);
		var y = new Vector3(0f, halfHeight, 0f);
		var z = new Vector3(MathF.Sin(rotation) * halfWidth, 0f, -MathF.Cos(rotation) * halfDepth);

		Vector3 Adjust(Vector3 v) => concave ? v : -v;

		yield return new Quad
		{
			Center = center + x,
			Right = Adjust(-z),
			Up = y,
			Color = colorArray[0],
		};
		yield return new Quad
		{
			Center = center + y,
			Right = Adjust(x),
			Up = -z,
			Color = colorArray[1],
		};
		yield return new Quad
		{
			Center = center + z,
			Right = Adjust(x),
			Up = y,
			Color = colorArray[2],
		};
		yield return new Quad
		{
			Center = center - x,
			Right = Adjust(z),
			Up = y,
			Color = colorArray[3],
		};
		yield return new Quad
		{
			Center = center - y,
			Right = Adjust(x),
			Up = z,
			Color = colorArray[4],
		};
		yield return new Quad
		{
			Center = center - z,
			Right = Adjust(-x),
			Up = y,
			Color = colorArray[5],
		};
	}
}
