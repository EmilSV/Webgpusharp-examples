using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

static byte[] ToByteArray(Stream input)
{
    using MemoryStream ms = new();
    input.CopyTo(ms);
    return ms.ToArray();
}

const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = WIDTH / (float)HEIGHT;

return Run(
    name: "Fractal Cube",
    width: WIDTH,
    height: HEIGHT,
    callback: async (instance, surface, onFrame) =>
    {
        var startTimeStamp = Stopwatch.GetTimestamp();
        var executingAssembly = Assembly.GetExecutingAssembly();
        var basicVertWgsl = ToByteArray(
            executingAssembly.GetManifestResourceStream("FractalCube.basic.vert.wgsl")!
        );
        var vertexPositionColorWgsl = ToByteArray(
            executingAssembly.GetManifestResourceStream("FractalCube.sampleSelf.frag.wgsl")!
        );

        var adapter = (await instance.RequestAdapterAsync(new() { CompatibleSurface = surface }))!;

        var device = (
            await adapter.RequestDeviceAsync(
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
            )
        )!;

        var queue = device.GetQueue();
        var surfaceCapabilities = surface.GetCapabilities(adapter)!;
        var surfaceFormat = surfaceCapabilities.Formats[0];

        surface.Configure(
            new()
            {
                Width = WIDTH,
                Height = HEIGHT,
                Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
                Format = surfaceFormat,
                Device = device,
                PresentMode = PresentMode.Fifo,
                AlphaMode = CompositeAlphaMode.Auto,
            }
        );

        var verticesBuffer = device.CreateBuffer(
            new()
            {
                Size = (ulong)System.Buffer.ByteLength(Cube.CubeVertices),
                Usage = BufferUsage.Vertex,
                MappedAtCreation = true,
            }
        )!;

        verticesBuffer.GetMappedRange<float>(static data =>
        {
            Cube.CubeVertices.CopyTo(data);
        });
        verticesBuffer.Unmap();

        var pipeline = device.CreateRenderPipeline(
            new()
            {
                Layout = null,
                Vertex = ref InlineInit(
                    new VertexState()
                    {
                        Module = device.CreateShaderModuleWGSL(new() { Code = basicVertWgsl }),
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
                    }
                ),
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

        var cubeTexture = device.CreateTexture(
            new()
            {
                Label = "Cube Texture",
                Size = new(WIDTH, HEIGHT),
                Format = surfaceFormat,
                Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            }
        );

        var sampler = device.CreateSampler(
            new() { MagFilter = FilterMode.Linear, MinFilter = FilterMode.Linear }
        );

        var uniformBindGroup = device.CreateBindGroup(
            new()
            {
                Layout = pipeline.GetBindGroupLayout(0),
                Entries =
                [
                    new() { Binding = 0, Buffer = uniformBuffer },
                    new() { Binding = 1, Sampler = sampler },
                    new() { Binding = 2, TextureView = cubeTexture.CreateView()! },
                ],
            }
        );

        var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView: (float)(2.0f * Math.PI / 5.0f),
            aspectRatio: ASPECT,
            nearPlaneDistance: 1f,
            farPlaneDistance: 100.0f
        );

        Matrix4x4 getTransformationMatrix()
        {
            float now = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;
            var viewMatrix = Matrix4x4.CreateFromAxisAngle(
                axis: new(MathF.Sin(now), MathF.Cos(now), 0),
                angle: 1
            );
            viewMatrix.Translation = new(0, 0, -4);
            return viewMatrix * projectionMatrix;
        }

        onFrame(() =>
        {
            var transformationMatrix = getTransformationMatrix();
            queue.WriteBuffer(uniformBuffer, 0, transformationMatrix);

            var swapChainTexture = surface.GetCurrentTexture().Texture!;
            var swapChainTextureView = swapChainTexture.CreateView()!;

            var commandEncoder = device.CreateCommandEncoder(new());
            var passEncoder = commandEncoder.BeginRenderPass(
                new()
                {
                    ColorAttachments =
                    [
                        new()
                        {
                            View = swapChainTextureView,
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

            commandEncoder.CopyTextureToTexture(
                new() { Texture = swapChainTexture },
                new() { Texture = cubeTexture },
                new(WIDTH, HEIGHT)
            );

            queue.Submit(commandEncoder.Finish());

            surface.Present();

            var activeHandleCount = WebGpuSharp.Internal.WebGpuSafeHandle.GetTotalActiveHandles();
            if (activeHandleCount > 300)
            {
                // GC.Collect();
            }
        });
    }
);
