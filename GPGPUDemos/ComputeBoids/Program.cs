using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
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


NativeLibrary.Load("C:\\Users\\emils\\git_repos\\webgpu-dawn-build\\dawn\\dawn_build_x64\\Debug\\webgpu_dawn.dll");
Environment.SetEnvironmentVariable("DAWN_DEBUG_BREAK_ON_ERROR", "1");
//intercept whenever WebGpuSharp request webgpu_dawn.dll
NativeLibrary.SetDllImportResolver(
    assembly: typeof(WebGpuSharp.WebGPU).Assembly,
    resolver: (libraryName, assembly, searchPath) =>
    {
        if (libraryName == "webgpu_dawn")
        {
            return NativeLibrary.Load("C:\\Users\\emils\\git_repos\\webgpu-dawn-build\\dawn\\dawn_build_x64\\Debug\\webgpu_dawn.dll");
        }
        return IntPtr.Zero;
    }
);


string perfDisplayText = "";
var asm = Assembly.GetExecutingAssembly();
var spriteWGSL = ToBytes(asm.GetManifestResourceStream("ComputeBoids.shaders.sprite.wgsl")!);
var updateSpritesWGSL = ToBytes(asm.GetManifestResourceStream("ComputeBoids.shaders.updateSprites.wgsl")!);

CommandBuffer DrawGui(GuiContext guiContext, Surface surface, ref SimParams simParams, out bool simParamsChanged)
{
    simParamsChanged = false;

    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(0, 0));
    ImGui.SetNextWindowSize(new(350, 240));
    ImGui.Begin("Compute Boids",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoTitleBar
    );
    ImGui.Text(perfDisplayText);
    ImGui.Separator();
    ImGui.Text("Simulation parameters:");
    simParamsChanged |= ImGui.InputFloat("DeltaT", ref simParams.DeltaT);
    simParamsChanged |= ImGui.InputFloat("Rule1Distance", ref simParams.Rule1Distance);
    simParamsChanged |= ImGui.InputFloat("Rule2Distance", ref simParams.Rule2Distance);
    simParamsChanged |= ImGui.InputFloat("Rule3Distance", ref simParams.Rule3Distance);
    simParamsChanged |= ImGui.InputFloat("Rule1Scale", ref simParams.Rule1Scale);
    simParamsChanged |= ImGui.InputFloat("Rule2Scale", ref simParams.Rule2Scale);
    simParamsChanged |= ImGui.InputFloat("Rule3Scale", ref simParams.Rule3Scale);
    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}


return Run("Compute Boids", WIDTH, HEIGHT, async (instance, surface, guiContext, onFrame) =>
{
    var adapter = await instance.RequestAdapterAsync(new()
    {
        CompatibleSurface = surface,
        BackendType = BackendType.Vulkan,
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

    var spriteShaderModule = device.CreateShaderModuleWGSL(new()
    {
        Code = spriteWGSL
    });

    var renderPipeline = device.CreateRenderPipelineSync(new()
    {
        Layout = null,
        Vertex = new()
        {
            Module = spriteShaderModule,
            Buffers = [
                new()
                {
                    // instanced particles buffer
                    ArrayStride = 4*4,
                    StepMode = VertexStepMode.Instance,
                    Attributes = [
                        new()
                        {
                            ShaderLocation = 0,
                            Offset = 0,
                            Format = VertexFormat.Float32x2
                        },
                        new()
                        {
                            ShaderLocation = 1,
                            Offset = 2 * 4,
                            Format = VertexFormat.Float32x2
                        }
                    ]
                },
                new()
                {
                    // quad vertex buffer
                    ArrayStride = 2 * 4,
                    StepMode = VertexStepMode.Vertex,
                    Attributes = [
                        new()
                        {
                            ShaderLocation = 2,
                            Offset = 0,
                            Format = VertexFormat.Float32x2
                        }
                    ]
                }
            ]
        },
        Fragment = new()
        {
            Module = spriteShaderModule,
            Targets = [new() { Format = surfaceFormat }]
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,
        }
    });

    var computePipeline = device.CreateComputePipelineSync(new()
    {
        Layout = null,
        Compute = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = updateSpritesWGSL
            }),
        }
    });

    /** Storage for timestamp query results */
    QuerySet? querySet = null;
    /** Timestamps are resolved into this buffer */
    GPUBuffer? resolveBuffer = null;


    /// Pool of spare buffers for MAP_READing the timestamps back to CPU. A buffer
    /// is taken from the pool (if available) when a readback is needed, and placed
    /// back into the pool once the readback is done and it's unmapped.
    Stack<GPUBuffer> spareResultBuffers = new();

    if (hasTimestampQuery)
    {
        querySet = device.CreateQuerySet(new()
        {
            Type = QueryType.Timestamp,
            Count = 4
        });

        resolveBuffer = device.CreateBuffer(new()
        {
            Size = 4 * sizeof(ulong),
            Usage = BufferUsage.QueryResolve | BufferUsage.CopySrc,
        });
    }

    var vertexBufferData = new float[]
    {
        -0.01f, -0.02f, 0.01f,
        -0.02f, 0.0f, 0.02f,
    };

    var spriteVertexBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(vertexBufferData.Length * sizeof(float)),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true
    });
    spriteVertexBuffer.GetMappedRange<float>(data => vertexBufferData.CopyTo(data));
    spriteVertexBuffer.Unmap();

    SimParams simParams = new()
    {
        DeltaT = 0.04f,
        Rule1Distance = 0.1f,
        Rule2Distance = 0.025f,
        Rule3Distance = 0.025f,
        Rule1Scale = 0.02f,
        Rule2Scale = 0.05f,
        Rule3Scale = 0.005f
    };

    var simParamBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Marshal.SizeOf<SimParams>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst
    });

    void UpdateSimParams()
    {
        device.GetQueue().WriteBuffer(simParamBuffer, 0, simParams);
    }
    UpdateSimParams();

    const int NUM_PARTICLES = 1500;
    var initialParticleData = new Vector4[NUM_PARTICLES];
    for (int i = 0; i < NUM_PARTICLES; i++)
    {
        initialParticleData[i] = new(
            2f * (Random.Shared.NextSingle() - 0.5f),
            2f * (Random.Shared.NextSingle() - 0.5f),
            2f * (Random.Shared.NextSingle() - 0.5f) * 0.1f,
            2f * (Random.Shared.NextSingle() - 0.5f) * 0.1f
        );
    }

    var particleBuffers = new GPUBuffer[2];
    var particleBindGroups = new BindGroup[2];
    for (int i = 0; i < 2; i++)
    {
        particleBuffers[i] = device.CreateBuffer(new()
        {
            Size = initialParticleData.GetSizeInBytes(),
            Usage = BufferUsage.Vertex | BufferUsage.Storage,
            MappedAtCreation = true
        });
        particleBuffers[i].GetMappedRange<Vector4>(data => initialParticleData.CopyTo(data));
        particleBuffers[i].Unmap();
    }

    for (int i = 0; i < 2; i++)
    {
        particleBindGroups[i] = device.CreateBindGroup(new()
        {
            Layout = computePipeline.GetBindGroupLayout(0),
            Entries = [
                new()
                {
                    Binding = 0,
                    Buffer = simParamBuffer,
                },
                new()
                {
                    Binding = 1,
                    Buffer = particleBuffers[i],
                    Offset = 0,
                    Size = initialParticleData.GetSizeInBytes()
                },
                new()
                {
                    Binding = 2,
                    Buffer = particleBuffers[(i + 1) % 2],
                    Offset = 0,
                    Size = initialParticleData.GetSizeInBytes()
                }
            ]
        });
    }

    ulong t = 0;
    float computePassDurationSum = 0;
    float renderPassDurationSum = 0;
    ulong timerSamples = 0;
    onFrame(() =>
    {
        var renderPassDescriptor = new RenderPassDescriptor()
        {
            ColorAttachments = [
                new()
                {
                    View = surface.GetCurrentTexture().Texture!.CreateView(),
                    ClearValue = new Color(0f, 0f, 0f, 1.0f),
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                }
            ]
        };
        var computePassDescriptor = new ComputePassDescriptor()
        {

        };

        if (hasTimestampQuery)
        {
            computePassDescriptor.TimestampWrites = new()
            {
                QuerySet = querySet!,
                BeginningOfPassWriteIndex = 0,
                EndOfPassWriteIndex = 1
            };

            renderPassDescriptor.TimestampWrites = new()
            {
                QuerySet = querySet!,
                BeginningOfPassWriteIndex = 2,
                EndOfPassWriteIndex = 3
            };
        }

        var commandEncoder = device.CreateCommandEncoder();
        {
            var passEncoder = commandEncoder.BeginComputePass(computePassDescriptor);
            passEncoder.SetPipeline(computePipeline);
            passEncoder.SetBindGroup(0, particleBindGroups[t % 2]);
            passEncoder.DispatchWorkgroups((uint)MathF.Ceiling(NUM_PARTICLES / 64f));
            passEncoder.End();
        }
        {
            var passEncoder = commandEncoder.BeginRenderPass(renderPassDescriptor);
            passEncoder.SetPipeline(renderPipeline);
            passEncoder.SetVertexBuffer(0, particleBuffers[(t + 1) % 2]);
            passEncoder.SetVertexBuffer(1, spriteVertexBuffer);
            passEncoder.Draw(3, (uint)NUM_PARTICLES, 0, 0);
            passEncoder.End();
        }

        GPUBuffer? resultBuffer = null;
        if (hasTimestampQuery)
        {
            resultBuffer = spareResultBuffers.Count > 0 ?
                spareResultBuffers.Pop() :
                device.CreateBuffer(new()
                {
                    Size = 4 * sizeof(ulong),
                    Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
                });
            commandEncoder.ResolveQuerySet(querySet!, 0, 4, resolveBuffer!, 0);
            commandEncoder.CopyBufferToBuffer(resolveBuffer!, resultBuffer!);
        }

        var guiCommanderBuffer = DrawGui(guiContext, surface, ref simParams, out var simParamsChanged);
        if (simParamsChanged)
        {
            UpdateSimParams();
        }

        device.GetQueue().Submit([commandEncoder.Finish(), guiCommanderBuffer]);
        surface.Present();

        if (hasTimestampQuery)
        {
            resultBuffer!.MapAsync(MapMode.Read).ContinueWith((t) =>
            {
                float computePassDuration = 0;
                float renderPassDuration = 0;
                resultBuffer.GetConstMappedRange<ulong>(times =>
                {
                    computePassDuration = (times[1] - times[0]);
                    renderPassDuration = (times[3] - times[2]);
                });
                // In some cases the timestamps may wrap around and produce a negative
                // number as the GPU resets it's timings. These can safely be ignored.
                if (computePassDuration > 0 && renderPassDuration > 0)
                {
                    computePassDurationSum += computePassDuration;
                    renderPassDurationSum += renderPassDuration;
                    timerSamples++;
                }
                resultBuffer.Unmap();
                // Periodically update the text for the timer stats
                const int NUM_TIMER_SAMPLES_PER_UPDATE = 100;
                if (timerSamples >= NUM_TIMER_SAMPLES_PER_UPDATE)
                {
                    var avgComputeMicroseconds = MathF.Round(computePassDurationSum / timerSamples / 1000f);
                    var avgRenderMicroseconds = MathF.Round(renderPassDurationSum / timerSamples / 1000f);
                    perfDisplayText =
                    $"""
                     Avg compute pass duration: {avgComputeMicroseconds}µs
                     Avg render pass duration: {avgRenderMicroseconds}µs
                     spare readback buffers: {spareResultBuffers.Count}
                    """;

                    computePassDurationSum = 0;
                    renderPassDurationSum = 0;
                    timerSamples = 0;
                }
                spareResultBuffers.Push(resultBuffer);
            });
        }
        ++t;
    });
});


public struct SimParams
{
    public float DeltaT;
    public float Rule1Distance;
    public float Rule2Distance;
    public float Rule3Distance;
    public float Rule1Scale;
    public float Rule2Scale;
    public float Rule3Scale;
}