﻿using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

static byte[] ToByteArray(Stream input)
{
    using MemoryStream ms = new();
    input.CopyTo(ms);
    return ms.ToArray();
}

const int WIDTH = 600;
const int HEIGHT = 600;

return Run(
    "Cubemap",
    WIDTH,
    HEIGHT,
    async (instance, surface, guiContext, onFrame) =>
    {
        var startTimeStamp = Stopwatch.GetTimestamp();

        var executingAssembly = Assembly.GetExecutingAssembly();
        var basicVertWGSL = ToByteArray(
            executingAssembly.GetManifestResourceStream("Cubemap.basic.vert.wgsl")!
        );
        var sampleCubemapWGSL = ToByteArray(
            executingAssembly.GetManifestResourceStream("Cubemap.sampleCubemap.frag.wgsl")!
        );

        var adapter = (await instance.RequestAdapterAsync(new() { CompatibleSurface = surface }))!;

        var device = await adapter.RequestDeviceAsync(
            new()
            {
                UncapturedErrorCallback = (type, message) =>
                {
                    var messageString = Encoding.UTF8.GetString(message);
                    Console.Error.WriteLine($"Uncaptured error: {type} {messageString}");
                    Environment.Exit(1);
                },
                DeviceLostCallback = (reason, message) =>
                {
                    var messageString = Encoding.UTF8.GetString(message);
                    Console.Error.WriteLine($"Device lost: {reason} {messageString}");
                },
            }
        );


        var queue = device.GetQueue()!;
        var surfaceCapabilities = surface.GetCapabilities(adapter)!;
        var surfaceFormat = surfaceCapabilities.Formats[0];

        guiContext.SetupIMGUI(device, surfaceFormat);

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

        var pipeline = device.CreateRenderPipeline(
            new()
            {
                Layout = null,
                Vertex = ref InlineInit(
                    new VertexState()
                    {
                        Module = device!.CreateShaderModuleWGSL(new() { Code = basicVertWGSL })!,
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
                    Module = device!.CreateShaderModuleWGSL(new() { Code = sampleCubemapWGSL })!,
                    Targets = [new() { Format = surfaceFormat }],
                },
                Primitive = new()
                {
                    Topology = PrimitiveTopology.TriangleList,

                    // Backface culling since the cube is solid piece of geometry.
                    // Faces pointing away from the camera will be occluded by faces
                    // pointing toward the camera.
                    CullMode = CullMode.None,
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

        Texture cubemapTexture;
        {
            string[] imgSrcs =
            [
                "Cubemap.cubemap.posx.jpg",
                "Cubemap.cubemap.negx.jpg",
                "Cubemap.cubemap.posy.jpg",
                "Cubemap.cubemap.negy.jpg",
                "Cubemap.cubemap.posz.jpg",
                "Cubemap.cubemap.negz.jpg",
            ];

            var imgTasks = imgSrcs
                .Select(imagePath =>
                {
                    var stream = executingAssembly.GetManifestResourceStream(imagePath)!;
                    var bytes = ImageUtils.LoadImage(stream, out var width, out var height);
                    return (width, height, bytes);
                })
                .ToArray();

            var (width, height, _) = imgTasks[0];

            cubemapTexture = device.CreateTexture(
                new()
                {
                    Dimension = TextureDimension.D2,
                    Size = new Extent3D((uint)width, (uint)height, (uint)imgTasks.Length),
                    Format = TextureFormat.RGBA8Unorm,
                    Usage =
                        TextureUsage.TextureBinding
                        | TextureUsage.CopyDst
                        | TextureUsage.RenderAttachment,
                }
            );

            for (int i = 0; i < imgTasks.Length; i++)
            {
                var bytes = imgTasks[i].bytes;
                queue.WriteTexture(
                    destination: new() { Texture = cubemapTexture, Origin = new(0, 0, (uint)i) },
                    data: bytes,
                    dataLayout: new() { BytesPerRow = 4 * (uint)width, RowsPerImage = (uint)height },
                    writeSize: new((uint)width, (uint)height)
                );
            }
        }

        const int uniformBufferSize = 4 * 16; // 4x4 matrix
        var uniformBuffer = device.CreateBuffer(
            new()
            {
                Label = "Uniform Buffer",
                Size = uniformBufferSize,
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            }
        );

        var sampler = device.CreateSampler(
            new() { MagFilter = FilterMode.Linear, MinFilter = FilterMode.Linear }
        );

        var uniformBindGroup = device.CreateBindGroup(
            new()
            {
                Layout = pipeline.GetBindGroupLayout(0)!,
                Entries =
                [
                    new() { Binding = 0, Buffer = uniformBuffer! },
                    new() { Binding = 1, Sampler = sampler! },
                    new()
                    {
                        Binding = 2,
                        TextureView = cubemapTexture.CreateView(
                            new() {
                                Dimension = TextureViewDimension.Cube }
                        ),
                    },
                ],
            }
        )!;

        const float aspect = WIDTH / (float)HEIGHT;
        var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
            (float)(2.0f * Math.PI / 5.0f),
            aspect,
            1f,
            3000
        );

        var modelMatrix = Matrix4x4.CreateScale(1000);

        Controls controls = new();

        Matrix4x4 getModelViewProjectionMatrix()
        {
            float now = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;
            var viewMatrix = Matrix4x4.CreateFromAxisAngle(
                axis: new(1, 0, 0),
                angle: MathF.PI / 10 * MathF.Sin(now)
            );
            viewMatrix.Rotate(new(0, 1, 0), now * controls.Speed);
            return viewMatrix * modelMatrix * projectionMatrix;
        }

        onFrame(() =>
        {
            var modelViewProjectionMatrix = getModelViewProjectionMatrix();
            queue.WriteBuffer(uniformBuffer, 0, modelViewProjectionMatrix);

            var texture = surface.GetCurrentTexture().Texture!;
            var textureView = texture.CreateView();

            var commandEncoder = device.CreateCommandEncoder(new());
            var passEncoder = commandEncoder.BeginRenderPass(
                new()
                {
                    ColorAttachments =
                    [
                        new()
                        {
                            View = textureView,
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

            guiContext.NewFrame();

            controls.Draw();

            guiContext.EndFrame();

            var guiCommandBuffer = guiContext.Render(surface)!.Value;

            passEncoder.End();
            queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);

            surface.Present();
        });
    }
);


class Controls
{
    public float Speed = 0.2f;

    public void Draw()
    {
        ImGui.SetNextWindowPos(new(400, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new(200, 100), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.3f);
        ImGui.Begin("Controls",
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse
        );
        ImGui.InputFloat("Speed", ref Speed);
        ImGui.End();
    }
}