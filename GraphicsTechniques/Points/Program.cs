using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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


var asm = Assembly.GetExecutingAssembly();
var distanceSizedPointsVertWGSL = ToBytes(asm.GetManifestResourceStream("Points.shaders.distance-sized-points.vert.wgsl")!);
var fixedSizePointsVertWGSL = ToBytes(asm.GetManifestResourceStream("Points.shaders.fixed-size-points.vert.wgsl")!);
var orangeFragWGSL = ToBytes(asm.GetManifestResourceStream("Points.shaders.orange.frag.wgsl")!);
var texturedFragWGSL = ToBytes(asm.GetManifestResourceStream("Points.shaders.textured.frag.wgsl")!);

var butterflyImage = ResourceUtils.LoadImage(asm.GetManifestResourceStream("Points.assets.img.Butterfly.png")!);

var settings = new Settings()
{
    FixedSize = false,
    Textured = false,
    Size = 10.0f
};


static float[] CreateFibonacciSphereVertices(int numSamples, float radius)
{
    float[] vertices = new float[numSamples * 3];
    float increment = MathF.PI * (3 - MathF.Sqrt(5));
    for (int i = 0; i < numSamples; ++i)
    {
        float offset = 2.0f / numSamples;
        float y = i * offset - 1 + offset / 2;
        float r = MathF.Sqrt(1.0f - y * y);
        float phi = i % numSamples * increment;
        float x = MathF.Cos(phi) * r;
        float z = MathF.Sin(phi) * r;
        vertices[i * 3 + 0] = x * radius;
        vertices[i * 3 + 1] = y * radius;
        vertices[i * 3 + 2] = z * radius;
    }
    return vertices;
}


CommandBuffer DrawGui(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(340, 0));
    ImGui.SetNextWindowSize(new(300, 100));
    ImGui.Begin("Points",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize
    );

    ImGui.Checkbox("fixedSize", ref settings.FixedSize);
    ImGui.Checkbox("textured", ref settings.Textured);
    ImGui.SliderFloat("size", ref settings.Size, 0.0f, 80.0f);

    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}


return Run("Points", WIDTH, HEIGHT, async runContext =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();

    Adapter adapter = await instance.RequestAdapterAsync(new()
    {
        PowerPreference = PowerPreference.HighPerformance,
        CompatibleSurface = surface
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

    // Create a bind group layout so we can share the bind groups
    // with multiple pipelines.
    var bindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries = [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex,
                Buffer = new(),
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Fragment,
                Sampler = new()
            },
            new()
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Texture = new()
            },
        ],
    });

    var pipelineLayout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = [bindGroupLayout],
    });


    // Compile all 4 shaders
    var distanceSizedPointsVertShader = device.CreateShaderModuleWGSL(new()
    {
        Code = distanceSizedPointsVertWGSL
    });

    var fixedSizePointsVertShader = device.CreateShaderModuleWGSL(new()
    {
        Code = fixedSizePointsVertWGSL
    });

    var orangeFragShader = device.CreateShaderModuleWGSL(new()
    {
        Code = orangeFragWGSL
    });

    var texturedFragShader = device.CreateShaderModuleWGSL(new()
    {
        Code = texturedFragWGSL
    });

    const TextureFormat DEPTH_FORMAT = TextureFormat.Depth24Plus;

    // make pipelines for each combination
    ShaderModule[] fragModules = [orangeFragShader, texturedFragShader];
    ShaderModule[] vertModules = [distanceSizedPointsVertShader, fixedSizePointsVertShader];


    var pipelines = vertModules.Select(vertModule =>
        fragModules.Select(fragModule =>
            device.CreateRenderPipelineSync(new()
            {
                Layout = pipelineLayout,
                Vertex = new()
                {
                    Module = vertModule,
                    Buffers = [
                        new()
                        {
                            ArrayStride = (ulong)Unsafe.SizeOf<Vector3>(),
                            StepMode = VertexStepMode.Instance,
                            Attributes = [
                                new()
                                {
                                    ShaderLocation = 0,
                                    Offset = 0,
                                    Format = VertexFormat.Float32x3,
                                },
                            ],
                        },
                    ],
                },
                Fragment = new()
                {
                    Module = fragModule,
                    Targets = [
                        new()
                        {
                            Format = surfaceFormat,
                            Blend = new()
                            {
                                Color = new()
                                {
                                    SrcFactor = BlendFactor.One,
                                    DstFactor = BlendFactor.OneMinusSrcAlpha,
                                },
                                Alpha = new()
                                {
                                    SrcFactor = BlendFactor.One,
                                    DstFactor = BlendFactor.OneMinusSrcAlpha,
                                },
                            },
                        },
                    ],
                },
                DepthStencil = new()
                {
                    DepthWriteEnabled = OptionalBool.True,
                    DepthCompare = CompareFunction.Less,
                    Format = DEPTH_FORMAT,
                },
            })
        ).ToArray()
    ).ToArray();

    var vertexData = CreateFibonacciSphereVertices(
        numSamples: 1000,
        radius: 1.0f
    );

    var numberOfPoints = vertexData.Length / 3;

    var vertexBuffer = device.CreateBuffer(new()
    {
        Label = "vertex buffer vertices",
        Size = vertexData.GetSizeInBytes(),
        Usage = BufferUsage.Vertex | BufferUsage.CopyDst
    });
    query.WriteBuffer(vertexBuffer, 0, vertexData);


    var uniformValues = new Uniforms();
    var uniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<Uniforms>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst
    });


    var sampler = device.CreateSampler();
    var texture = device.CreateTexture(new()
    {
        Size = new(butterflyImage.Width, butterflyImage.Height),
        Format = TextureFormat.RGBA8Unorm,
        Usage =
            TextureUsage.CopyDst |
            TextureUsage.TextureBinding |
            TextureUsage.RenderAttachment,
    });

    ResourceUtils.CopyExternalImageToTexture(
        queue: query,
        source: butterflyImage,
        texture: texture
    );

    var bindGroup = device.CreateBindGroup(new()
    {
        Layout = bindGroupLayout,
        Entries = [
            new()
            {
                Binding = 0,
                Buffer = uniformBuffer,
            },
            new()
            {
                Binding = 1,
                Sampler = sampler,
            },
            new()
            {
                Binding = 2,
                TextureView = texture.CreateView(),
            },
        ],
    });

    Texture? depthTexture = null;

    runContext.OnFrame += () =>
    {
        var time = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;

        // If we don't have a depth texture OR if its size is different
        // from the canvasTexture when make a new depth texture
        if (
            depthTexture == null ||
            depthTexture.GetWidth() != WIDTH ||
            depthTexture.GetHeight() != HEIGHT
        )
        {
            depthTexture?.Destroy();
            depthTexture = device.CreateTexture(new()
            {
                Size = new(WIDTH, HEIGHT),
                Format = DEPTH_FORMAT,
                Usage = TextureUsage.RenderAttachment,
            });
        }

        var renderPassDescriptor = new RenderPassDescriptor()
        {
            Label = "our basic canvas renderPass",
            ColorAttachments = [
                new()
                {
                    View = surface.GetCurrentTexture().Texture!.CreateView(),
                    ClearValue = new(0.3f, 0.3f, 0.3f, 1.0f),
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                },
            ],
            DepthStencilAttachment = new()
            {
                View = depthTexture.CreateView(),
                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            },
        };

        var fixedSize = settings.FixedSize;
        var textured = settings.Textured;

        var pipeline = pipelines[fixedSize ? 1 : 0][textured ? 1 : 0];

        // Set the size in the uniform values
        uniformValues.Size = settings.Size;

        var fov = 90 * MathF.PI / 180;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView: fov,
            aspectRatio: ASPECT,
            nearPlaneDistance: 0.1f,
            farPlaneDistance: 50.0f
        );

        var view = Matrix4x4.CreateLookAt(
            cameraPosition: new(0, 0, 1.5f),
            cameraTarget: Vector3.Zero,
            cameraUpVector: Vector3.UnitY
        );

        var viewProjection = Matrix4x4.Multiply(view, projection);


        viewProjection.RotateY(time);
        viewProjection.RotateX(time * 0.1f);

        uniformValues.Resolution = new Vector2(WIDTH, HEIGHT);
        uniformValues.Matrix = viewProjection;


        // Copy the uniform values to the GPU
        query.WriteBuffer(uniformBuffer, 0, uniformValues);

        var encoder = device.CreateCommandEncoder();
        var pass = encoder.BeginRenderPass(renderPassDescriptor);
        pass.SetPipeline(pipeline);
        pass.SetVertexBuffer(0, vertexBuffer);
        pass.SetBindGroup(0, bindGroup);
        pass.Draw(6, (uint)numberOfPoints);
        pass.End();

        var commandBuffer = encoder.Finish();

        var guiCommandBuffer = DrawGui(guiContext, surface);

        query.Submit([commandBuffer, guiCommandBuffer]);

        surface.Present();

    };

});

struct Uniforms
{
    public Matrix4x4 Matrix;
    public Vector2 Resolution;
    public float Size;
    private float _pad0;
}

class Settings
{
    public bool FixedSize = false;
    public bool Textured = false;
    public float Size = 10.0f;
}