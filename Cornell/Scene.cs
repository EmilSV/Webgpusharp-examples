using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

namespace Cornell;

/// <summary>
/// Scene holds the cornell-box scene information.
/// </summary>
public sealed class Scene
{
	private struct QuadUniform
	{
		public Vector4 Plane;
		public Vector4 Right;
		public Vector4 Up;
		public Vector3 Color;
		public float Emissive;
	}

	private struct VertexInUniform
	{
		public Vector4 Position;
		public Vector3 Uv;
		public Vector3 Emissive;
	};

	private const int VerticesPerQuad = 4;
	[InlineArray(VerticesPerQuad)]
	private struct VertexInUniformPerQuad
	{
		private VertexInUniform _element0;

		[UnscopedRef]
		public ref VertexInUniform this[int i]
		{
			get => ref this[i];
		}
	}
	private const int IndicesPerQuad = 6;
	[InlineArray(IndicesPerQuad)]
	private struct IndexPerQuad
	{
		private ushort _element0;

		[UnscopedRef]
		public ref ushort this[int i]
		{
			get => ref this[i];
		}
	}


	private const int QuadFloatCount = 16;
	private const int VertexFloatStride = 10;
	

	private static readonly Quad Light = new()
	{
		Center = new Vector3(0f, 9.95f, 0f),
		Right = new Vector3(1f, 0f, 0f),
		Up = new Vector3(0f, 0f, 1f),
		Color = new Vector3(5f, 5f, 5f),
		Emissive = 1f,
	};

	public readonly uint VertexCount;
	public readonly uint IndexCount;
	public GPUBuffer Vertices;
	public GPUBuffer Indices;
	public VertexBufferLayout[] VertexBufferLayout;
	public GPUBuffer QuadBuffer;

	public readonly Quad[] Quads = [
		..Box(
			center: new(0f, 5f, 0f),
			width: 10f,
			height: 10f,
			depth: 10f,
			rotation: 0f,
			colors:
			[
				new(0.0f, 0.5f, 0.0f),
				new(0.5f, 0.5f, 0.5f),
				new(0.5f, 0.5f, 0.5f),
				new(0.5f, 0.0f, 0.0f),
				new(0.5f, 0.5f, 0.5f),
				new(0.5f, 0.5f, 0.5f),
			],
			concave: true
		),
		..Box(
			center: new(1.5f, 1.5f, 1f),
			width: 3f,
			height: 3f,
			depth: 3f,
			rotation: 0.3f,
			color: new(0.8f, 0.8f, 0.8f) ,
			concave: false
		),
		..Box(
			center: new(-2f, 3f, -2f),
			width: 3f,
			height: 6f,
			depth: 3f,
			rotation: -0.4f,
			color: new(0.8f, 0.8f, 0.8f),
			concave: false
		),
		Light
	];

	public readonly Vector3 LightCenter = Light.Center;
	public readonly float LightWidth = Light.Right.Length() * 2f;
	public readonly float LightHeight = Light.Up.Length() * 2f;

	public Scene(Device device)
	{
		var quadBuffer = device.CreateBuffer(new()
		{
			Size = (ulong)(Unsafe.SizeOf<QuadUniform>() * Quads.Length),
			Usage = BufferUsage.Storage,
			MappedAtCreation = true,
		});

		var vertexBuffer = device.CreateBuffer(new()
		{
			Size = (ulong)(Unsafe.SizeOf<VertexInUniformPerQuad>() * Quads.Length),
			Usage = BufferUsage.Vertex,
			MappedAtCreation = true,
		});

		var indexBuffer = device.CreateBuffer(new()
		{
			Size = (ulong)(Unsafe.SizeOf<IndexPerQuad>() * Quads.Length),
			Usage = BufferUsage.Index,
			MappedAtCreation = true,
		});

		GPUBuffer.DoReadWriteOperation([quadBuffer, vertexBuffer, indexBuffer], context =>
		{
			var quadSpan = context.GetMappedRange<QuadUniform>(quadBuffer);
			var vertexSpan = context.GetMappedRange<VertexInUniformPerQuad>(vertexBuffer);
			var indexSpan = context.GetMappedRange<IndexPerQuad>(indexBuffer);

			Debug.Assert(quadSpan.Length == Quads.Length);
			Debug.Assert(vertexSpan.Length == Quads.Length);
			Debug.Assert(indexSpan.Length == Quads.Length);

			for (int quadIdx = 0; quadIdx < Quads.Length; quadIdx++)
			{
				ref var quad = ref Quads[quadIdx];
				var normal = Vector3.Normalize(Vector3.Cross(quad.Right, quad.Up));

				var invRight = Reciprocal(quad.Right);
				var invUp = Reciprocal(quad.Up);

				quadSpan[quadIdx] = new()
				{
					Plane = new(normal, -Vector3.Dot(normal, quad.Center)),
					Right = new(invRight, -Vector3.Dot(invRight, quad.Center)),
					Up = new(invUp, -Vector3.Dot(invUp, quad.Center)),
					Color = quad.Color,
					Emissive = quad.Emissive,
				};

				var a = quad.Center - quad.Right + quad.Up;
				var b = quad.Center + quad.Right + quad.Up;
				var c = quad.Center - quad.Right - quad.Up;
				var d = quad.Center + quad.Right - quad.Up;

				vertexSpan[quadIdx] = new()
				{
					[0] = new()
					{
						Position = new(a, 1f),
						Uv = new(0f, 1f, quadIdx),
						Emissive = quad.Color * quad.Emissive,
					},
					[1] = new()
					{
						Position = new(b, 1f),
						Uv = new(1f, 1f, quadIdx),
						Emissive = quad.Color * quad.Emissive,
					},
					[2] = new()
					{
						Position = new(c, 1f),
						Uv = new(0f, 0f, quadIdx),
						Emissive = quad.Color * quad.Emissive,
					},
					[3] = new()
					{
						Position = new(d, 1f),
						Uv = new(1f, 0f, quadIdx),
						Emissive = quad.Color * quad.Emissive,
					},
				};

				ushort baseIndex = (ushort)(quadIdx * VerticesPerQuad);
				indexSpan[quadIdx] = new()
				{
					[0] = baseIndex,
					[1] = (ushort)(baseIndex + 2),
					[2] = (ushort)(baseIndex + 1),
					[3] = (ushort)(baseIndex + 1),
					[4] = (ushort)(baseIndex + 2),
					[5] = (ushort)(baseIndex + 3),
				};
			}

		});

		quadBuffer.Unmap();
		vertexBuffer.Unmap();
		indexBuffer.Unmap();

		var vertexBufferLayout = new VertexBufferLayout[]
		{
			new()
			{
				ArrayStride = (ulong)Unsafe.SizeOf<VertexInUniform>(),
				Attributes =
				[
					// position
					new()
					{
						ShaderLocation = 0,
						Offset = (ulong)Marshal.OffsetOf<VertexInUniform>(nameof(VertexInUniform.Position)),
						Format = VertexFormat.Float32x4,
					},
					// uv
					new()
					{
						ShaderLocation = 1,
						Offset = (ulong)Marshal.OffsetOf<VertexInUniform>(nameof(VertexInUniform.Uv)),
						Format = VertexFormat.Float32x3,
					},

					// color
					new()
					{
						ShaderLocation = 2,
						Offset = (ulong)Marshal.OffsetOf<VertexInUniform>(nameof(VertexInUniform.Emissive)),
						Format = VertexFormat.Float32x3,
					},
				],
			},
		};

		VertexCount = (uint)(Quads.Length * VerticesPerQuad);
		IndexCount = (uint)(Quads.Length * IndicesPerQuad);
		Vertices = vertexBuffer;
		Indices = indexBuffer;
		VertexBufferLayout = vertexBufferLayout;
		QuadBuffer = quadBuffer;
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

	private static Quad[] Box(
		Vector3 center,
		float width,
		float height,
		float depth,
		float rotation,
		Vector3 color,
		bool concave)
	{
		Span<Vector3> colorSpan = stackalloc Vector3[6];
		colorSpan.Fill(color);
		return Box(center, width, height, depth, rotation, colorSpan, concave);
	}

	private static Quad[] Box(
		Vector3 center,
		float width,
		float height,
		float depth,
		float rotation,
		ReadOnlySpan<Vector3> colors,
		bool concave)
	{
		var x = new Vector3(
			x: MathF.Cos(rotation) * width / 2f,
			y: 0f,
			z: MathF.Sin(rotation) * depth / 2f
		);
		var y = new Vector3(
			x: 0f,
			y: height / 2f,
			z: 0f
		);
		var z = new Vector3(
			x: MathF.Sin(rotation) * width / 2f,
			y: 0f,
			z: -MathF.Cos(rotation) * depth / 2f
		);

		Vector3 Sign(Vector3 v) => concave ? v : -v;

		return [
			new()
			{
				Center = center + x,
				Right = Sign(-z),
				Up = y,
				Color = colors[0],
			},
			new()
			{
				Center = center + y,
				Right = Sign(x),
				Up = -z,
				Color = colors[1],
			},
			new()
			{
				Center = center + z,
				Right = Sign(x),
				Up = y,
				Color = colors[2],
			},
			new()
			{
				Center = center - x,
				Right = Sign(z),
				Up = y,
				Color = colors[3],
			},
			new()
			{
				Center = center - y,
				Right = Sign(x),
				Up = z,
				Color = colors[4],
			},
			new()
			{
				Center = center - z,
				Right = Sign(-x),
				Up = y,
				Color = colors[5],
			}
		];
	}
}