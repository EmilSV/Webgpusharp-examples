using System.Diagnostics;
using System.Numerics;
using System.Reflection;
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

const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = WIDTH / (float)HEIGHT;

bool useRenderBundles = true;
int asteroidCount = 100;

CommandBuffer DrawGUI(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.3f);
    ImGui.Begin("Settings",
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoCollapse
    );
    ImGui.Checkbox(nameof(useRenderBundles), ref useRenderBundles);
    ImGui.SliderInt(nameof(asteroidCount), ref asteroidCount, 1000, 10000);
    ImGui.End();
    guiContext.EndFrame();

    return guiContext.Render(surface)!.Value!;
}

return Run(
    name: "Render Bundles",
    width: WIDTH,
    height: HEIGHT,
    callback: async (instance, surface, guiContext, onFrame) =>
    {
        var startTimeStamp = Stopwatch.GetTimestamp();

        var meshWGSL = ResourceUtils.GetEmbeddedResource("RenderBundles.shaders.mesh.wgsl");

        var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });
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

        surface.Configure(new()
        {
            Width = WIDTH,
            Height = HEIGHT,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            Format = surfaceFormat,
            Device = device,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto,
        });

        var shaderModule = device.CreateShaderModuleWGSL(new()
        {
            Code = meshWGSL
        });


        var pipeline = device.CreateRenderPipeline(new()
        {
            Layout = null,
            Vertex = ref InlineInit(new VertexState()
            {
                Module = shaderModule,
                Buffers = [
                    new()
                    {
                        ArrayStride =  SphereMesh.Vertex.VertexStride,
                        Attributes= [
                            new()
                            {
                                // position
                                ShaderLocation = 0,
                                Offset = SphereMesh.Vertex.PositionsOffset,
                                Format = VertexFormat.Float32x3,
                            },
                            new()
                            {
                                // normal
                                ShaderLocation = 1,
                                Offset = SphereMesh.Vertex.NormalOffset,
                                Format = VertexFormat.Float32x3,
                            },
                            new()
                            {
                                // uv
                                ShaderLocation = 2,
                                Offset = SphereMesh.Vertex.UvOffset,
                                Format = VertexFormat.Float32x2,
                            },
                        ]
                    }
                ]
            }),
            Fragment = new FragmentState()
            {
                Module = shaderModule,
                Targets = [
                    new(){
                        Format = surfaceFormat
                    }
                ]
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                // Backface culling since the sphere is solid piece of geometry.
                // Faces pointing away from the camera will be occluded by faces
                // pointing toward the camera.
                CullMode = CullMode.Back,
            },

            // Enable depth testing so that the fragment closest to the camera
            // is rendered in front.
            DepthStencil = new DepthStencilState()
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

        const int uniformBufferSize = 4 * 16; // 4x4 matrix
        var uniformBuffer = device.CreateBuffer(new()
        {
            Size = uniformBufferSize,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        });

        
        
    }
);
