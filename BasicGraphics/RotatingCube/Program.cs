using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 640;
const int HEIGHT = 480;

return Run(
    "Rotating Cube",
    WIDTH,
    HEIGHT,
    async (instance, surface, onFrame) =>
    {
        var startTimeStamp = Stopwatch.GetTimestamp();
        var executingAssembly = Assembly.GetExecutingAssembly();
        var basicVertWgsl = ResourceUtils.GetEmbeddedResource("RotatingCube.shaders.basic.vert.wgsl", executingAssembly);
        var vertexPositionColorWgsl = ResourceUtils.GetEmbeddedResource("RotatingCube.shaders.vertexPositionColor.frag.wgsl", executingAssembly);

        var adapter = (await instance.RequestAdapterAsync(new() { CompatibleSurface = surface }))!;

        var device = await adapter.RequestDeviceAsync(
            new()
            {
                UncapturedErrorCallback = (type, message) =>
                {
                    var messageString = Encoding.UTF8.GetString(message);
                    Console.Error.WriteLine($"Uncaptured error: {type} {messageString}");
                },
                DeviceLostCallback = (reason, message) =>
                {
                    var messageString = Encoding.UTF8.GetString(message);
                    Console.Error.WriteLine($"Device lost: {reason} {messageString}");
                },
            }
        ) ?? throw new Exception("Could not create device");

        var queue = device.GetQueue()!;
        var surfaceCapabilities = surface.GetCapabilities(adapter)!;
        var surfaceFormat = surfaceCapabilities.Formats[0];

        var verticesBuffer = device.CreateBuffer(
            new()
            {
                Label = "Vertices Buffer",
                Size = (ulong)System.Buffer.ByteLength(Cube.CubeVertices),
                Usage = BufferUsage.Vertex,
                MappedAtCreation = true,
            }
        )!;

        verticesBuffer.GetMappedRange<float>(data =>
        {
            Cube.CubeVertices.AsSpan().CopyTo(data);
        });
        verticesBuffer.Unmap();

        surface.Configure(
            new()
            {
                Width = WIDTH,
                Height = HEIGHT,
                Usage = TextureUsage.RenderAttachment,
                Format = surfaceFormat,
                Device = device,
                PresentMode = PresentMode.Fifo,
                AlphaMode = CompositeAlphaMode.Auto,
            }
        );


        var pipeline = device.CreateRenderPipeline(
            new()
            {
                Layout = null, // Auto-layout
                Vertex = new VertexState()
                {
                    Module = device!.CreateShaderModuleWGSL(new() { Code = basicVertWgsl })!,
                    Buffers =
                    [
                        new()
                        {
                            ArrayStride = Cube.CubeVertexSize,
                            Attributes =
                            [
                                new()
                                {
                                    ShaderLocation = 0,
                                    Offset = Cube.CubePositionOffset,
                                    Format = VertexFormat.Float32x4,
                                },
                                new()
                                {
                                    ShaderLocation = 1,
                                    Offset = Cube.CubeUVOffset,
                                    Format = VertexFormat.Float32x2,
                                },
                            ],
                        },
                    ],
                },
                Fragment = new FragmentState()
                {
                    Module = device!.CreateShaderModuleWGSL(new() { Code = vertexPositionColorWgsl })!,
                    Targets = [new() { Format = surfaceFormat }],
                },
                Primitive = new()
                {
                    Topology = PrimitiveTopology.TriangleList,

                    // Backface culling since the cube is solid piece of geometry.
                    // Faces pointing away from the camera will be occluded by faces
                    // pointing toward the camera.
                    CullMode = CullMode.Back,
                },

                DepthStencil = new DepthStencilState()
                {
                    DepthWriteEnabled = OptionalBool.True,
                    DepthCompare = CompareFunction.Less,
                    Format = TextureFormat.Depth24Plus,
                },
            }
        )!;

        var depthTexture = device.CreateTexture(
            new()
            {
                Size = new(WIDTH, HEIGHT),
                Format = TextureFormat.Depth24Plus,
                Usage = TextureUsage.RenderAttachment,
            }
        )!;

        const int uniformBufferSize = 4 * 16; // 4x4 matrix
        var uniformBuffer = device.CreateBuffer(
            new()
            {
                Label = "Uniform Buffer",
                Size = uniformBufferSize,
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            }
        );

        var uniformBindGroup = device.CreateBindGroup(
            new()
            {
                Layout = pipeline.GetBindGroupLayout(0)!,
                Entries = [new BindGroupEntry() { Binding = 0, Buffer = uniformBuffer! }],
            }
        )!;

        const float aspect = WIDTH / (float)HEIGHT;
        var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView((float)(2.0f * Math.PI / 5.0f), aspect, 1f, 100.0f);

        Matrix4x4 getTransformationMatrix()
        {
            float now = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;
            var viewMatrix = Matrix4x4.CreateFromAxisAngle(new(MathF.Sin(now), MathF.Cos(now), 0), 1) with
            {
                Translation = new(0, 0, -4),
            };
            return viewMatrix * projectionMatrix;
        }

        onFrame(() =>
        {
            var transformationMatrix = getTransformationMatrix();
            queue.WriteBuffer(uniformBuffer!, 0, transformationMatrix);

            var texture = surface.GetCurrentTexture().Texture!;
            var textureView = texture.CreateView()!;

            var commandEncoder = device.CreateCommandEncoder(new());
            var passEncoder = commandEncoder.BeginRenderPass(
                new()
                {
                    ColorAttachments =
                    [
                        new()
                        {
                            View = textureView,
                            ClearValue = new(0.5, 0.5, 0.5, 1.0),
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                        },
                    ],
                    DepthStencilAttachment = new RenderPassDepthStencilAttachment()
                    {
                        View = depthTexture.CreateView()!,
                        DepthClearValue = 1.0f,
                        DepthLoadOp = LoadOp.Clear,
                        DepthStoreOp = StoreOp.Store,
                    },
                }
            );
            passEncoder.SetPipeline(pipeline);
            passEncoder.SetBindGroup(0, uniformBindGroup);
            passEncoder.SetVertexBuffer(0, verticesBuffer);
            passEncoder.Draw(Cube.CubeVertexCount);
            passEncoder.End();
            queue.Submit([commandEncoder.Finish()]);

            surface.Present();
        });
    }
);
