using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using WebGpuSharp;
using WebGpuSharp.FFI;
using WebGpuSharp.Marshalling;

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

const int WIDTH = 512;
const int HEIGHT = 512;

var instance = WebGPU.CreateInstance()!;
var surface = CreateSurface(instance)!;

var adapter = instance.RequestAdapterSync(new()
{
    CompatibleSurface = surface
});

var device = adapter.RequestDeviceSync();
var queue = device.GetQueue()!;
var surfaceFormat = surface.GetCapabilities(adapter)!.Formats[0];

surface.Configure(new()
{
    Width = WIDTH,
    Height = HEIGHT,
    Usage = TextureUsage.RenderAttachment,
    Format = surfaceFormat,
    Device = device
});


var pipeline = device.CreateRenderPipelineSync(new()
{
    Layout = null, // Auto-layout
    Vertex = new()
    {
        Module = device.CreateShaderModuleWGSL(new()
        {
            Code = TriangleVertShaderSource
        }),
    },
    Fragment = new()
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

WebgpuHandles.Surface = surface;
WebgpuHandles.Device = device;
WebgpuHandles.Queue = queue;
WebgpuHandles.Pipeline = pipeline;

unsafe
{
    EmscriptenInterop.emscripten_set_main_loop_arg(&WasmCallbacks.RenderLoop, nint.Zero, 0, 1);
}

unsafe static Surface CreateSurface(Instance instance)
{
    var selectorU8 = "canvas"u8;
    var instanceHanlde = WebGPUMarshal.GetHandle(instance);

    fixed (byte* ptr = selectorU8)
    {
        var desc = new EmscriptenSurfaceSourceCanvasHTMLSelectorFFI()
        {
            Chain = new ChainedStruct()
            {
                SType = SType.EmscriptenSurfaceSourceCanvasHTMLSelector
            },
            Selector = new StringViewFFI()
            {
                Data = ptr,
                Length = (nuint)selectorU8.Length
            }
        };
        var surfaceDescriptor = new SurfaceDescriptorFFI()
        {
            NextInChain = (ChainedStruct*)&desc
        };

        return instanceHanlde.CreateSurface(&surfaceDescriptor).ToSafeHandle()!;
    }
}

class WebgpuHandles
{
    public static Surface? Surface;
    public static Device? Device;
    public static Queue? Queue;
    public static RenderPipeline? Pipeline;
}

static class WasmCallbacks
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RenderLoop(nint _)
    {
        var device = WebgpuHandles.Device!;
        var surface = WebgpuHandles.Surface!;
        var pipeline = WebgpuHandles.Pipeline!;
        var queue = WebgpuHandles.Queue!;

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
    }
}