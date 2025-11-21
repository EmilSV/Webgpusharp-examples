using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using WebGpuSharp;
using WebGpuSharp.FFI;
using Setup;
using ImGuiNET;
using static Setup.SetupWebGPU;

namespace BitonicSort.GenTest
{
    public enum StepEnum
    {
        NONE,
        FLIP_LOCAL,
        DISPERSE_LOCAL,
        FLIP_GLOBAL,
        DISPERSE_GLOBAL,
    }

    public enum DisplayType
    {
        Elements,
        SwapHighlight
    }

    public class ConfigInfo
    {
        public int Sorts;
        public double Time;
    }

    public class Settings
    {
        public int TotalElements;
        public int GridWidth;
        public int GridHeight;
        public string GridDimensions = "";
        public uint WorkgroupSize;
        public int SizeLimit;
        public int WorkgroupsPerStep;
        public int HoveredCell;
        public int SwappedCell;
        public string CurrentStep = "";
        public int StepIndex;
        public int TotalSteps;
        public StepEnum PrevStep;
        public StepEnum NextStep;
        public int PrevSwapSpan;
        public int NextSwapSpan;
        public bool ExecuteStep;
        public Action? RandomizeValues;
        public Action? ExecuteSortStep;
        public Action? LogElements;
        public Action? AutoSort;
        public int AutoSortSpeed;
        public DisplayType DisplayMode;
        public int TotalSwaps;
        public double StepTime;
        public string StepTimeString = "";
        public double SortTime;
        public string SortTimeString = "";
        public string AverageSortTimeString = "";
        public Dictionary<string, ConfigInfo> ConfigToCompleteSwapsMap = new();
        public string ConfigKey = "";
    }

    public static class Program
    {
        public static double GetNumSteps(int numElements)
        {
            var n = Math.Log2(numElements);
            return n * (n + 1) / 2;
        }

        public const string AtomicToZeroWGSL = @"
@group(0) @binding(3) var<storage, read_write> counter: atomic<u32>;

@compute @workgroup_size(1, 1, 1)
fn atomicToZero() {
  let counterValue = atomicLoad(&counter);
  atomicSub(&counter, counterValue);
}
";

        public static int Run()
        {
            const int WIDTH = 1280;
            const int HEIGHT = 720;

            return SetupWebGPU.Run("Bitonic Sort GenTest", WIDTH, HEIGHT, async runContext =>
            {
                var instance = runContext.GetInstance();
                var surface = runContext.GetSurface();
                var adapter = await instance.RequestAdapterAsync(new RequestAdapterOptions
                {
                    CompatibleSurface = surface,
                    FeatureLevel = FeatureLevel.Compatibility
                });

                var hasTimestampQuery = adapter.HasFeature(FeatureName.TimestampQuery);
                var device = await adapter.RequestDeviceAsync(new DeviceDescriptor
                {
                    RequiredFeatures = hasTimestampQuery ? new[] { FeatureName.TimestampQuery } : Array.Empty<FeatureName>(),
                });

                var queue = device.GetQueue();
                var surfaceCapabilities = surface.GetCapabilities(adapter)!;
                var surfaceFormat = surfaceCapabilities.Formats[0];

                surface.Configure(new SurfaceConfiguration
                {
                    Device = device,
                    Format = surfaceFormat,
                    Usage = TextureUsage.RenderAttachment,
                    Width = WIDTH,
                    Height = HEIGHT,
                    PresentMode = PresentMode.Fifo,
                    AlphaMode = CompositeAlphaMode.Auto
                });

                var maxInvocationsX = device.GetLimits().MaxComputeWorkgroupSizeX;

                QuerySet? querySet = null;
                WebGpuSharp.Buffer? timestampQueryResolveBuffer = null;
                WebGpuSharp.Buffer? timestampQueryResultBuffer = null;

                if (hasTimestampQuery)
                {
                    querySet = device.CreateQuerySet(new QuerySetDescriptor
                    {
                        Type = QueryType.Timestamp,
                        Count = 2
                    });
                    timestampQueryResolveBuffer = device.CreateBuffer(new BufferDescriptor
                    {
                        Size = 2 * 8, // 2 * sizeof(long)
                        Usage = BufferUsage.QueryResolve | BufferUsage.CopySrc
                    });
                    timestampQueryResultBuffer = device.CreateBuffer(new BufferDescriptor
                    {
                        Size = 2 * 8,
                        Usage = BufferUsage.CopyDst | BufferUsage.MapRead
                    });
                }

                var totalElementOptions = new List<int>();
                var maxElements = maxInvocationsX * 32;
                for (var i = maxElements; i >= 4; i /= 2)
                {
                    totalElementOptions.Add((int)i);
                }

                var sizeLimitOptions = new List<int>();
                for (var i = maxInvocationsX; i >= 2; i /= 2)
                {
                    sizeLimitOptions.Add((int)i);
                }

                var defaultGridWidth = (int)(Math.Sqrt(maxElements) % 2 == 0
                    ? Math.Floor(Math.Sqrt(maxElements))
                    : Math.Floor(Math.Sqrt(maxElements / 2)));
                var defaultGridHeight = (int)(maxElements / defaultGridWidth);

                var settings = new Settings
                {
                    TotalElements = (int)maxElements,
                    GridWidth = defaultGridWidth,
                    GridHeight = defaultGridHeight,
                    GridDimensions = $"{defaultGridWidth}x{defaultGridHeight}",
                    WorkgroupSize = maxInvocationsX,
                    SizeLimit = (int)maxInvocationsX,
                    WorkgroupsPerStep = (int)(maxElements / (maxInvocationsX * 2)),
                    HoveredCell = 0,
                    SwappedCell = 1,
                    StepIndex = 0,
                    TotalSteps = (int)GetNumSteps((int)maxElements),
                    CurrentStep = "0 of 91",
                    PrevStep = StepEnum.NONE,
                    NextStep = StepEnum.FLIP_LOCAL,
                    PrevSwapSpan = 0,
                    NextSwapSpan = 2,
                    ExecuteStep = false,
                    AutoSortSpeed = 50,
                    DisplayMode = DisplayType.Elements,
                    TotalSwaps = 0,
                    StepTime = 0,
                    StepTimeString = "0ms",
                    SortTime = 0,
                    SortTimeString = "0ms",
                    AverageSortTimeString = "0ms",
                    ConfigToCompleteSwapsMap = new Dictionary<string, ConfigInfo>
                    {
                        { "8192 256", new ConfigInfo { Sorts = 0, Time = 0 } }
                    },
                    ConfigKey = "8192 256"
                };

                var elements = new uint[settings.TotalElements];
                for (int i = 0; i < elements.Length; i++) elements[i] = (uint)i;

                var elementsBufferSize = (ulong)(4 * totalElementOptions[0]); 

                var elementsInputBuffer = device.CreateBuffer(new BufferDescriptor
                {
                    Size = elementsBufferSize,
                    Usage = BufferUsage.Storage | BufferUsage.CopyDst
                });
                var elementsOutputBuffer = device.CreateBuffer(new BufferDescriptor
                {
                    Size = elementsBufferSize,
                    Usage = BufferUsage.Storage | BufferUsage.CopySrc
                });
                var elementsStagingBuffer = device.CreateBuffer(new BufferDescriptor
                {
                    Size = elementsBufferSize,
                    Usage = BufferUsage.MapRead | BufferUsage.CopyDst
                });

                var atomicSwapsOutputBuffer = device.CreateBuffer(new BufferDescriptor
                {
                    Size = 4,
                    Usage = BufferUsage.Storage | BufferUsage.CopySrc
                });
                var atomicSwapsStagingBuffer = device.CreateBuffer(new BufferDescriptor
                {
                    Size = 4,
                    Usage = BufferUsage.MapRead | BufferUsage.CopyDst
                });

                var computeUniformsBuffer = device.CreateBuffer(new BufferDescriptor
                {
                    Size = 4 * 4,
                    Usage = BufferUsage.Uniform | BufferUsage.CopyDst
                });

                var computeBGCluster = Utils.CreateBindGroupCluster(
                    new[] { 0, 1, 2, 3 },
                    new[] {
                        ShaderStage.Compute | ShaderStage.Fragment,
                        ShaderStage.Compute,
                        ShaderStage.Compute | ShaderStage.Fragment,
                        ShaderStage.Compute
                    },
                    new[] { "buffer", "buffer", "buffer", "buffer" },
                    new object[] {
                        new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage },
                        new BufferBindingLayout { Type = BufferBindingType.Storage },
                        new BufferBindingLayout { Type = BufferBindingType.Uniform },
                        new BufferBindingLayout { Type = BufferBindingType.Storage }
                    },
                    new[] {
                        new[] {
                            new BindGroupEntry { Binding = 0, Buffer = elementsInputBuffer },
                            new BindGroupEntry { Binding = 1, Buffer = elementsOutputBuffer },
                            new BindGroupEntry { Binding = 2, Buffer = computeUniformsBuffer },
                            new BindGroupEntry { Binding = 3, Buffer = atomicSwapsOutputBuffer }
                        }
                    },
                    "BitonicSort",
                    device
                );

                var computePipeline = device.CreateComputePipeline(new ComputePipelineDescriptor
                {
                    Layout = device.CreatePipelineLayout(new PipelineLayoutDescriptor
                    {
                        BindGroupLayouts = new[] { computeBGCluster.BindGroupLayout }
                    }),
                    Compute = new ComputeState
                    {
                        Module = device.CreateShaderModuleWGSL(new ShaderModuleWGSLDescriptor
                        {
                            Code = BitonicCompute.NaiveBitonicCompute(settings.WorkgroupSize)
                        }),
                        EntryPoint = "computeMain"
                    }
                });

                var atomicToZeroComputePipeline = device.CreateComputePipeline(new ComputePipelineDescriptor
                {
                    Layout = device.CreatePipelineLayout(new PipelineLayoutDescriptor
                    {
                        BindGroupLayouts = new[] { computeBGCluster.BindGroupLayout }
                    }),
                    Compute = new ComputeState
                    {
                        Module = device.CreateShaderModuleWGSL(new ShaderModuleWGSLDescriptor
                        {
                            Code = AtomicToZeroWGSL
                        }),
                        EntryPoint = "atomicToZero"
                    }
                });

                var colorAttachments = new[]
                {
                    new RenderPassColorAttachment
                    {
                        View = null, // Assigned later
                        ClearValue = new Color(0.1, 0.4, 0.5, 1.0),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store
                    }
                };

                var bitonicDisplayRenderer = new BitonicDisplayRenderer(
                    device,
                    surfaceFormat,
                    colorAttachments,
                    computeBGCluster,
                    "BitonicDisplay"
                );

                Action resetTimeInfo = () =>
                {
                    settings.StepTime = 0;
                    settings.SortTime = 0;
                    settings.StepTimeString = "0ms";
                    settings.SortTimeString = "0ms";
                    if (settings.ConfigToCompleteSwapsMap.ContainsKey(settings.ConfigKey))
                    {
                        var info = settings.ConfigToCompleteSwapsMap[settings.ConfigKey];
                        var nanCheck = info.Sorts > 0 ? info.Time / info.Sorts : 0;
                        settings.AverageSortTimeString = $"{nanCheck:F5}ms";
                    }
                };

                int highestBlockHeight = 2;

                Action resetExecutionInformation = () =>
                {
                    var constraint = Math.Min(settings.TotalElements / 2, settings.SizeLimit);
                    settings.WorkgroupSize = (uint)constraint;
                    var workgroupsPerStep = (settings.TotalElements - 1) / (settings.SizeLimit * 2);
                    settings.WorkgroupsPerStep = (int)Math.Ceiling((double)workgroupsPerStep);

                    settings.StepIndex = 0;
                    settings.TotalSteps = (int)GetNumSteps(settings.TotalElements);
                    settings.CurrentStep = $"{settings.StepIndex} of {settings.TotalSteps}";

                    var newCellWidth = Math.Sqrt(settings.TotalElements) % 2 == 0
                        ? (int)Math.Floor(Math.Sqrt(settings.TotalElements))
                        : (int)Math.Floor(Math.Sqrt(settings.TotalElements / 2));
                    var newCellHeight = settings.TotalElements / newCellWidth;
                    settings.GridWidth = newCellWidth;
                    settings.GridHeight = newCellHeight;
                    settings.GridDimensions = $"{newCellWidth}x{newCellHeight}";

                    settings.PrevStep = StepEnum.NONE;
                    settings.NextStep = StepEnum.FLIP_LOCAL;
                    settings.PrevSwapSpan = 0;
                    settings.NextSwapSpan = 2;

                    var commandEncoder = device.CreateCommandEncoder();
                    var computePassEncoder = commandEncoder.BeginComputePass();
                    computePassEncoder.SetPipeline(atomicToZeroComputePipeline);
                    computePassEncoder.SetBindGroup(0, computeBGCluster.BindGroups[0]);
                    computePassEncoder.DispatchWorkgroups(1);
                    computePassEncoder.End();
                    queue.Submit(new[] { commandEncoder.Finish() });
                    settings.TotalSwaps = 0;

                    highestBlockHeight = 2;
                };

                Action randomizeElementArray = () =>
                {
                    var rnd = new Random();
                    for (int i = elements.Length - 1; i > 0; i--)
                    {
                        int j = rnd.Next(i + 1);
                        (elements[i], elements[j]) = (elements[j], elements[i]);
                    }
                };

                Action resizeElementArray = () =>
                {
                    elements = new uint[settings.TotalElements];
                    for (int i = 0; i < elements.Length; i++) elements[i] = (uint)i;

                    resetExecutionInformation();

                    computePipeline = device.CreateComputePipeline(new ComputePipelineDescriptor
                    {
                        Layout = device.CreatePipelineLayout(new PipelineLayoutDescriptor
                        {
                            BindGroupLayouts = new[] { computeBGCluster.BindGroupLayout }
                        }),
                        Compute = new ComputeState
                        {
                            Module = device.CreateShaderModuleWGSL(new ShaderModuleWGSLDescriptor
                            {
                                Code = BitonicCompute.NaiveBitonicCompute(
                                    (uint)Math.Min(settings.TotalElements / 2, settings.SizeLimit)
                                )
                            }),
                            EntryPoint = "computeMain"
                        }
                    });

                    randomizeElementArray();
                    highestBlockHeight = 2;
                };

                randomizeElementArray();

                Action setSwappedCell = () =>
                {
                    int swappedIndex;
                    switch (settings.NextStep)
                    {
                        case StepEnum.FLIP_LOCAL:
                        case StepEnum.FLIP_GLOBAL:
                            {
                                var blockHeight = settings.NextSwapSpan;
                                var p2 = (int)Math.Floor((double)settings.HoveredCell / blockHeight) + 1;
                                var p3 = settings.HoveredCell % blockHeight;
                                swappedIndex = blockHeight * p2 - p3 - 1;
                                settings.SwappedCell = swappedIndex;
                            }
                            break;
                        case StepEnum.DISPERSE_LOCAL:
                            {
                                var blockHeight = settings.NextSwapSpan;
                                var halfHeight = blockHeight / 2;
                                swappedIndex = settings.HoveredCell % blockHeight < halfHeight
                                    ? settings.HoveredCell + halfHeight
                                    : settings.HoveredCell - halfHeight;
                                settings.SwappedCell = swappedIndex;
                            }
                            break;
                        default:
                            swappedIndex = settings.HoveredCell;
                            settings.SwappedCell = swappedIndex;
                            break;
                    }
                };

                // Auto sort logic
                bool autoSortActive = false;
                DateTime lastAutoSortTime = DateTime.Now;

                runContext.OnFrame += () =>
                {
                    var guiContext = runContext.GetGuiContext();
                    guiContext.NewFrame();

                    // ImGui Logic
                    ImGui.SetNextWindowPos(new System.Numerics.Vector2(0, 0));
                    ImGui.SetNextWindowSize(new System.Numerics.Vector2(300, HEIGHT));
                    ImGui.Begin("Controls");
                    ImGui.Text("Bitonic Sort");
                    
                    if (ImGui.Button("Randomize Values"))
                    {
                        autoSortActive = false;
                        randomizeElementArray();
                        resetExecutionInformation();
                        resetTimeInfo();
                    }
                    if (ImGui.Button("Execute Sort Step"))
                    {
                        autoSortActive = false;
                        settings.ExecuteStep = true;
                    }
                    if (ImGui.Button("Auto Sort"))
                    {
                        autoSortActive = !autoSortActive;
                    }
                    ImGui.Text($"Step: {settings.CurrentStep}");
                    ImGui.Text($"Total Swaps: {settings.TotalSwaps}");
                    ImGui.Text($"Step Time: {settings.StepTimeString}");
                    ImGui.Text($"Sort Time: {settings.SortTimeString}");
                    ImGui.Text($"Avg Sort Time: {settings.AverageSortTimeString}");

                    ImGui.End();
                    guiContext.EndFrame();

                    // Auto Sort Interval
                    if (autoSortActive)
                    {
                        if ((DateTime.Now - lastAutoSortTime).TotalMilliseconds > settings.AutoSortSpeed)
                        {
                            if (settings.NextStep == StepEnum.NONE)
                            {
                                autoSortActive = false;
                            }
                            else
                            {
                                settings.ExecuteStep = true;
                                setSwappedCell();
                                lastAutoSortTime = DateTime.Now;
                            }
                        }
                    }

                    // Frame Logic
                    queue.WriteBuffer(elementsInputBuffer, 0, elements);

                    var dims = new float[] { settings.GridWidth, settings.GridHeight };
                    queue.WriteBuffer(computeUniformsBuffer, 0, dims);

                    var stepDetails = new uint[] { (uint)settings.NextStep, (uint)settings.NextSwapSpan };
                    queue.WriteBuffer(computeUniformsBuffer, 8, stepDetails);

                    var textureView = surface.GetCurrentTexture().Texture!.CreateView();
                    colorAttachments[0].View = textureView;

                    var commandEncoder = device.CreateCommandEncoder();
                    bitonicDisplayRenderer.StartRun(commandEncoder, new BitonicDisplayRenderArgs
                    {
                        Highlight = settings.DisplayMode == DisplayType.Elements ? 0u : 1u
                    });

                    if (settings.ExecuteStep && highestBlockHeight < settings.TotalElements * 2)
                    {
                        ComputePassEncoder computePassEncoder;
                        if (hasTimestampQuery && querySet != null)
                        {
                            computePassEncoder = commandEncoder.BeginComputePass(new ComputePassDescriptor
                            {
                                TimestampWrites = new PassTimestampWrites
                                {
                                    QuerySet = querySet!,
                                    BeginningOfPassWriteIndex = 0,
                                    EndOfPassWriteIndex = 1
                                }
                            });
                        }
                        else
                        {
                            computePassEncoder = commandEncoder.BeginComputePass();
                        }

                        computePassEncoder.SetPipeline(computePipeline);
                        computePassEncoder.SetBindGroup(0, computeBGCluster.BindGroups[0]);
                        computePassEncoder.DispatchWorkgroups((uint)settings.WorkgroupsPerStep);
                        computePassEncoder.End();

                        if (hasTimestampQuery && querySet != null && timestampQueryResolveBuffer != null && timestampQueryResultBuffer != null)
                        {
                            commandEncoder.ResolveQuerySet(querySet, 0, 2, timestampQueryResolveBuffer, 0);
                            commandEncoder.CopyBufferToBuffer(timestampQueryResolveBuffer, 0, timestampQueryResultBuffer, 0, 16);
                        }

                        settings.StepIndex++;
                        settings.CurrentStep = $"{settings.StepIndex} of {settings.TotalSteps}";
                        settings.PrevStep = settings.NextStep;
                        settings.PrevSwapSpan = settings.NextSwapSpan;
                        
                        if (settings.NextSwapSpan == 1)
                        {
                            highestBlockHeight *= 2;
                            if (highestBlockHeight == settings.TotalElements * 2)
                            {
                                settings.NextStep = StepEnum.NONE;
                                if (settings.ConfigToCompleteSwapsMap.ContainsKey(settings.ConfigKey))
                                {
                                    settings.ConfigToCompleteSwapsMap[settings.ConfigKey].Sorts++;
                                }
                            }
                            else if (highestBlockHeight > settings.WorkgroupSize * 2)
                            {
                                settings.NextStep = StepEnum.FLIP_GLOBAL;
                                settings.NextSwapSpan = highestBlockHeight;
                            }
                            else
                            {
                                settings.NextStep = StepEnum.FLIP_LOCAL;
                                settings.NextSwapSpan = highestBlockHeight;
                            }
                        }
                        else
                        {
                            settings.NextSwapSpan /= 2;
                            if (settings.NextSwapSpan > settings.WorkgroupSize * 2)
                            {
                                settings.NextStep = StepEnum.DISPERSE_GLOBAL;
                            }
                            else
                            {
                                settings.NextStep = StepEnum.DISPERSE_LOCAL;
                            }
                        }

                        commandEncoder.CopyBufferToBuffer(elementsOutputBuffer, 0, elementsStagingBuffer, 0, elementsBufferSize);
                        commandEncoder.CopyBufferToBuffer(atomicSwapsOutputBuffer, 0, atomicSwapsStagingBuffer, 0, 4);
                    }

                    var guiCommandBuffer = guiContext.Render(surface);
                    if (guiCommandBuffer.HasValue)
                    {
                        queue.Submit(new[] { commandEncoder.Finish(), guiCommandBuffer.Value });
                    }
                    else
                    {
                        queue.Submit(new[] { commandEncoder.Finish() });
                    }
                    surface.Present();

                    if (settings.ExecuteStep && highestBlockHeight < settings.TotalElements * 4)
                    {
                        var task = Task.Run(async () => 
                        {
                            await elementsStagingBuffer.MapAsync(MapMode.Read, 0, (nuint)elementsBufferSize);
                            elementsStagingBuffer.GetConstMappedRange<uint>(0, (nuint)elementsBufferSize, span => 
                            {
                                span.CopyTo(elements);
                            });
                            elementsStagingBuffer.Unmap();

                            await atomicSwapsStagingBuffer.MapAsync(MapMode.Read, 0, 4);
                            atomicSwapsStagingBuffer.GetConstMappedRange<uint>(0, 4, span => 
                            {
                                settings.TotalSwaps += (int)span[0];
                            });
                            atomicSwapsStagingBuffer.Unmap();

                            setSwappedCell();

                            if (hasTimestampQuery && timestampQueryResultBuffer != null)
                            {
                                await timestampQueryResultBuffer.MapAsync(MapMode.Read, 0, 16);
                                timestampQueryResultBuffer.GetConstMappedRange<ulong>(0, 16, span => 
                                {
                                    var newStepTime = (span[1] - span[0]) / 1000000.0;
                                    settings.StepTime = newStepTime;
                                    settings.SortTime += newStepTime;
                                    settings.StepTimeString = $"{newStepTime:F2}ms";
                                    settings.SortTimeString = $"{settings.SortTime:F2}ms";
                                });
                                timestampQueryResultBuffer.Unmap();
                            }
                        });
                        
                        task.Wait(); 
                    }

                    settings.ExecuteStep = false;
                };

                // return Task.CompletedTask;
            });
        }
    }
}
