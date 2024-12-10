using System.Reflection;
using System.Text;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

const int WIDTH = 640;
const int HEIGHT = 480;
const int SAMPLE_COUNT = 4;

static byte[] ToByteArray(Stream input)
{
    using MemoryStream ms = new();
    input.CopyTo(ms);
    return ms.ToArray();
}

return Run(
    "Hello Triangle",
    WIDTH,
    HEIGHT,
    async (instance, surface, onFrame) =>
    {
        var executingAssembly = Assembly.GetExecutingAssembly();
        var triangleVertShaderWgsl = ToByteArray(
            executingAssembly.GetManifestResourceStream("HelloTriangleMSAA.triangle.vert.wgsl")!
        );
        var redFragShaderWgsl = ToByteArray(
            executingAssembly.GetManifestResourceStream("HelloTriangleMSAA.red.frag.wgsl")!
        );

        var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });

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
        );

        var queue = device.GetQueue();
        var surfaceCapabilities = surface.GetCapabilities(adapter)!;
        var surfaceFormat = surfaceCapabilities.Formats[0];

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
                Layout = null!,
                Vertex = ref InlineInit(
                    new VertexState()
                    {
                        Module = device.CreateShaderModuleWGSL(new() { Code = triangleVertShaderWgsl }),
                    }
                ),
                Fragment = new FragmentState()
                {
                    Module = device.CreateShaderModuleWGSL(new() { Code = redFragShaderWgsl }),
                    Targets = [new() { Format = surfaceFormat }],
                },
                Primitive = new() { Topology = PrimitiveTopology.TriangleList },
                Multisample = new() { Count = SAMPLE_COUNT },
            }
        );

        var texture = device.CreateTexture(
            new()
            {
                Size = new(WIDTH, HEIGHT),
                SampleCount = SAMPLE_COUNT,
                Format = surfaceFormat,
                Usage = TextureUsage.RenderAttachment,
            }
        );

        var view = texture.CreateView();

        onFrame(() =>
        {
            var commandEncoder = device.CreateCommandEncoder(new());
            var surfaceTexture = surface.GetCurrentTexture().Texture!;
            var surfaceTextureView = surfaceTexture.CreateView();

            var passEncoder = commandEncoder.BeginRenderPass(
                new()
                {
                    ColorAttachments =
                    [
                        new()
                        {
                            View = view,
                            ResolveTarget = surfaceTextureView,
                            ClearValue = new(0, 0, 0, 0), // Clear to transparent
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                        },
                    ],
                }
            );

            passEncoder.SetPipeline(pipeline);
            passEncoder.Draw(3);
            passEncoder.End();

            queue.Submit(commandEncoder.Finish());
            surface.Present();

            var activeHandleCount = WebGpuSharp.Internal.WebGpuSafeHandle.GetTotalActiveHandles();
            if (activeHandleCount > 300)
            {
                //GC.Collect();
            }
        });
    }
);
