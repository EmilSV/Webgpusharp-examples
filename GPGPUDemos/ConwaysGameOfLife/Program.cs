using System.Reflection;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using GPUBuffer = WebGpuSharp.Buffer;


const int WIDTH = 800;
const int HEIGHT = 800;
int[] workgroupSizes = [4, 8, 16];
string[] workgroupSizesNames = ["4", "8", "16"];
bool shouldResetGameState = false;


static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

var gameOptions = new GameOptions();
var asm = Assembly.GetExecutingAssembly();
var computeWGSL = ToBytes(asm.GetManifestResourceStream("ConwaysGameOfLife.shaders.compute.wgsl")!);
var fragWGSL = ToBytes(asm.GetManifestResourceStream("ConwaysGameOfLife.shaders.frag.wgsl")!);
var vertWGSL = ToBytes(asm.GetManifestResourceStream("ConwaysGameOfLife.shaders.vert.wgsl")!);


CommandBuffer DrawGui(GuiContext guiContext, Surface surface, out bool resetGameState)
{
    resetGameState = false;

    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(0, 0));
    ImGui.SetNextWindowSize(new(350, 240));
    ImGui.Begin("Conways Game of Life",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoTitleBar
    );
    int timestep = (int)gameOptions.TimeStep;
    if (ImGui.InputInt("timestep", ref timestep, step: 1))
    {
        gameOptions.TimeStep = (uint)Math.Clamp(timestep, 1, 60);
    }

    int width = (int)gameOptions.Width;
    if (ImGui.InputInt("width", ref width, step: 16))
    {
        gameOptions.Width = (uint)Math.Clamp(width, 16, 1024);
        resetGameState = true;
    }
    int height = (int)gameOptions.Height;
    if (ImGui.InputInt("height", ref height, step: 16))
    {
        gameOptions.Height = (uint)Math.Clamp(height, 16, 1024);
        resetGameState = true;
    }

    int workgroupSize = (int)gameOptions.WorkgroupSize;
    int currentItem = Array.IndexOf(workgroupSizes, workgroupSize);
    if (currentItem == -1)
    {
        currentItem = 0;
        workgroupSize = workgroupSizes[0];
    }

    if (ImGui.Combo("workgroup size", ref currentItem, workgroupSizesNames, workgroupSizes.Length))
    {
        gameOptions.WorkgroupSize = (uint)workgroupSizes[currentItem];
        resetGameState = true;
    }
    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}

return Run("Conway's Game of Life", WIDTH, HEIGHT, async runContext =>
{
    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();

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
        RequiredLimits = LimitsDefaults.GetDefaultLimits() with
        {
            MaxComputeInvocationsPerWorkgroup = 256
        }
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

    var computeShader = device.CreateShaderModuleWGSL(new()
    {
        Code = computeWGSL
    });

    var bindGroupLayoutCompute = device.CreateBindGroupLayout(new()
    {
        Entries = new[]
        {
            new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Compute,
                Buffer = new()
                {
                    Type = BufferBindingType.ReadOnlyStorage,
                }
            },
            new BindGroupLayoutEntry
            {
                Binding = 1,
                Visibility = ShaderStage.Compute,
                Buffer = new()
                {
                    Type = BufferBindingType.ReadOnlyStorage,
                }
            },
            new BindGroupLayoutEntry
            {
                Binding = 2,
                Visibility = ShaderStage.Compute,
                Buffer = new()
                {
                    Type = BufferBindingType.Storage
                }
            },
        }
    });


    uint[] squareVertices = [0, 0, 0, 1, 1, 0, 1, 1];
    var squareBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)squareVertices.GetSizeInBytes(),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true
    });

    squareBuffer.GetMappedRange<uint>(data => ((ReadOnlySpan<uint>)squareVertices).CopyTo(data));
    squareBuffer.Unmap();

    VertexBufferLayout squareStride = new()
    {
        ArrayStride = sizeof(uint) * 2,
        StepMode = VertexStepMode.Vertex,
        Attributes = [
            new()
            {
                ShaderLocation = 1,
                Offset = 0,
                Format = VertexFormat.Uint32x2
            }
        ]
    };

    var vertexShader = device.CreateShaderModuleWGSL(new()
    {
        Code = vertWGSL
    });

    var fragmentShader = device.CreateShaderModuleWGSL(new()
    {
        Code = fragWGSL
    });

    var bindGroupLayoutRender = device.CreateBindGroupLayout(new()
    {
        Entries = [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex,
                Buffer = new()
                {
                    Type = BufferBindingType.Uniform,
                }
            },
        ]
    });

    VertexBufferLayout cellsStride = new()
    {
        ArrayStride = sizeof(uint),
        StepMode = VertexStepMode.Instance,
        Attributes = [
            new()
            {
                ShaderLocation = 0,
                Offset = 0,
                Format = VertexFormat.Uint32
            }
        ]
    };

    int wholeTime = 0;
    int currentIndex = 0;
    (GPUBuffer? Buffer, BindGroup? BindGroup)[] bufferAndBindGroups = [
        (null, null),
        (null, null)
    ];
    Action<bool>? render = null;

    void ResetGameData()
    {
        var computePipeline = device.CreateComputePipelineSync(new()
        {
            Layout = device.CreatePipelineLayout(new()
            {
                BindGroupLayouts = [bindGroupLayoutCompute]
            }),
            Compute = new()
            {
                Module = computeShader,
                Constants = [
                    new("blockSize", gameOptions.WorkgroupSize)
                ]
            }
        });

        var sizeBuffer = device.CreateBuffer(new()
        {
            Size = 2 * sizeof(uint),
            Usage =
                BufferUsage.Storage |
                BufferUsage.Uniform |
                BufferUsage.CopyDst |
                BufferUsage.Vertex,
            MappedAtCreation = true
        });

        sizeBuffer.GetMappedRange<uint>(data =>
        {
            data[0] = gameOptions.Width;
            data[1] = gameOptions.Height;
        });
        sizeBuffer.Unmap();
        var length = gameOptions.Width * gameOptions.Height;
        var cells = new uint[length];
        for (int i = 0; i < length; i++)
        {
            cells[i] = Random.Shared.NextDouble() < 0.25 ? 1u : 0u;
        }

        var buffer0 = device.CreateBuffer(new()
        {
            Size = cells.GetSizeInBytes(),
            Usage =
                BufferUsage.Storage |
                BufferUsage.Vertex,
            MappedAtCreation = true
        });

        buffer0.GetMappedRange<uint>(data => ((ReadOnlySpan<uint>)cells).CopyTo(data));
        buffer0.Unmap();

        var buffer1 = device.CreateBuffer(new()
        {
            Size = cells.GetSizeInBytes(),
            Usage =
                BufferUsage.Storage |
                BufferUsage.Vertex,
        });

        var bindGroup0 = device.CreateBindGroup(new()
        {
            Layout = bindGroupLayoutCompute,
            Entries = [
                new()
                {
                    Binding = 0,
                    Buffer = sizeBuffer,
                },
                new()
                {
                    Binding = 1,
                        Buffer = buffer0,
                },
                new()
                {
                    Binding = 2,
                    Buffer = buffer1,
                },
            ]
        });

        var bindGroup1 = device.CreateBindGroup(new()
        {
            Layout = bindGroupLayoutCompute,
            Entries = [
                new()
                {
                    Binding = 0,
                    Buffer = sizeBuffer,
                },
                new()
                {
                    Binding = 1,
                        Buffer = buffer1,
                },
                new()
                {
                    Binding = 2,
                    Buffer = buffer0,
                },
            ]
        });

        bufferAndBindGroups[0] = (buffer0, bindGroup0);
        bufferAndBindGroups[1] = (buffer1, bindGroup1);

        var renderPipeline = device.CreateRenderPipelineSync(new()
        {
            Layout = device.CreatePipelineLayout(new()
            {
                BindGroupLayouts = [bindGroupLayoutRender]
            }),
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleStrip,
            },
            Vertex = new()
            {
                Module = vertexShader,
                Buffers = [cellsStride, squareStride]
            },
            Fragment = new()
            {
                Module = fragmentShader,
                Targets = [
                    new()
                    {
                        Format = surfaceFormat
                    }
                ]
            },
        });

        var uniformBindGroup = device.CreateBindGroup(new()
        {
            Layout = bindGroupLayoutRender,
            Entries = [
                new()
                {
                    Binding = 0,
                    Buffer = sizeBuffer,
                    Offset = 0,
                    Size = 2 * sizeof(uint)
                },
            ]
        });

        currentIndex = 0;

        render = (bool doCompute) =>
        {
            var view = surface.GetCurrentTexture().Texture!.CreateView();
            var renderPass = new RenderPassDescriptor
            {
                ColorAttachments = [
                    new()
                    {
                        View = view,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                    }
                ]
            };
            var commandEncoder = device.CreateCommandEncoder();

            if (doCompute)
            {
                // compute
                var passEncoderCompute = commandEncoder.BeginComputePass();
                passEncoderCompute.SetPipeline(computePipeline);
                passEncoderCompute.SetBindGroup(0, bufferAndBindGroups[currentIndex].BindGroup!);
                passEncoderCompute.DispatchWorkgroups(
                    gameOptions.Width / gameOptions.WorkgroupSize,
                    gameOptions.Height / gameOptions.WorkgroupSize
                );
                passEncoderCompute.End();
                currentIndex ^= 1;
            }
            // render 
            var passEncoderRender = commandEncoder.BeginRenderPass(renderPass);
            passEncoderRender.SetPipeline(renderPipeline);
            passEncoderRender.SetVertexBuffer(0, bufferAndBindGroups[currentIndex].Buffer!);
            passEncoderRender.SetVertexBuffer(1, squareBuffer);
            passEncoderRender.SetBindGroup(0, uniformBindGroup);
            passEncoderRender.Draw(4, length);
            passEncoderRender.End();

            var guiCommandBuffer = DrawGui(guiContext, surface, out var newResetGameState);
            shouldResetGameState |= newResetGameState;
            query.Submit([commandEncoder.Finish(), guiCommandBuffer]);
            surface.Present();
        };
    }

    ResetGameData();

    runContext.OnFrame += () =>
    {
        if (shouldResetGameState)
        {
            ResetGameData();
            shouldResetGameState = false;
            wholeTime = (int)gameOptions.TimeStep;
        }

        bool doCompute = false;
        if (gameOptions.TimeStep != 0)
        {
            wholeTime++;
            if (wholeTime >= gameOptions.TimeStep)
            {
                doCompute = true;
                wholeTime -= (int)gameOptions.TimeStep;
            }
        }
        render?.Invoke(doCompute);
    };
});


class GameOptions
{
    public uint Width = 128;
    public uint Height = 128;
    public uint TimeStep = 4;
    public uint WorkgroupSize = 8;
}