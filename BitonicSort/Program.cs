
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using WebGpuSharp.FFI;
using static Setup.SetupWebGPU;
using GPUBuffer = WebGpuSharp.Buffer;


const int WIDTH = 1280;
const int HEIGHT = 720;

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

var asm = Assembly.GetExecutingAssembly();

var fullscreenTexturedQuadWGSL = ToBytes(asm.GetManifestResourceStream("BitonicSort.shaders.fullscreenTexturedQuad.wgsl")!);
var atomicToZeroWGSL = ToBytes(asm.GetManifestResourceStream("BitonicSort.shaders.atomicToZero.wgsl")!);
var bitonicDisplayFragWGSL = ToBytes(asm.GetManifestResourceStream("BitonicSort.shaders.bitonicDisplay.frag.wgsl")!);

return Run("Bitonic Sort", WIDTH, HEIGHT, async (instance, surface, onFrame) =>
{
    var adapter = await instance.RequestAdapterAsync(new()
    {
        CompatibleSurface = surface,
        FeatureLevel = FeatureLevel.Compatibility
    });

    var hasTimestampQuery = adapter.HasFeature(FeatureName.TimestampQuery);

    var device = await adapter.RequestDeviceAsync(new()
    {
        RequiredFeatures = hasTimestampQuery ? new[] { FeatureName.TimestampQuery } : null,

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
});
