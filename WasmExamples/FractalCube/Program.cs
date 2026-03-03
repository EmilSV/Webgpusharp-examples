using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text;
using WebGpuSharp;
using WebGpuSharp.FFI;
using WebGpuSharp.Marshalling;


const int WIDTH = 512;
const int HEIGHT = 512;
const float ASPECT = WIDTH / (float)HEIGHT;

var instance = WebGPU.CreateInstance()!;
var surface = CreateSurface(instance)!;


var startTimeStamp = Stopwatch.GetTimestamp();
var executingAssembly = typeof(Program).Assembly;
var basicVertWgsl = GetEmbeddedResource("FractalCube.shaders.basic.vert.wgsl", executingAssembly);
var vertexPositionColorWgsl = GetEmbeddedResource("FractalCube.shaders.sampleSelf.frag.wgsl", executingAssembly);

var adapter = instance.RequestAdapterSync(new() { CompatibleSurface = surface });

var device = adapter.RequestDeviceSync(
    new DeviceDescriptor()
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
)!;

var queue = device.GetQueue();
var surfaceCapabilities = surface.GetCapabilities(adapter)!;
var surfaceFormat = surfaceCapabilities.Formats[0];

surface.Configure(
    new()
    {
        Width = WIDTH,
        Height = HEIGHT,
        Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Auto,
    }
);

var verticesBuffer = device.CreateBuffer(
    new()
    {
        Size = (ulong)System.Buffer.ByteLength(Cube.CubeVertices),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    }
)!;

verticesBuffer.GetMappedRange<float>(static data =>
{
    Cube.CubeVertices.CopyTo(data);
});
verticesBuffer.Unmap();

var pipeline = device.CreateRenderPipelineSync(
    new()
    {
        Layout = null,
        Vertex = new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new() { Code = basicVertWgsl }),
            Buffers =
            [
                new()
                        {
                            ArrayStride = Cube.CubeVertexSize,
                            Attributes =
                            [
                                new()
                                {
                                    ShaderLocation = 0,
                                    Offset = Cube.CubePositionOffset,
                                    Format = VertexFormat.Float32x4,
                                },
                                new()
                                {
                                    ShaderLocation = 1,
                                    Offset = Cube.CubeUVOffset,
                                    Format = VertexFormat.Float32x2,
                                },
                            ],
                        },
            ],
        },
        Fragment = new FragmentState()
        {
            Module = device!.CreateShaderModuleWGSL(new() { Code = vertexPositionColorWgsl })!,
            Targets = [new() { Format = surfaceFormat }],
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,

            // Backface culling since the cube is solid piece of geometry.
            // Faces pointing away from the camera will be occluded by faces
            // pointing toward the camera.
            CullMode = CullMode.Back,
        },

        DepthStencil = new DepthStencilState()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth24Plus,
        },
    }
)!;

var depthTexture = device.CreateTexture(
    new()
    {
        Size = new(WIDTH, HEIGHT),
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment,
    }
)!;

const int uniformBufferSize = 4 * 16; // 4x4 matrix
var uniformBuffer = device.CreateBuffer(
    new()
    {
        Label = "Uniform Buffer",
        Size = uniformBufferSize,
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    }
);

var cubeTexture = device.CreateTexture(
    new()
    {
        Label = "Cube Texture",
        Size = new(WIDTH, HEIGHT),
        Format = surfaceFormat,
        Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
    }
);

var sampler = device.CreateSampler(
    new() { MagFilter = FilterMode.Linear, MinFilter = FilterMode.Linear }
);

var uniformBindGroup = device.CreateBindGroup(
    new()
    {
        Layout = pipeline.GetBindGroupLayout(0),
        Entries =
        [
            new() { Binding = 0, Buffer = uniformBuffer },
                    new() { Binding = 1, Sampler = sampler },
                    new() { Binding = 2, TextureView = cubeTexture.CreateView()! },
        ],
    }
);

var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
    fieldOfView: (float)(2.0f * Math.PI / 5.0f),
    aspectRatio: ASPECT,
    nearPlaneDistance: 1f,
    farPlaneDistance: 100.0f
);

Matrix4x4 getTransformationMatrix()
{
    float now = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;
    var viewMatrix = Matrix4x4.CreateFromAxisAngle(
        axis: new(MathF.Sin(now), MathF.Cos(now), 0),
        angle: 1
    );
    viewMatrix.Translation = new(0, 0, -4);
    return viewMatrix * projectionMatrix;
}


EmscriptenInterop.emscriptenSetMainLoop(() =>
{
    var transformationMatrix = getTransformationMatrix();
    queue.WriteBuffer(uniformBuffer, 0, transformationMatrix);

    var swapChainTexture = surface.GetCurrentTexture().Texture!;
    var swapChainTextureView = swapChainTexture.CreateView()!;

    var commandEncoder = device.CreateCommandEncoder(new());
    var passEncoder = commandEncoder.BeginRenderPass(
        new()
        {
            ColorAttachments =
            [
                new()
                        {
                            View = swapChainTextureView,
                            ClearValue = new(0.5, 0.5, 0.5, 1.0),
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                        },
            ],
            DepthStencilAttachment = new RenderPassDepthStencilAttachment()
            {
                View = depthTexture.CreateView()!,
                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            },
        }
    );
    passEncoder.SetPipeline(pipeline);
    passEncoder.SetBindGroup(0, uniformBindGroup);
    passEncoder.SetVertexBuffer(0, verticesBuffer);
    passEncoder.Draw(Cube.CubeVertexCount);
    passEncoder.End();

    commandEncoder.CopyTextureToTexture(
        new() { Texture = swapChainTexture },
        new() { Texture = cubeTexture },
        new(WIDTH, HEIGHT)
    );

    queue.Submit(commandEncoder.Finish());
}, 0, 1);


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

static byte[] ToByteArray(Stream input)
{
    using MemoryStream ms = new();
    input.CopyTo(ms);
    return ms.ToArray();
}
static byte[] GetEmbeddedResource(string resourceName, Assembly assembly)
{
    var executingAssembly = assembly;
    return ToByteArray(executingAssembly.GetManifestResourceStream(resourceName)!)!;
}