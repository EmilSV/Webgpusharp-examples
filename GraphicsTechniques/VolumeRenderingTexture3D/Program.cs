using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 960;
const int HEIGHT = 540;
const uint VOLUME_WIDTH = 180;
const uint VOLUME_HEIGHT = 216;
const uint VOLUME_DEPTH = 180;
const uint SAMPLE_COUNT = 4;

var rotateCamera = true;
var near = 4.3f;
var far = 4.4f;
var textureFormat = TextureFormat.R8Unorm;
var statusText = string.Empty;


var formatOptions = new[]
{
    TextureFormat.R8Unorm,
    TextureFormat.BC4RUnorm,
    TextureFormat.ASTC12x12Unorm,
};

var brainImages = new Dictionary<TextureFormat, BrainImageInfo>
{
    {
        TextureFormat.R8Unorm,
        new(
            BytesPerBlock: 1,
            BlockLength: 1,
            RequiredFeature: null,
            ResourceName: "VolumeRenderingTexture3D.assets.t1_icbm_normal_1mm_pn0_rf0_180x216x180_uint8_1x1.bin-gz"
        )
    },
    {
        TextureFormat.BC4RUnorm,
        new(
            BytesPerBlock: 8,
            BlockLength: 4,
            RequiredFeature: FeatureName.TextureCompressionBCSliced3D,
            ResourceName: "VolumeRenderingTexture3D.assets.t1_icbm_normal_1mm_pn0_rf0_180x216x180_bc4_4x4.bin-gz"
        )
    },
    {
        TextureFormat.ASTC12x12Unorm,
        new(
            BytesPerBlock: 16,
            BlockLength: 12,
            RequiredFeature: FeatureName.TextureCompressionASTCSliced3D,
            ResourceName: "VolumeRenderingTexture3D.assets.t1_icbm_normal_1mm_pn0_rf0_180x216x180_astc_12x12.bin-gz"
        )
    },
};

var asm = Assembly.GetExecutingAssembly();
var volumeWGSL = ResourceUtils.GetEmbeddedResource("VolumeRenderingTexture3D.shaders.volume.wgsl", asm);

CommandBuffer? DrawGUI(GuiContext guiContext, Surface surface, out bool createNewVolumeTexture)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(12, 12), ImGuiCond.Once);
    ImGui.SetNextWindowSize(new(360, 180), ImGuiCond.Once);

    ImGui.Begin("Settings", ImGuiWindowFlags.NoResize);
    ImGui.Checkbox("rotateCamera", ref rotateCamera);
    if (ImGui.SliderFloat("near", ref near, 2.0f, 7.0f))
    {
        if (near >= far)
        {
            near = far - 0.1f;
        }
    }
    if (ImGui.SliderFloat("far", ref far, 2.0f, 7.0f))
    {
        if (far <= near)
        {
            far = near + 0.1f;
        }
    }

    var selectedFormat = textureFormat;
    if (ImGuiUtils.EnumDropdown("textureFormat", ref selectedFormat, formatOptions))
    {
        textureFormat = selectedFormat;
        createNewVolumeTexture = true;
    }
    else
    {
        createNewVolumeTexture = false;
    }

    if (!string.IsNullOrEmpty(statusText))
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), statusText);
    }

    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface);
}

return Run("Volume Rendering (Texture 3D)", WIDTH, HEIGHT, async runContext =>
{
    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();

    var adapter = await instance.RequestAdapterAsync(new()
    {
        CompatibleSurface = surface,
        FeatureLevel = FeatureLevel.Compatibility,
    }) ?? throw new Exception("Could not create adapter");

    var adapterFeatures = adapter.GetFeatures();
    List<FeatureName> requiredFeatures = new();
    if (adapterFeatures.Contains(FeatureName.TextureCompressionBCSliced3D))
    {
        requiredFeatures.Add(FeatureName.TextureCompressionBC);
        requiredFeatures.Add(FeatureName.TextureCompressionBCSliced3D);
    }
    if (adapterFeatures.Contains(FeatureName.TextureCompressionASTCSliced3D))
    {
        requiredFeatures.Add(FeatureName.TextureCompressionASTC);
        requiredFeatures.Add(FeatureName.TextureCompressionASTCSliced3D);
    }

    var device = await adapter.RequestDeviceAsync(new()
    {
        RequiredFeatures = CollectionsMarshal.AsSpan(requiredFeatures),
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
    }) ?? throw new Exception("Could not create device");

    var queue = device.GetQueue();

    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

    var devicePixelRatio = runContext.GetDevicePixelRatio();
    var renderWidth = (uint)Math.Max(1, (int)MathF.Round(WIDTH * devicePixelRatio));
    var renderHeight = (uint)Math.Max(1, (int)MathF.Round(HEIGHT * devicePixelRatio));

    surface.Configure(new()
    {
        Width = renderWidth,
        Height = renderHeight,
        Usage = TextureUsage.RenderAttachment,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Auto,
    });

    guiContext.SetupIMGUI(device, surfaceFormat);

    var shaderModule = device.CreateShaderModuleWGSL(new() { Code = volumeWGSL });
    var pipeline = device.CreateRenderPipelineSync(new()
    {
        Layout = null,
        Vertex = new()
        {
            Module = shaderModule,
            EntryPoint = "vertex_main",
        },
        Fragment = new()
        {
            Module = shaderModule,
            EntryPoint = "fragment_main",
            Targets = [new() { Format = surfaceFormat }],
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,
            CullMode = CullMode.Back,
        },
        Multisample = new()
        {
            Count = SAMPLE_COUNT,
        },
    });

    var msaaTexture = device.CreateTexture(new()
    {
        Size = new(renderWidth, renderHeight),
        SampleCount = SAMPLE_COUNT,
        Format = surfaceFormat,
        Usage = TextureUsage.RenderAttachment,
    });
    var msaaView = msaaTexture.CreateView();

    var uniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<Matrix4x4>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var sampler = device.CreateSampler(new()
    {
        MagFilter = FilterMode.Linear,
        MinFilter = FilterMode.Linear,
        MipmapFilter = MipmapFilterMode.Linear,
        MaxAnisotropy = 16,
    });




    Texture? volumeTexture = null;

    bool CreateVolumeTexture(TextureFormat format)
    {
        volumeTexture = null;

        if (!brainImages.TryGetValue(format, out var imageInfo))
        {
            statusText = $"Unsupported format: {format}";
            return false;
        }

        if (imageInfo.RequiredFeature is FeatureName feature && !requiredFeatures.Contains(feature))
        {
            statusText = $"{feature} not supported";
            return false;
        }

        using var compressedData = ResourceUtils.GetEmbeddedResourceStream(imageInfo.ResourceName, asm)
            ?? throw new Exception($"Missing resource '{imageInfo.ResourceName}'");
        using var gzipStream = new GZipStream(compressedData, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzipStream.CopyTo(output);
        var decompressedData = output.ToArray();

        var blocksWide = (VOLUME_WIDTH + imageInfo.BlockLength - 1) / imageInfo.BlockLength;
        var blocksHigh = (VOLUME_HEIGHT + imageInfo.BlockLength - 1) / imageInfo.BlockLength;
        var bytesPerRow = blocksWide * imageInfo.BytesPerBlock;

        volumeTexture = device.CreateTexture(new()
        {
            Dimension = TextureDimension.D3,
            Size = new(VOLUME_WIDTH, VOLUME_HEIGHT, VOLUME_DEPTH),
            Format = format,
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
        });

        queue.WriteTexture(
            destination: new() { Texture = volumeTexture },
            data: decompressedData,
            dataLayout: new()
            {
                BytesPerRow = bytesPerRow,
                RowsPerImage = blocksHigh,
            },
            writeSize: new(VOLUME_WIDTH, VOLUME_HEIGHT, VOLUME_DEPTH)
        );

        statusText = string.Empty;
        return true;
    }

    CreateVolumeTexture(textureFormat);

    float rotation = 0f;
    Matrix4x4 GetInverseModelViewProjectionMatrix(float deltaTime)
    {
        var viewMatrix = Matrix4x4.Identity;
        viewMatrix.Translate(new Vector3(0, 0, -4));
        if (rotateCamera)
        {
            rotation += deltaTime;
        }
        viewMatrix.Rotate(new Vector3(MathF.Sin(rotation), MathF.Cos(rotation), 0), 1f);

        var aspect = renderWidth / (float)renderHeight;
        var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView: 2f * MathF.PI / 5f,
            aspectRatio: aspect,
            nearPlaneDistance: near,
            farPlaneDistance: far
        );

        var modelViewProjectionMatrix = Matrix4x4.Multiply(viewMatrix, projectionMatrix);
        Matrix4x4.Invert(modelViewProjectionMatrix, out var inverseModelViewProjectionMatrix);
        return inverseModelViewProjectionMatrix;
    }



    var lastFrameTimestamp = Stopwatch.GetTimestamp();
    runContext.OnFrame += () =>
    {
        var now = Stopwatch.GetTimestamp();
        var deltaTime = (float)Stopwatch.GetElapsedTime(lastFrameTimestamp, now).TotalSeconds;
        lastFrameTimestamp = now;

        var inverseModelViewProjectionMatrix = GetInverseModelViewProjectionMatrix(deltaTime);
        queue.WriteBuffer(uniformBuffer, 0, inverseModelViewProjectionMatrix);

        var commandEncoder = device.CreateCommandEncoder();
        var surfaceTexture = surface.GetCurrentTexture().Texture!;
        var passEncoder = commandEncoder.BeginRenderPass(new()
        {
            ColorAttachments =
            [
                new()
                {
                    View = msaaView,
                    ResolveTarget = surfaceTexture.CreateView(),
                    ClearValue = new(0f, 0f, 0f, 1f),
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Discard,
                },
            ],
        });

        if (volumeTexture != null)
        {
            var uniformBindGroup = device.CreateBindGroup(new()
            {
                Layout = pipeline.GetBindGroupLayout(0),
                Entries =
                [
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
                        TextureView = volumeTexture.CreateView(),
                    },
                ],
            });

            passEncoder.SetPipeline(pipeline);
            passEncoder.SetBindGroup(0, uniformBindGroup);
            passEncoder.Draw(3);
        }
        passEncoder.End();

        var guiCommands = DrawGUI(guiContext, surface, out var createNewVolumeTexture);
        if (createNewVolumeTexture)
        {
            CreateVolumeTexture(textureFormat);
        }

        var drawCommands = commandEncoder.Finish();
        if (guiCommands is { } guiCommandBuffer)
        {
            queue.Submit([drawCommands, guiCommandBuffer]);
        }
        else
        {
            queue.Submit(drawCommands);
        }

        surface.Present();
    };
});

readonly record struct BrainImageInfo(
    uint BytesPerBlock,
    uint BlockLength,
    FeatureName? RequiredFeature,
    string ResourceName
);
