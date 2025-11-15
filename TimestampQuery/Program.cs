using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = (float)WIDTH / HEIGHT;

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

string? perfText = null;

var startTimeStamp = Stopwatch.GetTimestamp();
var asm = Assembly.GetExecutingAssembly();
var basicVertWGSL = ToBytes(asm.GetManifestResourceStream("TimestampQuery.shaders.basic.vert.wgsl")!);
var blackFragWGSL = ToBytes(asm.GetManifestResourceStream("TimestampQuery.shaders.black.frag.wgsl")!);

CommandBuffer DrawGui(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();

    ImGui.SetNextWindowPos(new(0, 0));
    ImGui.SetNextWindowSize(new(350, 50));
    ImGui.Begin("Timestamp Query",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoTitleBar
    );
    if (perfText != null)
    {
        ImGui.TextColored(new Vector4(0, 0, 0, 1), perfText);
    }
    else
    {
        ImGui.Text("no data");
    }
    ImGui.End();

    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}

return Run("Timestamp Query", WIDTH, HEIGHT, async (instance, surface, guiContext, onFrame) =>
{
    var adapter = await instance.RequestAdapterAsync(new()
    {
        FeatureLevel = FeatureLevel.Compatibility,
        CompatibleSurface = surface,
    });

    // The use of timestamps require a dedicated adapter feature:
    // The adapter may or may not support timestamp queries. If not, we simply
    // don't measure timestamps and deactivate the timer display.
    var supportsTimestampQueries = adapter.HasFeature(FeatureName.TimestampQuery);


    var device = await adapter.RequestDeviceAsync(new()
    {
        RequiredFeatures = supportsTimestampQueries ? [FeatureName.TimestampQuery] : [],
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
    });

    var queue = device.GetQueue();
    var surfaceCaps = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCaps.Formats[0];

    guiContext.SetupIMGUI(device, surfaceFormat);

    surface.Configure(new()
    {
        Width = WIDTH,
        Height = HEIGHT,
        Usage = TextureUsage.RenderAttachment,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Auto,
    });

    var renderPassDurationCounter = new PerfCounter();


    // GPU-side timer and the CPU-side counter where we accumulate statistics:
    // NB: Look for 'timestampQueryManager' in this file to locate parts of this
    // snippets that are related to timestamps. Most of the logic is in
    // TimestampQueryManager.cs
    var timestampQueryManager = new TimestampQueryManager(device, elapsedNs =>
    {
        double elapsedMs = elapsedNs * 1e-6;
        renderPassDurationCounter.AddSample(elapsedMs);
        perfText = $"Render Pass duration: {renderPassDurationCounter
            .GetAverage():F3} ms ± {renderPassDurationCounter.GetStddev():F3} ms";
    });

    var verticesBuffer = device.CreateBuffer(new()
    {
        Size = Cube.CubeVertexArray.GetSizeInBytes(),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    });

    verticesBuffer.GetMappedRange<(Vector4, Vector4, Vector2)>(data =>
    {
        Cube.CubeVertexArray.CopyTo(data);
    });
    verticesBuffer.Unmap();


    var pipeline = device.CreateRenderPipeline(new()
    {
        Layout = null, // Auto-layout
        Vertex = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = basicVertWGSL
            }),
            Buffers = [new()
            {
                ArrayStride = Cube.CUBE_VERTEX_SIZE,
                Attributes = [
                    new()
                    {
                        ShaderLocation = 0,
                        Offset = Cube.CUBE_POSITION_OFFSET,
                        Format = VertexFormat.Float32x4,
                    },
                    new()
                    {
                        ShaderLocation = 1,
                        Offset = Cube.CUBE_UV_OFFSET,
                        Format = VertexFormat.Float32x2,
                    }
                ],
            }],
        },
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new() { Code = blackFragWGSL }),
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
        // Enable depth testing so that the fragment closest to the camera
        // is rendered in front.
        DepthStencil = new()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth24Plus,
        },
    });

    var depthTexture = device.CreateTexture(new()
    {
        Size = new(WIDTH, HEIGHT),
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment,
    });

    var uniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<Matrix4x4>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var uniformBindGroup = device.CreateBindGroup(new()
    {
        Layout = pipeline.GetBindGroupLayout(0),
        Entries = [
            new()
            {
                Binding = 0,
                Buffer = uniformBuffer
            }
        ]
    });

    var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
        fieldOfView: 2 * MathF.PI / 5,
        aspectRatio: ASPECT,
        nearPlaneDistance: 1f,
        farPlaneDistance: 100.0f
    );

    var modelViewProjectionMatrix = new Matrix4x4();
    Matrix4x4 GetTransformationMatrix()
    {
        var viewMatrix = Matrix4x4.Identity;
        viewMatrix.Translate(new Vector3(0, 0, -4));
        var now = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;
        viewMatrix.Rotate(new Vector3(MathF.Sin(now), MathF.Cos(now), 0), 1);

        modelViewProjectionMatrix = Matrix4x4.Multiply(viewMatrix, projectionMatrix);
        return modelViewProjectionMatrix;
    }

    onFrame(() =>
    {
        var transformationMatrix = GetTransformationMatrix();
        queue.WriteBuffer(uniformBuffer, 0, transformationMatrix);

        var renderPassDescriptor = new RenderPassDescriptor()
        {
            ColorAttachments = [new()
            {
                View = surface.GetCurrentTexture().Texture!.CreateView(),
                ClearValue = new(0.95f, 0.95f, 0.95f, 1.0f),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            }],
            DepthStencilAttachment = new()
            {
                View = depthTexture.CreateView(),

                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            }
        };
        timestampQueryManager.addTimestampWrite(ref renderPassDescriptor);

        var commandEncoder = device.CreateCommandEncoder();
        var passEncoder = commandEncoder.BeginRenderPass(renderPassDescriptor);
        passEncoder.SetPipeline(pipeline);
        passEncoder.SetBindGroup(0, uniformBindGroup);
        passEncoder.SetVertexBuffer(0, verticesBuffer);
        passEncoder.Draw(Cube.CUBE_VERTEX_COUNT);
        passEncoder.End();

        // Resolve timestamp queries, so that their result is available in
        // a GPU-side buffer.
        timestampQueryManager.Resolve(commandEncoder);

        var guiCommandBuffer = DrawGui(guiContext, surface);

        queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);

        // Try to download the time stamp.
        timestampQueryManager.TryInitiateTimestampDownload();

        surface.Present();
    });
});