using System.Reflection;
using System.Text;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 640;
const int HEIGHT = 480;
const int SAMPLE_COUNT = 4;

return Run(
    "Hello Triangle",
    WIDTH,
    HEIGHT,
    async (instance, surface, onFrame) =>
    {
        var executingAssembly = Assembly.GetExecutingAssembly();
        var triangleVertShaderWgsl = ResourceUtils.GetEmbeddedResource(
            "HelloTriangleMSAA.shaders.triangle.vert.wgsl",
            executingAssembly
        );
        var redFragShaderWgsl = ResourceUtils.GetEmbeddedResource(
            "HelloTriangleMSAA.shaders.red.frag.wgsl",
            executingAssembly
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
        ) ?? throw new Exception("Could not create device");

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

        var pipeline = device.CreateRenderPipelineSync(
            new()
            {
                Layout = null!,
                Vertex = new()
                {
                    Module = device.CreateShaderModuleWGSL(new() { Code = triangleVertShaderWgsl }),
                },
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
        });
    }
);
