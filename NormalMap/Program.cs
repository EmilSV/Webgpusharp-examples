
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = (float)WIDTH / HEIGHT;
var asm = Assembly.GetExecutingAssembly();
var settings = new GUISettings();

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}


Texture LoadAndCreateTexture(Device device, string path)
{
    var stream = asm.GetManifestResourceStream(path);
    var imageData = ResourceUtils.LoadImage(stream!);
    var texture = device.CreateTexture(new()
    {
        Size = new(imageData.Width, imageData.Height, 1),
        Format = TextureFormat.RGBA8Unorm,
        Usage =
            TextureUsage.TextureBinding |
            TextureUsage.CopyDst |
            TextureUsage.RenderAttachment
    });
    ResourceUtils.CopyExternalImageToTexture(device.GetQueue(), imageData, texture);
    return texture;
}

var normalMapWGSL = ToBytes(asm.GetManifestResourceStream("NormalMap.shaders.normalMap.wgsl")!);


return Run("Normal Map", WIDTH, HEIGHT, async runContext =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();

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


    // Create normal mapping resources and pipeline
    var depthTexture = device.CreateTexture(new()
    {
        Size = new(WIDTH, HEIGHT),
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment
    });
    var depthTextureView = depthTexture.CreateView();

    var spaceTransformsBuffer = device.CreateBuffer(new()
    {
        // Buffer holding projection, view, and model matrices plus padding bytes
        Size = (uint)Unsafe.SizeOf<SpaceTransformsBuffer>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst
    });

    var mapInfoBuffer = device.CreateBuffer(new()
    {
        Size = (uint)Unsafe.SizeOf<MapInfo>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    MapInfo mapInfo = new();

    Texture woodAlbedoTexture = LoadAndCreateTexture(device, "NormalMap.assets.wood_albedo.png");
    Texture spiralNormalTexture = LoadAndCreateTexture(device, "NormalMap.assets.spiral_normal.png");
    Texture spiralHeightTexture = LoadAndCreateTexture(device, "NormalMap.assets.spiral_height.png");
    Texture toyboxNormalTexture = LoadAndCreateTexture(device, "NormalMap.assets.toybox_normal.png");
    Texture toyboxHeightTexture = LoadAndCreateTexture(device, "NormalMap.assets.toybox_height.png");
    Texture brickwallAlbedoTexture = LoadAndCreateTexture(device, "NormalMap.assets.brickwall_albedo.png");
    Texture brickwallNormalTexture = LoadAndCreateTexture(device, "NormalMap.assets.brickwall_normal.png");
    Texture brickwallHeightTexture = LoadAndCreateTexture(device, "NormalMap.assets.brickwall_height.png");

    // Create a sampler with linear filtering for smooth interpolation.
    var sampler = device.CreateSampler(new()
    {
        MagFilter = FilterMode.Linear,
        MinFilter = FilterMode.Linear,
    })!;

    var box = Mesh.CreateMeshRenderable(
        device: device,
        mesh: Box.CreateBoxMeshWithTangents(1.0f, 1.0f, 1.0f)
    );

    var frameBGDescriptor = Utils.CreateBindGroupDescriptor(
        bindings: [0, 1],
        [
            ShaderStage.Vertex | ShaderStage.Fragment,
            ShaderStage.Fragment | ShaderStage.Vertex
        ],
        resourceLayouts: [
            new BufferBindingLayout()
            {
                Type = BufferBindingType.Uniform,
            },
            new BufferBindingLayout()
            {
                Type = BufferBindingType.Uniform
            }
        ],
        resources: [[spaceTransformsBuffer, mapInfoBuffer]],
        label: "Frame",
        device: device
    );

    var surfaceBGDescriptor = Utils.CreateBindGroupDescriptor(
        bindings: [0, 1, 2, 3],
        [
            ShaderStage.Fragment
        ],
        resourceLayouts: [
            new SamplerBindingLayout()
            {
                Type = SamplerBindingType.Filtering,
            },
            new TextureBindingLayout()
            {
                SampleType = TextureSampleType.Float
            },
            new TextureBindingLayout()
            {
                SampleType = TextureSampleType.Float
            },
            new TextureBindingLayout()
            {
                SampleType = TextureSampleType.Float
            },
        ],
        // Multiple bindgroups that accord to the layout defined above
        resources: [
            [
                sampler,
                woodAlbedoTexture.CreateView(),
                spiralNormalTexture.CreateView(),
                spiralHeightTexture.CreateView()
            ],
            [
                sampler,
                woodAlbedoTexture.CreateView(),
                toyboxNormalTexture.CreateView(),
                toyboxHeightTexture.CreateView(),
            ],
            [
                sampler,
                brickwallAlbedoTexture.CreateView(),
                brickwallNormalTexture.CreateView(),
                brickwallHeightTexture.CreateView(),
            ]
        ],
        label: "Surface",
        device: device
    );

    var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
        fieldOfView: 2 * MathF.PI / 5,
        aspectRatio: ASPECT,
        nearPlaneDistance: 0.1f,
        farPlaneDistance: 10.0f
    );

    Matrix4x4 GetViewMatrix() => Matrix4x4.CreateLookAt(
        cameraPosition: settings.CameraPos,
        cameraTarget: new Vector3(0, 0, 0),
        cameraUpVector: new Vector3(0, 1, 0)
    );

    Matrix4x4 GetModelMatrix()
    {
        var modelMatrix = Matrix4x4.Identity;
        var now = Stopwatch.GetElapsedTime(startTimeStamp).TotalMilliseconds / 1000;
        modelMatrix.RotateY((float)now * -0.5f);
        return modelMatrix;
    }

    uint GetMode() => settings.BumpMode switch
    {
        BumpMode.AlbedoTexture => 0,
        BumpMode.NormalTexture => 1,
        BumpMode.DepthTexture => 2,
        BumpMode.NormalMap => 3,
        BumpMode.ParallaxScale => 4,
        BumpMode.SteepParallax => 5
    };

    var texturedCubePipeline = Utils.Create3DRenderPipeline(
        device: device,
        label: "NormalMappingRender",
        bindGroupLayouts: [frameBGDescriptor.BindGroupLayout, surfaceBGDescriptor.BindGroupLayout],
        vertexShader: normalMapWGSL,
        vertexBufferFormats: [
            VertexFormat.Float32x3, //Position
            VertexFormat.Float32x3, //normal
            VertexFormat.Float32x2, //uv
            VertexFormat.Float32x3, //tangent
            VertexFormat.Float32x3 // bitangent
        ],
        fragmentShader: normalMapWGSL,
        presentationFormat: surfaceFormat
    );

    int currentSurfaceBindGroup = 0;
    void OnChangeTexture()
    {
        currentSurfaceBindGroup = (int)settings.Texture;
    }

    runContext.OnFrame += () =>
    {
        var viewMatrix = GetViewMatrix();
        var worldViewMatrix = GetModelMatrix() * viewMatrix;
        var worldViewProjMatrix = worldViewMatrix * projectionMatrix;
        SpaceTransformsBuffer matrices = new() {
            WorldViewProjMatrix = worldViewProjMatrix,
            WorldViewMatrix = worldViewMatrix
        };

        // Update mapInfoBuffer
        var lightPowWS = settings.LightPos;
        var lightPoVS = Vector3.Transform(lightPowWS, viewMatrix);
        var mode = GetMode();
        var queue = device.GetQueue();

        queue.WriteBuffer(
            buffer: spaceTransformsBuffer,
            bufferOffset: 0,
            data: matrices
        );

        queue.WriteBuffer(mapInfoBuffer, mapInfo);

        RenderPassDescriptor renderPassDescriptor = new()
        {
            ColorAttachments = [
                new()
                {
                    View = surface.GetCurrentTexture()!.Texture!.CreateView(),
                    ClearValue = new(0,0,0,1f),
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                }
            ],
            DepthStencilAttachment = new()
            {
                View = depthTextureView,
                DepthClearValue = 1f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            }
        };

        var commandEncoder = device.CreateCommandEncoder();
        var passEncoder = commandEncoder.BeginRenderPass(renderPassDescriptor);
        // Draw textured Cube
        passEncoder.SetPipeline(texturedCubePipeline);
        passEncoder.SetBindGroup(0, frameBGDescriptor.BindGroups[0]);
        passEncoder.SetBindGroup(1, surfaceBGDescriptor.BindGroups[currentSurfaceBindGroup]);
        passEncoder.SetVertexBuffer(0, box.VertexBuffer);
        passEncoder.SetIndexBuffer(box.IndexBuffer, IndexFormat.Uint16);
        passEncoder.DrawIndexed(box.IndexCount);
        passEncoder.End();
        queue.Submit(commandEncoder.Finish());

        surface.Present();
    };
});

enum BumpMode
{
    AlbedoTexture,
    NormalTexture,
    DepthTexture,
    NormalMap,
    ParallaxScale,
    SteepParallax,
}

enum TextureAtlas
{
    Spiral = 0,
    Toybox = 1,
    BrickWall = 2,
}

struct MapInfo
{
    public Vector3 LightPosVS;
    public uint Mode;
    public float LightIntensity;
    public float DepthScale;
    public float DepthLayers;
}

struct SpaceTransformsBuffer
{
    public Matrix4x4 WorldViewProjMatrix;
    public Matrix4x4 WorldViewMatrix;
}

class GUISettings
{
    public BumpMode BumpMode = BumpMode.NormalMap;
    public Vector3 CameraPos = new(0.0f, 0.8f, 1.4f);
    public Vector3 LightPos = new(1.7f, 0.7f, 1.9f);
    public float LightIntensity = 5.0f;
    public float DepthScale = 0.05f;
    public float DepthLayers = 16;
    public TextureAtlas Texture = TextureAtlas.Spiral;
    public Action ResetLight = static () => { return; };
}