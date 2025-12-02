
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

var asm = Assembly.GetExecutingAssembly();
var cubeWGSL = ResourceUtils.GetEmbeddedResource("Cameras.shaders.cube.wgsl", asm)!;
var currentCameraType = CameraType.WASD;

CommandBuffer DrawGUI(GuiContext guiContext, Surface surface, out bool changeCameraType)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(340, 0));
    ImGui.SetNextWindowSize(new(300, 40));
    ImGui.Begin("Cameras",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoTitleBar
    );

    changeCameraType = ImGuiUtils.EnumDropdown("Camera Type", ref currentCameraType);

    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}

return Run("Cameras", WIDTH, HEIGHT, async runContext =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();
    var inputEvents = runContext.Input;
    var inputHandler = new InputHandler(inputEvents);


    var initialCameraPosition = new Vector3(3, 2, 5);

    var camerasArcball = new ArcballCamera(initialCameraPosition);
    var camerasWASD = new WASDCamera(initialCameraPosition);

    var adapter = await instance.RequestAdapterAsync(new()
    {
        CompatibleSurface = surface,
        FeatureLevel = FeatureLevel.Compatibility
    });

    var device = await adapter.RequestDeviceAsync(new()
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
    });

    var query = device.GetQueue();

    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

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

    // Create a vertex buffer from the cube data.
    var verticesBuffer = device.CreateBuffer(new()
    {
        Size = Cube.CubeVertexArray.GetSizeInBytes(),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    });
    verticesBuffer.GetMappedRange<float>(
        callback: data => Cube.CubeVertexArray.AsSpan().CopyTo(data)
    );
    verticesBuffer.Unmap();

    var pipeline = device.CreateRenderPipeline(new()
    {
        Layout = null!,
        Vertex = new()
        {
            Module = device.CreateShaderModuleWGSL(new() { Code = cubeWGSL }),
            Buffers = [
                new()
                {
                    ArrayStride = Cube.CUBE_VERTEX_SIZE,
                    Attributes = [
                        new()
                        {
                            // position
                            ShaderLocation = 0,
                            Offset = Cube.CUBE_POSITION_OFFSET,
                            Format = VertexFormat.Float32x4
                        },
                        new()
                        {
                            // uv
                            ShaderLocation = 1,
                            Offset = Cube.CUBE_UV_OFFSET,
                            Format = VertexFormat.Float32x2
                        }
                    ]
                }
            ]
        },
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new() { Code = cubeWGSL }),
            Targets = [new() { Format = surfaceFormat }]
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,
            CullMode = CullMode.Back,
        },
        DepthStencil = new()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth24Plus,
        },
    });

    var depthTexture = device.CreateTexture(new()
    {
        Size = new() { Width = WIDTH, Height = HEIGHT },
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment,
    });

    var uniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<Matrix4x4>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    // Fetch the image and upload it into a GPUTexture.
    Texture cubeTexture;

    using var imageStream = ResourceUtils.GetEmbeddedResourceStream("Cameras.assets.Di-3d.png", asm);
    var imageBitmap = ResourceUtils.LoadImage(imageStream!);

    cubeTexture = device.CreateTexture(new()
    {
        Size = new() { Width = imageBitmap.Width, Height = imageBitmap.Height },
        Format = TextureFormat.RGBA8Unorm,
        Usage =
            TextureUsage.TextureBinding |
            TextureUsage.CopyDst |
            TextureUsage.RenderAttachment,
    });
    ResourceUtils.CopyExternalImageToTexture(
        queue: query,
        source: imageBitmap,
        texture: cubeTexture,
        width: imageBitmap.Width,
        height: imageBitmap.Height
    );

    // Create a sampler with linear filtering for smooth interpolation.
    var sampler = device.CreateSampler(new()
    {
        MagFilter = FilterMode.Linear,
        MinFilter = FilterMode.Linear,
    });

    var uniformBindGroup = device.CreateBindGroup(new()
    {
        Layout = pipeline.GetBindGroupLayout(0),
        Entries = [
            new()
            {
                Binding = 0,
                Buffer = uniformBuffer
            },
            new()
            {
                Binding = 1,
                Sampler = sampler
            },
            new()
            {
                Binding = 2,
                TextureView = cubeTexture.CreateView()
            }
        ]
    });

    var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
        fieldOfView: (2f * MathF.PI) / 5f,
        aspectRatio: ASPECT,
        nearPlaneDistance: 1f,
        farPlaneDistance: 100.0f
    );

    var modelViewProjectionMatrix = Matrix4x4.Identity;

    Matrix4x4 GetModelViewProjectionMatrix(float deltaTime)
    {
        BaseCamera camera = currentCameraType switch
        {
            CameraType.Arcball => camerasArcball,
            CameraType.WASD => camerasWASD,
            _ => throw new ArgumentOutOfRangeException()
        };
        var viewMatrix = camera.Update(deltaTime, inputHandler.GetInput());
        modelViewProjectionMatrix = Matrix4x4.Multiply(viewMatrix, projectionMatrix);
        return modelViewProjectionMatrix;
    }

    var lastFrameTimeStamp = Stopwatch.GetTimestamp();

    runContext.OnFrame += () =>
    {
        var currentTimeStamp = Stopwatch.GetTimestamp();
        var deltaTime = (float)(Stopwatch.GetElapsedTime(lastFrameTimeStamp, currentTimeStamp).TotalMilliseconds / 1000.0);
        lastFrameTimeStamp = currentTimeStamp;

        var modelViewProjection = GetModelViewProjectionMatrix(deltaTime);

        query.WriteBuffer(
            buffer: uniformBuffer,
            data: modelViewProjection
        );

        var renderPassDescriptor = new RenderPassDescriptor()
        {
            ColorAttachments = [
                new()
                {
                    View = surface.GetCurrentTexture().Texture!.CreateView(),

                    ClearValue = new Color(0.5f, 0.5f, 0.5f, 1.0f),

                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                }
            ],
            DepthStencilAttachment = new()
            {
                View = depthTexture.CreateView(),
                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            }
        };

        var commandEncoder = device.CreateCommandEncoder();
        var passEncoder = commandEncoder.BeginRenderPass(renderPassDescriptor);
        passEncoder.SetPipeline(pipeline);
        passEncoder.SetBindGroup(0, uniformBindGroup);
        passEncoder.SetVertexBuffer(0, verticesBuffer);
        passEncoder.Draw(Cube.CUBE_VERTEX_COUNT);
        passEncoder.End();

        var oldCameraType = currentCameraType;
        var guiCommandBuffer = DrawGUI(guiContext, surface, out var cameraTypeChanged);
        if (cameraTypeChanged)
        {
            BaseCamera oldCamera = oldCameraType switch
            {
                CameraType.Arcball => camerasArcball,
                CameraType.WASD => camerasWASD,
                _ => throw new ArgumentOutOfRangeException()
            };
            BaseCamera newCamera = currentCameraType switch
            {
                CameraType.Arcball => camerasArcball,
                CameraType.WASD => camerasWASD,
                _ => throw new ArgumentOutOfRangeException()
            };

            newCamera.SetMatrix(oldCamera.GetMatrix());
        }


        query.Submit([commandEncoder.Finish(), guiCommandBuffer]);
        surface.Present();
    };
});


enum CameraType
{
    Arcball,
    WASD
}