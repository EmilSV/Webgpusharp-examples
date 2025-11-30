using System.Text;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 640;
const int HEIGHT = 480;

return Run("Hello Triangle", WIDTH, HEIGHT, async (instance, surface, onFrame) =>
{
    const string TriangleVertShaderSource =
    """
    @vertex
    fn main(
    @builtin(vertex_index) VertexIndex : u32
    ) -> @builtin(position) vec4f {
    var pos = array<vec2f, 3>(
        vec2(0.0, 0.5),
        vec2(-0.5, -0.5),
        vec2(0.5, -0.5)
    );

    return vec4f(pos[VertexIndex], 0.0, 1.0);
    }
    """;

    const string RedFragShaderSource =
    """
    @fragment
    fn main() -> @location(0) vec4f {
    return vec4(1.0, 0.0, 0.0, 1.0);
    }
    """;


    var adapter = (await instance.RequestAdapterAsync(new()
    {
        CompatibleSurface = surface
    }))!;

    var device = (await adapter.RequestDeviceAsync(new()
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
    }))!;

    var queue = device.GetQueue()!;
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


    var pipeline = device.CreateRenderPipeline(new()
    {
        Layout = null, // Auto-layout
        Vertex = new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = TriangleVertShaderSource
            }),
        },
        Fragment = new FragmentState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = RedFragShaderSource
            }),
            Targets = [
                new()
                {
                    Format = surfaceFormat
                }
          ]
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,
        },
    })!;

    onFrame(() =>
    {
        var commandEncoder = device.CreateCommandEncoder(new());
        var texture = surface.GetCurrentTexture().Texture!;
        var textureView = texture.CreateView()!;

        var passEncoder = commandEncoder.BeginRenderPass(new()
        {
            ColorAttachments = [
                new()
                {
                    View = textureView,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new(0, 0, 0, 0),
                }
            ],
        });

        passEncoder.SetPipeline(pipeline);
        passEncoder.Draw(3);
        passEncoder.End();

        queue.Submit([commandEncoder.Finish()]);
        surface.Present();
    });
});