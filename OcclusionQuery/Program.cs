using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

const int WIDTH = 640;
const int HEIGHT = 480;

const TextureFormat DEPTH_FORMAT = TextureFormat.Depth24Plus;

(Vector3 position, Vector3 normal)[] vertexData = [
    (new (1,  1, -1),    new(1,  0,  0)),
    (new (1,  1,  1),    new(1,  0,  0)),
    (new (1, -1,  1),    new(1,  0,  0)),
    (new (1, -1, -1),    new(1,  0,  0)),
    (new (-1,  1,  1),   new(-1,  0,  0)),
    (new (-1,  1, -1),   new(-1,  0,  0)),
    (new (-1, -1, -1),   new(-1,  0,  0)),
    (new (-1, -1,  1),   new(-1,  0,  0)),
    (new (-1,  1,  1),   new(0,  1,  0)),
    (new (1,  1,  1),    new(0,  1,  0)),
    (new (1,  1, -1),    new(0,  1,  0)),
    (new (-1,  1, -1),   new(0,  1,  0)),
    (new (-1, -1, -1),   new(0, -1,  0)),
    (new (1, -1, -1),    new(0, -1,  0)),
    (new (1, -1,  1),    new(0, -1,  0)),
    (new (-1, -1,  1),   new(0, -1,  0)),
    (new (1,  1,  1),    new(0,  0,  1)),
    (new (-1,  1,  1),   new(0,  0,  1)),
    (new (-1, -1,  1),   new(0,  0,  1)),
    (new (1, -1,  1),    new(0,  0,  1)),
    (new (-1,  1, -1),   new(0,  0, -1)),
    (new (1,  1, -1),    new(0,  0, -1)),
    (new (1, -1, -1),    new(0,  0, -1)),
    (new (-1, -1, -1),   new(0,  0, -1)),
];

ushort[] indices = [
   0,  1,  2,  0,  2,  3, // +x face
   4,  5,  6,  4,  6,  7, // -x face
   8,  9, 10,  8, 10, 11, // +y face
  12, 13, 14, 12, 14, 15, // -y face
  16, 17, 18, 16, 18, 19, // +z face
  20, 21, 22, 20, 22, 23, // -z face
];

(Vector3 position, Vector4 color)[] cubePositions = [
  (new (-1,  0,  0),  new(1, 0, 0, 1)),
  (new ( 1,  0,  0),  new(1, 1, 0, 1)),
  (new ( 0, -1,  0),  new(0, 0.5f, 0, 1)),
  (new ( 0,  1,  0),  new(1, 0.6f, 0, 1)),
  (new ( 0,  0, -1),  new(0, 0, 1, 1)),
  (new ( 0,  0,  1),  new(0.5f, 0, 0.5f, 1)),
];


bool settingsIsAnimated = true;
List<Vector4> cubeVisibility = [];

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

static WebGpuSharp.Buffer CreateBufferWithData<T>(
    Device device,
    T[] data,
    BufferUsage usage,
    string label
)
    where T : unmanaged
{
    var size = Unsafe.SizeOf<T>() * data.Length;
    var buffer = device.CreateBuffer(new()
    {
        Label = label,
        Size = (ulong)size,
        Usage = usage,
        MappedAtCreation = true
    });

    buffer.GetMappedRange<T, ReadOnlySpan<T>>(0, (nuint)data.Length, static (bufferData, data) =>
    {
        data.CopyTo(bufferData);
    }, data);
    buffer.Unmap();
    return buffer;
}

static float Lerp(float a, float b, float t) => a + (b - a) * t;
static Vector3 LerpV(Vector3 a, Vector3 b, float t) => new(
    Lerp(a.X, b.X, t),
    Lerp(a.Y, b.Y, t),
    Lerp(a.Z, b.Z, t)
);
static float PingPongSine(float t) => (MathF.Sin(t * MathF.PI * 2) + 1) / 2;

CommandBuffer DrawGui(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowPos(new(0, 0));
    ImGui.SetNextWindowSize(new(200, 100));
    ImGui.SetNextWindowBgAlpha(0.3f);
    ImGui.Begin("Settings",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoCollapse
    );
    ImGui.Checkbox("Animate", ref settingsIsAnimated);

    ImGui.Separator();
    ImGui.Text("Visible Cubes:");

    for (int i = 0; i < cubeVisibility.Count; i++)
    {
        var cube = cubeVisibility[i];

        ImGui.ColorButton($"##cube{i}", cube, ImGuiColorEditFlags.NoTooltip, new Vector2(20, 20));
        ImGui.SameLine();
    }

    ImGui.End();

    guiContext.EndFrame();

    return guiContext.Render(surface)!.Value!;
}

return Run("Occlusion Query", WIDTH, HEIGHT, async (instance, surface, guiContext, onFrame) =>
{
    var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });
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

    var queue = device.GetQueue();
    var surfaceCaps = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCaps.Formats[0];

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

    // Load shader
    var asm = Assembly.GetExecutingAssembly();
    var solidColorLitWGSL = ToBytes(asm.GetManifestResourceStream("OcclusionQuery.shaders.solidColorLit.wgsl")!);
    var module = device.CreateShaderModuleWGSL(new()
    {
        Code = solidColorLitWGSL
    });

    var pipeline = device.CreateRenderPipeline(new()
    {
        Layout = null,
        Vertex = ref InlineInit(new VertexState()
        {
            Module = module,
            Buffers = [
                new()
                {
                    ArrayStride = (uint)Unsafe.SizeOf<(Vector3 position, Vector3 normal)>(),
                    Attributes = [
                        new() // position
                        {
                            ShaderLocation = 0,
                            Offset = 0,
                            Format = VertexFormat.Float32x3,
                        },
                        new()
                        {
                            Offset = (uint)Unsafe.SizeOf<Vector3>(),
                            ShaderLocation = 1, // normal
                            Format = VertexFormat.Float32x3,
                        }
                    ],
                    StepMode = VertexStepMode.Vertex,
                }
            ]
        }),
        Fragment = new FragmentState()
        {
            Module = module,
            Targets = [new() { Format = surfaceFormat }]
        },
        Primitive = new PrimitiveState()
        {
            Topology = PrimitiveTopology.TriangleList,
            CullMode = CullMode.Back,
        },
        DepthStencil = new DepthStencilState
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = DEPTH_FORMAT,
        }
    });


    var objectInfos = cubePositions.Select(item =>
    {
        var (position, color) = item;
        ulong uniformBufferSize = (ulong)Unsafe.SizeOf<ObjectInfos.UniformData>();
        var uniformBuffer = device.CreateBuffer(new()
        {
            Size = uniformBufferSize,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });

        ObjectInfos.UniformData uniform = new()
        {
            ColorValue = color
        };

        var bindGroup = device.CreateBindGroup(new()
        {
            Layout = pipeline.GetBindGroupLayout(0),
            Entries = [
                new()
                {
                    Binding = 0,
                    Buffer = uniformBuffer
                }
            ]
        });

        return new ObjectInfos()
        {
            Position = position * 10,
            BindGroup = bindGroup,
            UniformBuffer = uniformBuffer,
            Uniforms = uniform
        };
    }).ToList();

    var querySet = device.CreateQuerySet(new()
    {
        Type = QueryType.Occlusion,
        Count = (uint)objectInfos.Count,
    });

    var resolveBuf = device.CreateBuffer(new()
    {
        Label = "resolveBuffer",
        // Query results are 64bit unsigned integers.
        Size = (uint)objectInfos.Count * sizeof(ulong),
        Usage = BufferUsage.QueryResolve | BufferUsage.CopySrc
    });

    var resultBuf = device.CreateBuffer(new()
    {
        Label = "resultBuffer",
        Size = resolveBuf.GetSize(), // should be 48
        Usage = BufferUsage.CopyDst | BufferUsage.MapRead
    });

    var vertexBuf = CreateBufferWithData(
        device,
        vertexData,
        BufferUsage.Vertex,
        "vertexBuffer"
    );

    var indicesBuf = CreateBufferWithData(
        device,
        indices,
        BufferUsage.Index,
        "indexBuffer"
    );


    Texture? depthTexture = null;

    double time = 0;
    long then = Stopwatch.GetTimestamp();
    onFrame(() =>
    {
        long now = Stopwatch.GetTimestamp();
        var deltaTime = Stopwatch.GetElapsedTime(then, now).TotalSeconds;
        then = now;

        if (settingsIsAnimated)
        {
            time += deltaTime;
        }

        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView: 30 * MathF.PI / 180,
            aspectRatio: (float)WIDTH / HEIGHT,
            nearPlaneDistance: 0.5f,
            farPlaneDistance: 100f
        );

        var m = Matrix4x4.Identity;
        m.RotateX((float)time);
        m.RotateY((float)time * 0.7f);
        m.Translate(LerpV(new Vector3(0, 0, 5), new Vector3(0, 0, 40), PingPongSine((float)time * 0.2f)));
        if (!Matrix4x4.Invert(m, out Matrix4x4 view))
        {
            Console.WriteLine("Failed to invert matrix");
        }
        var viewProjection = view * projection;
        var surfaceTexture = surface.GetCurrentTexture()!.Texture!;

        if (depthTexture == null || depthTexture.GetWidth() != WIDTH || depthTexture.GetHeight() != HEIGHT)
        {
            depthTexture?.Destroy();
            depthTexture = device.CreateTexture(new()
            {
                Size = new(surfaceTexture.GetWidth(), surfaceTexture.GetHeight(), surfaceTexture.GetDepthOrArrayLayers()),
                Format = DEPTH_FORMAT,
                Usage = TextureUsage.RenderAttachment
            });
        }

        var colorTexture = surface.GetCurrentTexture()!.Texture!;
        var renderPassDescriptor = new RenderPassDescriptor
        {
            ColorAttachments = [
                new()
                {
                    View = colorTexture.CreateView(),
                    ClearValue = new(0.5f, 0.5f, 0.5f, 1.0f),
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                }
            ],
            DepthStencilAttachment = new RenderPassDepthStencilAttachment
            {
                View = depthTexture.CreateView(),
                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            },
            OcclusionQuerySet = querySet
        };

        var encoder = device.CreateCommandEncoder();
        var pass = encoder.BeginRenderPass(renderPassDescriptor);
        pass.SetPipeline(pipeline);
        pass.SetVertexBuffer(0, vertexBuf);
        pass.SetIndexBuffer(indicesBuf, IndexFormat.Uint16);

        for (int i = 0; i < objectInfos.Count; i++)
        {
            var objectInfo = objectInfos[i];
            var world = Matrix4x4.CreateTranslation(objectInfo.Position);
            Matrix4x4.Invert(world, out Matrix4x4 worldInverse);

            objectInfo.Uniforms = objectInfo.Uniforms with
            {
                WorldViewProjection = Matrix4x4.Multiply(world, viewProjection),
                WorldInverseTranspose = Matrix4x4.Transpose(worldInverse),
            };

            device.GetQueue().WriteBuffer(objectInfo.UniformBuffer, 0, objectInfo.Uniforms);

            pass.SetBindGroup(0, objectInfo.BindGroup);
            pass.BeginOcclusionQuery((uint)i);
            pass.DrawIndexed((uint)indices.Length);
            pass.EndOcclusionQuery();
        }

        pass.End();
        encoder.ResolveQuerySet(querySet, 0, (uint)objectInfos.Count, resolveBuf, 0);
        if (resultBuf.GetMapState() == BufferMapState.Unmapped)
        {
            encoder.CopyBufferToBuffer(resolveBuf, 0, resultBuf, 0, resolveBuf.GetSize());
        }

        var guiCommanderBuffer = DrawGui(guiContext, surface);

        device.GetQueue().Submit([encoder.Finish(), guiCommanderBuffer]);
        surface.Present();

        if (resultBuf.GetMapState() == BufferMapState.Unmapped)
        {
            resultBuf.MapAsync(MapMode.Read).ContinueWith(status =>
            {
                resultBuf.GetConstMappedRange<ulong>(data =>
                {
                    cubeVisibility.Clear();
                    for (int i = 0; i < objectInfos.Count; i++)
                    {
                        var objectInfo = objectInfos[i];
                        var visible = data[i] > 0;
                        if (visible)
                        {
                            cubeVisibility.Add(objectInfo.Uniforms.ColorValue);
                        }
                    }
                });
                resultBuf.Unmap();
            });
        }
    });
});


class ObjectInfos
{
    public struct UniformData
    {
        public Matrix4x4 WorldViewProjection;
        public Matrix4x4 WorldInverseTranspose;
        public Vector4 ColorValue;
    }

    public required Vector3 Position { get; init; }
    public required BindGroup BindGroup { get; init; }
    public required WebGpuSharp.Buffer UniformBuffer { get; init; }
    public required UniformData Uniforms { get; set; }
}