using System.Numerics;
using System.Runtime.CompilerServices;
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

namespace Cornell;

/// <summary>
/// Sets up the shared uniform state used across the Cornell box pipelines.
/// </summary>
public sealed class Common
{
	private struct CommonUniforms
	{
		// Model View Projection matrix
		public Matrix4x4 Mvp;
		// Inverse of mvp
		public Matrix4x4 InvMvp;
		// Random seed for the workgroup
		public Vector3 Seed;

		private readonly float _pad0;
	}

	/// <summary>The WGSL of the common shader.</summary>
	public static Lazy<byte[]> Wgsl = new(
		() => ResourceUtils.GetEmbeddedResource($"Cornell.shaders.common.wgsl", typeof(Common).Assembly)
	);

	public static Lazy<string> WgslStr = new(
		() => System.Text.Encoding.UTF8.GetString(Wgsl.Value)
	);

	private readonly Queue _queue;
	private readonly GPUBuffer _uniformBuffer;
	private readonly Random _rng = new();
	private ulong _frame;

	/// <summary>Bind group layout for the common uniforms and quad storage buffer.</summary>
	public BindGroupLayout UniformBindGroupLayout { get; }

	/// <summary>Bind group that binds the shared uniform buffer and quads.</summary>
	public BindGroup UniformBindGroup { get; }

	public Common(Device device, GPUBuffer quadBuffer)
	{
		_queue = device.GetQueue();

		_uniformBuffer = device.CreateBuffer(new()
		{
			Label = "Common.uniformBuffer",
			Size = (ulong)Unsafe.SizeOf<CommonUniforms>(),
			Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
		});

		UniformBindGroupLayout = device.CreateBindGroupLayout(new()
		{
			Label = "Common.bindGroupLayout",
			Entries =
			[
				new()
				{
					Binding = 0,
					Visibility = ShaderStage.Vertex | ShaderStage.Compute,
					Buffer = new BufferBindingLayout
					{
						Type = BufferBindingType.Uniform,
					},
				},
				new()
				{
					Binding = 1,
					Visibility = ShaderStage.Compute,
					Buffer = new BufferBindingLayout
					{
						Type = BufferBindingType.ReadOnlyStorage,
					},
				},
			],
		});

		UniformBindGroup = device.CreateBindGroup(new()
		{
			Label = "Common.bindGroup",
			Layout = UniformBindGroupLayout,
			Entries =
			[
				new()
				{
					Binding = 0,
					Buffer = _uniformBuffer,
					Size = _uniformBuffer.GetSize(),
				},
				new()
				{
					Binding = 1,
					Buffer = quadBuffer,
					Size = quadBuffer.GetSize(),
				},
			],
		});
	}




	/// <summary>
	/// Update the uniform buffer with the latest camera matrices and random seeds.
	/// </summary>
	public void Update(bool rotateCamera, float aspect)
	{
		var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
			fieldOfView: 2f * MathF.PI / 8f,
			aspectRatio: aspect,
			nearPlaneDistance: 0.5f,
			farPlaneDistance: 100f
		);

		float viewRotation = rotateCamera ? _frame / 1000f : 0f;

		var viewMatrix = Matrix4x4.CreateLookAt(
			new(
				x: MathF.Sin(viewRotation) * 15f,
				y: 5f,
				z: MathF.Cos(viewRotation) * 15f
			),
			new(0f, 5f, 0f),
			new(0f, 1f, 0f)
		);

		var mvp = Matrix4x4.Multiply(viewMatrix, projectionMatrix);
		Matrix4x4.Invert(mvp, out var invMvp);

		CommonUniforms uniformsData = new()
		{
			Mvp = mvp,
			InvMvp = invMvp,
			Seed = new(
				x: 0xffffffff * _rng.NextSingle(),
				y: 0xffffffff * _rng.NextSingle(),
				z: 0xffffffff * _rng.NextSingle()
			)
		};

		_queue.WriteBuffer(_uniformBuffer, 0, uniformsData);
		_frame++;
	}
}
