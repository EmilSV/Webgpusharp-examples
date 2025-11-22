using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using BitonicSort;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using Buffer = WebGpuSharp.Buffer;

const int WindowWidth = 1200;
const int WindowHeight = 900;

var assembly = Assembly.GetExecutingAssembly();
var atomicResetWGSL = ResourceUtils.GetEmbeddedResource("BitonicSort.shaders.atomicToZero.wgsl", assembly);

return Run("Bitonic Sort", WindowWidth, WindowHeight, async runContext =>
{
    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();

    var adapter = await instance.RequestAdapterAsync(new()
    {
        FeatureLevel = FeatureLevel.Compatibility,
        CompatibleSurface = surface
    });

    var hasLimit = adapter.GetLimits(out var limits);
    Debug.Assert(hasLimit, "Failed to get adapter limits");

    bool timestampQueryAvailable = adapter.HasFeature(FeatureName.TimestampQuery);
    uint maxInvocationsX = limits.MaxComputeWorkgroupSizeX;
    uint maxComputeInvocationsPerWorkgroup = limits.MaxComputeInvocationsPerWorkgroup;
    uint requestedWorkgroupLimit = Math.Min(Math.Min(maxInvocationsX, maxComputeInvocationsPerWorkgroup), 256u);

    Span<FeatureName> requiredFeatures = timestampQueryAvailable
        ? [FeatureName.TimestampQuery]
        : [];

    var device = await adapter.RequestDeviceAsync(new()
    {
        RequiredFeatures = requiredFeatures,
        RequiredLimits = new()
        {
            MaxComputeWorkgroupSizeX = requestedWorkgroupLimit,
            MaxComputeInvocationsPerWorkgroup = requestedWorkgroupLimit,
        },
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
    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

    guiContext.SetupIMGUI(device, surfaceFormat);
    surface.Configure(new()
    {
        Width = WindowWidth,
        Height = WindowHeight,
        Usage = TextureUsage.RenderAttachment,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Auto,
    });

    QuerySet? querySet = null;
    Buffer? timestampQueryResolveBuffer = null;
    Buffer? timestampQueryResultBuffer = null;
    if (timestampQueryAvailable)
    {
        querySet = device.CreateQuerySet(new()
        {
            Type = QueryType.Timestamp,
            Count = 2,
        });

        timestampQueryResolveBuffer = device.CreateBuffer(new()
        {
            Size = sizeof(ulong) * 2,
            Usage = BufferUsage.QueryResolve | BufferUsage.CopySrc,
        });

        timestampQueryResultBuffer = device.CreateBuffer(new()
        {
            Size = sizeof(ulong) * 2,
            Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
        });
    }

    uint maxWorkgroupSize = requestedWorkgroupLimit;
    uint maxElements = maxWorkgroupSize * 32;

    List<uint> totalElementOptions = new();
    for (uint value = maxElements; value >= 4; value /= 2)
    {
        totalElementOptions.Add(value);
    }
    var totalElementLabels = totalElementOptions.ConvertAll(static v => v.ToString()).ToArray();

    List<uint> sizeLimitOptions = new();
    for (uint value = maxWorkgroupSize; value >= 2; value /= 2)
    {
        sizeLimitOptions.Add(value);
    }
    var sizeLimitLabels = sizeLimitOptions.ConvertAll(static v => v.ToString()).ToArray();



    var defaultGridWidth = (uint)(Math.Sqrt(maxElements) % 2 == 0
        ? Math.Floor(Math.Sqrt(maxElements))
        : Math.Floor(Math.Sqrt(maxElements / 2)));
    var defaultGridHeight = maxElements / defaultGridWidth;

    var settings = new BitonicSettings(maxElements, defaultGridWidth, defaultGridHeight, maxWorkgroupSize);
    var configStats = new Dictionary<(uint TotalElements, uint SizeLimit), ConfigStats>();

    uint highestBlockHeight = 2;
    bool autoSortEnabled = true;
    double autoSortAccumulatorMs = 0;
    bool manualStepRequested = false;
    bool sortCompleted = false;
    bool averageUpdatePending = false;
    bool logCopyRequested = false;
    bool logCopyInFlight = false;
    bool logReadbackPending = false;
    bool swapReadbackPending = false;
    int totalElementsIndex = 0;
    int sizeLimitIndex = 0;

    // Initialize initial elements array
    var elements = Enumerable.Range(0, (int)settings.TotalElements).Select(i => (uint)i).ToArray();

    var elementBufferSize = (ulong)(totalElementOptions[0] * sizeof(float));
    var elementsInputBuffer = device.CreateBuffer(new()
    {
        Size = elementBufferSize,
        Usage = BufferUsage.Storage | BufferUsage.CopyDst,
    });
    var elementsOutputBuffer = device.CreateBuffer(new()
    {
        Size = elementBufferSize,
        Usage = BufferUsage.Storage | BufferUsage.CopySrc,
    });
    var elementsStagingBuffer = device.CreateBuffer(new()
    {
        Size = elementBufferSize,
        Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
    });

    var atomicSwapsOutputBuffer = device.CreateBuffer(new()
    {
        Size = sizeof(uint),
        Usage = BufferUsage.Storage | BufferUsage.CopySrc,
    });

    var atomicSwapsStagingBuffer = device.CreateBuffer(new()
    {
        Size = sizeof(uint),
        Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
    });

    var computeUniformsBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<ComputeUniforms>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var elementsReadbackBuffer = device.CreateBuffer(new()
    {
        Size = elementBufferSize,
        Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
    });

    var swapCounterBuffer = device.CreateBuffer(new()
    {
        Size = sizeof(uint),
        Usage = BufferUsage.Storage | BufferUsage.CopySrc,
    });
    var swapReadbackBuffer = device.CreateBuffer(new()
    {
        Size = sizeof(uint),
        Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
    });

    var computeBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Compute | ShaderStage.Fragment,
                Buffer = new() { Type = BufferBindingType.ReadOnlyStorage },
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Compute,
                Buffer = new() { Type = BufferBindingType.Storage },
            },
            new()
            {
                Binding = 2,
                Visibility = ShaderStage.Compute | ShaderStage.Fragment,
                Buffer = new() { Type = BufferBindingType.Uniform },
            },
            new()
            {
                Binding = 3,
                Visibility = ShaderStage.Compute,
                Buffer = new() { Type = BufferBindingType.Storage },
            },
        ],
    });

    var computeBindGroup = device.CreateBindGroup(new()
    {
        Layout = computeBindGroupLayout,
        Entries =
        [
            new() { Binding = 0, Buffer = elementsInputBuffer },
            new() { Binding = 1, Buffer = elementsOutputBuffer },
            new() { Binding = 2, Buffer = computeUniformsBuffer },
            new() { Binding = 3, Buffer = swapCounterBuffer },
        ],
    });

    var computePipelineLayout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = new[] { computeBindGroupLayout },
    });

    ComputePipeline? computePipeline = device.CreateComputePipeline(new()
    {
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts = new[] { computeBindGroupLayout },
        }),
        Compute = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = BitonicCompute.NaiveBitonicCompute(settings.WorkgroupSize)
            })
        },
    });

    ComputePipeline CreateBitonicPipeline()
    {
        return device.CreateComputePipeline(new()
        {
            Layout = computePipelineLayout,
            Compute = new ComputeState
            {
                Module = device.CreateShaderModuleWGSL(new() { Code = BitonicCompute.NaiveBitonicCompute(settings.WorkgroupSize) })
            },
        });
    }

    var atomicResetPipeline = device.CreateComputePipeline(new()
    {
        Layout = computePipelineLayout,
        Compute = new ComputeState
        {
            Module = device.CreateShaderModuleWGSL(new() { Code = atomicResetWGSL }),
        },
    });

    var displayRenderer = new BitonicDisplayRenderer(
        device: device,
        presentationFormat: surfaceFormat,
        computeLayout: computeBindGroupLayout
    );

    bool timestampSupported = false;
    var timestampRecorder = new TimestampRecorder(device, stepDurationMs =>
    {
        settings.StepTimeMs = stepDurationMs;
        settings.SortTimeMs += stepDurationMs;
        if (averageUpdatePending && sortCompleted)
        {
            RegisterSortCompletion();
            averageUpdatePending = false;
        }
    });
    timestampSupported = timestampRecorder.Supported;

    void EnsureConfigEntry()
    {
        if (!configStats.ContainsKey(settings.CurrentConfigKey))
        {
            configStats[settings.CurrentConfigKey] = new ConfigStats();
        }
        var stats = configStats[settings.CurrentConfigKey];
        settings.AverageSortTimeMs = stats.Sorts == 0 ? 0 : stats.TotalTimeMs / stats.Sorts;
    }

    void ResetSwapCounter()
    {
        var encoder = device.CreateCommandEncoder();
        var pass = encoder.BeginComputePass();
        pass.SetPipeline(atomicResetPipeline);
        pass.SetBindGroup(0, computeBindGroup);
        pass.DispatchWorkgroups(1);
        pass.End();
        queue.Submit(encoder.Finish());
        settings.TotalSwaps = 0;
    }

    void RandomizeElements()
    {
        int length = (int)settings.TotalElements;
        for (int i = 0; i < length; i++)
        {
            elements[i] = (uint)i;
        }
        for (int i = length - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (elements[i], elements[j]) = (elements[j], elements[i]);
        }
        queue.WriteBuffer(elementsInputBuffer, 0, elements.AsSpan(0, length));
    }

    void UpdateSwappedCell()
    {
        if (settings.TotalElements == 0)
        {
            settings.SwappedCell = 0;
            return;
        }

        var hovered = Math.Clamp(settings.HoveredCell, 0, (int)settings.TotalElements - 1);
        settings.HoveredCell = hovered;
        var span = settings.NextSwapSpan;
        var swapped = hovered;

        if (span > 0)
        {
            switch (settings.NextStep)
            {
                case StepType.FlipLocal:
                case StepType.FlipGlobal:
                    {
                        var blockHeight = (int)span;
                        var blockIndex = hovered / blockHeight + 1;
                        var offset = hovered % blockHeight;
                        swapped = blockHeight * blockIndex - offset - 1;
                        break;
                    }
                case StepType.DisperseLocal:
                case StepType.DisperseGlobal:
                    {
                        var half = (int)(span / 2);
                        if (half > 0)
                        {
                            swapped = hovered % (int)span < half ? hovered + half : hovered - half;
                        }
                        break;
                    }
            }
        }

        swapped = Math.Clamp(swapped, 0, (int)settings.TotalElements - 1);
        settings.SwappedCell = swapped;
    }

    void ResetExecutionState(bool randomize)
    {
        settings.WorkgroupSize = BitonicMath.ComputeWorkgroupSize(settings.TotalElements, settings.SizeLimit);
        settings.WorkgroupsPerStep = BitonicMath.ComputeWorkgroupsPerStep(settings.TotalElements, settings.WorkgroupSize);
        settings.TotalSteps = BitonicMath.GetNumSteps(settings.TotalElements);
        settings.StepIndex = 0;
        settings.PrevStep = StepType.None;
        settings.NextStep = StepType.FlipLocal;
        settings.PrevSwapSpan = 0;
        settings.NextSwapSpan = 2;
        settings.TotalSwaps = 0;
        settings.SortTimeMs = 0;
        settings.StepTimeMs = 0;
        autoSortAccumulatorMs = 0;
        sortCompleted = false;
        averageUpdatePending = false;
        highestBlockHeight = 2;
        var dims = BitonicMath.GetGridDimensions(settings.TotalElements);
        settings.GridWidth = dims.Width;
        settings.GridHeight = dims.Height;
        EnsureConfigEntry();
        UpdateSwappedCell();
        ResetSwapCounter();
        if (randomize)
        {
            RandomizeElements();
        }
        computePipeline = CreateBitonicPipeline();
    }

    void RegisterSortCompletion()
    {
        if (!timestampSupported)
        {
            return;
        }

        var stats = configStats[settings.CurrentConfigKey];
        stats.Sorts++;
        stats.TotalTimeMs += settings.SortTimeMs;
        configStats[settings.CurrentConfigKey] = stats;
        settings.AverageSortTimeMs = stats.TotalTimeMs / stats.Sorts;
    }

    bool EvaluateStepRequest(double deltaMs)
    {
        if (settings.NextStep == StepType.None)
        {
            autoSortEnabled = false;
            manualStepRequested = false;
            return false;
        }

        if (manualStepRequested)
        {
            manualStepRequested = false;
            UpdateSwappedCell();
            return true;
        }

        if (!autoSortEnabled)
        {
            return false;
        }

        autoSortAccumulatorMs += deltaMs;
        if (autoSortAccumulatorMs >= settings.AutoSortSpeedMs)
        {
            autoSortAccumulatorMs = 0;
            UpdateSwappedCell();
            return true;
        }

        return false;
    }

    void AdvanceSortState()
    {
        settings.StepIndex++;
        settings.PrevStep = settings.NextStep;
        settings.PrevSwapSpan = settings.NextSwapSpan;

        if (settings.NextSwapSpan > 1)
        {
            settings.NextSwapSpan /= 2;
            settings.NextStep = settings.NextSwapSpan > settings.WorkgroupSize * 2
                ? StepType.DisperseGlobal
                : StepType.DisperseLocal;
        }
        else
        {
            highestBlockHeight *= 2;
            if (highestBlockHeight >= settings.TotalElements * 2)
            {
                settings.NextStep = StepType.None;
                settings.NextSwapSpan = 0;
                sortCompleted = true;
                averageUpdatePending = timestampSupported;
                autoSortEnabled = false;
            }
            else
            {
                settings.NextSwapSpan = highestBlockHeight;
                settings.NextStep = highestBlockHeight > settings.WorkgroupSize * 2
                    ? StepType.FlipGlobal
                    : StepType.FlipLocal;
            }
        }

        UpdateSwappedCell();
    }

    void TryDownloadSwapCount()
    {
        if (!swapReadbackPending)
        {
            return;
        }
        if (swapReadbackBuffer.GetMapState() != BufferMapState.Unmapped)
        {
            return;
        }

        swapReadbackPending = false;
        _ = swapReadbackBuffer.MapAsync(MapMode.Read).ContinueWith(task =>
        {
            if (task.Result == MapAsyncStatus.Success)
            {
                swapReadbackBuffer.GetConstMappedRange<uint>(0, 1, span => settings.TotalSwaps = span[0]);
            }
            swapReadbackBuffer.Unmap();
        });
    }

    void TryDownloadElementsLog()
    {
        if (!logReadbackPending)
        {
            return;
        }
        if (elementsReadbackBuffer.GetMapState() != BufferMapState.Unmapped)
        {
            return;
        }

        logReadbackPending = false;
        logCopyInFlight = false;
        _ = elementsReadbackBuffer.MapAsync(MapMode.Read).ContinueWith(task =>
        {
            if (task.Result == MapAsyncStatus.Success)
            {
                elementsReadbackBuffer.GetConstMappedRange<uint>(0, (nuint)settings.TotalElements, span =>
                {
                    Console.WriteLine($"[{string.Join(", ", span.ToArray())}]");
                });
            }
            elementsReadbackBuffer.Unmap();
        });
    }

    CommandBuffer DrawGui()
    {
        guiContext.NewFrame();
        ImGui.SetNextWindowBgAlpha(0.8f);
        ImGui.SetNextWindowPos(new(10, 10), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new(360, 640), ImGuiCond.FirstUseEver);
        ImGui.Begin("Bitonic Sort", ImGuiWindowFlags.NoCollapse);

        ImGui.Text("Compute Resources");
        ImGui.Separator();
        int currentTotalIndex = totalElementsIndex;
        if (ImGui.Combo("Total Elements", ref currentTotalIndex, totalElementLabels, totalElementLabels.Length))
        {
            totalElementsIndex = currentTotalIndex;
            settings.TotalElements = totalElementOptions[currentTotalIndex];
            ResetExecutionState(true);
        }

        bool disableSizeLimit = settings.StepIndex > 0 && settings.NextStep != StepType.None;
        if (disableSizeLimit)
        {
            ImGui.BeginDisabled();
        }
        int currentSizeLimitIndex = sizeLimitIndex;
        if (ImGui.Combo("Size Limit", ref currentSizeLimitIndex, sizeLimitLabels, sizeLimitLabels.Length))
        {
            sizeLimitIndex = currentSizeLimitIndex;
            settings.SizeLimit = sizeLimitOptions[currentSizeLimitIndex];
            ResetExecutionState(false);
        }
        if (disableSizeLimit)
        {
            ImGui.EndDisabled();
        }

        ImGui.Text($"Grid: {settings.GridWidth} x {settings.GridHeight}");
        ImGui.Text($"Workgroup Size: {settings.WorkgroupSize}");
        ImGui.Text($"Workgroups / Step: {settings.WorkgroupsPerStep}");

        ImGui.Dummy(new(0, 10));
        ImGui.Text("Controls");
        ImGui.Separator();
        if (ImGui.Button("Execute Step"))
        {
            manualStepRequested = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Randomize"))
        {
            ResetExecutionState(true);
        }

        if (ImGui.Button(autoSortEnabled ? "Stop Auto Sort" : "Start Auto Sort"))
        {
            autoSortEnabled = !autoSortEnabled;
            autoSortAccumulatorMs = 0;
        }

        int autoSpeed = settings.AutoSortSpeedMs;
        if (ImGui.SliderInt("Auto Sort Speed (ms)", ref autoSpeed, 50, 1000))
        {
            settings.AutoSortSpeedMs = autoSpeed;
        }

        if (ImGui.Button("Log Elements"))
        {
            logCopyRequested = true;
        }

        int displayMode = (int)settings.DisplayMode;
        if (ImGui.Combo("Display Mode", ref displayMode, new[] { "Elements", "Swap Highlight" }, 2))
        {
            settings.DisplayMode = (DisplayMode)displayMode;
        }

        ImGui.Dummy(new(0, 10));
        ImGui.Text("Execution");
        ImGui.Separator();
        ImGui.Text($"Step: {settings.StepIndex} / {settings.TotalSteps}");
        ImGui.Text($"Prev Step: {settings.PrevStep}");
        ImGui.Text($"Next Step: {settings.NextStep}");
        ImGui.Text($"Prev Span: {settings.PrevSwapSpan}");
        ImGui.Text($"Next Span: {settings.NextSwapSpan}");
        ImGui.Text($"Hovered Cell: {settings.HoveredCell}");
        ImGui.Text($"Swapped Cell: {settings.SwappedCell}");
        ImGui.Text($"Total Swaps: {settings.TotalSwaps}");
        ImGui.Text(timestampSupported ? $"Step Time: {settings.StepTimeMs:0.#####} ms" : "Step Time: N/A");
        ImGui.Text(timestampSupported ? $"Sort Time: {settings.SortTimeMs:0.#####} ms" : "Sort Time: N/A");
        ImGui.Text(timestampSupported ? $"Avg Sort Time: {settings.AverageSortTimeMs:0.#####} ms" : "Avg Sort Time: N/A");

        ImGui.End();
        guiContext.EndFrame();
        return guiContext.Render(surface)!.Value!;
    }

    var frameClock = Stopwatch.StartNew();
    long previousTicks = frameClock.ElapsedTicks;

    void Frame()
    {
        var nowTicks = frameClock.ElapsedTicks;
        var deltaMs = (nowTicks - previousTicks) * 1000.0 / Stopwatch.Frequency;
        previousTicks = nowTicks;

        var shouldDispatch = EvaluateStepRequest(deltaMs);

        queue.WriteBuffer(computeUniformsBuffer, new ComputeUniforms
        {
            Width = settings.GridWidth,
            Height = settings.GridHeight,
            Algo = (uint)settings.NextStep,
            BlockHeight = settings.NextSwapSpan,
        });

        var currentTexture = surface.GetCurrentTexture();
        if (currentTexture.Texture is null)
        {
            return;
        }

        var targetView = currentTexture.Texture.CreateView();
        var commandEncoder = device.CreateCommandEncoder();

        displayRenderer.Render(commandEncoder, targetView, computeBindGroup, settings.DisplayMode);

        if (shouldDispatch)
        {
            var computePass = timestampRecorder.BeginPass(commandEncoder);
            computePass.SetPipeline(computePipeline!);
            computePass.SetBindGroup(0, computeBindGroup);
            computePass.DispatchWorkgroups(settings.WorkgroupsPerStep);
            computePass.End();
            timestampRecorder.Resolve(commandEncoder);
            commandEncoder.CopyBufferToBuffer(elementsOutputBuffer, 0, elementsInputBuffer, 0, elementBufferSize);
            if (swapReadbackBuffer.GetMapState() == BufferMapState.Unmapped)
            {
                commandEncoder.CopyBufferToBuffer(swapCounterBuffer, 0, swapReadbackBuffer, 0, sizeof(uint));
                swapReadbackPending = true;
            }
            AdvanceSortState();
        }

        if (logCopyRequested && !logCopyInFlight)
        {
            if (elementsReadbackBuffer.GetMapState() == BufferMapState.Unmapped)
            {
                logCopyRequested = false;
                logCopyInFlight = true;
                logReadbackPending = true;
                var activeBytes = (ulong)(settings.TotalElements * sizeof(uint));
                commandEncoder.CopyBufferToBuffer(elementsInputBuffer, 0, elementsReadbackBuffer, 0, activeBytes);
            }
        }

        var commandBuffer = commandEncoder.Finish();
        var guiCommandBuffer = DrawGui();
        queue.Submit(new[] { commandBuffer, guiCommandBuffer });
        surface.Present();

        if (shouldDispatch)
        {
            timestampRecorder.TryDownload();
        }

        TryDownloadSwapCount();
        TryDownloadElementsLog();
    }

    ResetExecutionState(true);
    runContext.Input.OnMouseMotion += motion =>
    {
        if (ImGui.GetIO().WantCaptureMouse)
        {
            return;
        }

        var cellWidth = settings.GridWidth > 0 ? WindowWidth / (double)settings.GridWidth : 1;
        var cellHeight = settings.GridHeight > 0 ? WindowHeight / (double)settings.GridHeight : 1;
        var xIndex = Math.Clamp((int)(motion.x / cellWidth), 0, (int)settings.GridWidth - 1);
        var yIndex = Math.Clamp((int)(motion.y / cellHeight), 0, (int)settings.GridHeight - 1);
        settings.HoveredCell = (int)(settings.GridWidth * (settings.GridHeight - 1 - yIndex) + xIndex);
        UpdateSwappedCell();
    };

    runContext.OnFrame += Frame;
});











// import { GUI } from 'dat.gui';
// import { createBindGroupCluster, SampleInitFactoryWebGPU } from './utils';
// import BitonicDisplayRenderer from './bitonicDisplay';
// import { NaiveBitonicCompute } from './bitonicCompute';
// import atomicToZero from './atomicToZero.wgsl';

// // Type of step that will be executed in our shader
// enum StepEnum {
//   NONE,
//   FLIP_LOCAL,
//   DISPERSE_LOCAL,
//   FLIP_GLOBAL,
//   DISPERSE_GLOBAL,
// }

// type StepType =
//   // NONE: No sort step has or will occur
//   | 'NONE'
//   // FLIP_LOCAL: A sort step that performs a flip operation over indices in a workgroup's locally addressable area
//   // (i.e invocations * workgroup_index -> invocations * (workgroup_index + 1) - 1.
//   | 'FLIP_LOCAL'
//   // DISPERSE_LOCAL A sort step that performs a flip operation over indices in a workgroup's locally addressable area.
//   | 'DISPERSE_LOCAL'
//   // FLIP_GLOBAL A sort step that performs a flip step across a range of indices outside a workgroup's locally addressable area.
//   | 'FLIP_GLOBAL'
//   // DISPERSE_GLOBAL A sort step that performs a disperse operation across a range of indices outside a workgroup's locally addressable area.
//   | 'DISPERSE_GLOBAL';

// type DisplayType = 'Elements' | 'Swap Highlight';

// interface ConfigInfo {
//   // Number of sorts executed under a given elements + size limit config
//   sorts: number;
//   // Total collective time taken to execute each complete sort under this config
//   time: number;
// }

// interface StringKeyToNumber {
//   [key: string]: ConfigInfo;
// }

// // Gui settings object
// interface SettingsInterface {
//   'Total Elements': number;
//   'Grid Width': number;
//   'Grid Height': number;
//   'Grid Dimensions': string;
//   'Workgroup Size': number;
//   'Size Limit': number;
//   'Workgroups Per Step': number;
//   'Hovered Cell': number;
//   'Swapped Cell': number;
//   'Current Step': string;
//   'Step Index': number;
//   'Total Steps': number;
//   'Prev Step': StepType;
//   'Next Step': StepType;
//   'Prev Swap Span': number;
//   'Next Swap Span': number;
//   executeStep: boolean;
//   'Randomize Values': () => void;
//   'Execute Sort Step': () => void;
//   'Log Elements': () => void;
//   'Auto Sort': () => void;
//   'Auto Sort Speed': number;
//   'Display Mode': DisplayType;
//   'Total Swaps': number;
//   stepTime: number;
//   'Step Time': string;
//   sortTime: number;
//   'Sort Time': string;
//   'Average Sort Time': string;
//   configToCompleteSwapsMap: StringKeyToNumber;
//   configKey: string;
// }

// const getNumSteps = (numElements: number) => {
//   const n = Math.log2(numElements);
//   return (n * (n + 1)) / 2;
// };

// SampleInitFactoryWebGPU(
//   async ({
//     device,
//     gui,
//     presentationFormat,
//     context,
//     canvas,
//     timestampQueryAvailable,
//   }) => {
//     const maxInvocationsX = device.limits.maxComputeWorkgroupSizeX;

//     let querySet: GPUQuerySet;
//     let timestampQueryResolveBuffer: GPUBuffer;
//     let timestampQueryResultBuffer: GPUBuffer;
//     if (timestampQueryAvailable) {
//       querySet = device.createQuerySet({ type: 'timestamp', count: 2 });
//       timestampQueryResolveBuffer = device.createBuffer({
//         // 2 timestamps * BigInt size for nanoseconds
//         size: 2 * BigInt64Array.BYTES_PER_ELEMENT,
//         usage: GPUBufferUsage.QUERY_RESOLVE | GPUBufferUsage.COPY_SRC,
//       });
//       timestampQueryResultBuffer = device.createBuffer({
//         // 2 timestamps * BigInt size for nanoseconds
//         size: 2 * BigInt64Array.BYTES_PER_ELEMENT,
//         usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
//       });
//     }

//     const totalElementOptions = [];
//     const maxElements = maxInvocationsX * 32;
//     for (let i = maxElements; i >= 4; i /= 2) {
//       totalElementOptions.push(i);
//     }

//     const sizeLimitOptions: number[] = [];
//     for (let i = maxInvocationsX; i >= 2; i /= 2) {
//       sizeLimitOptions.push(i);
//     }

//     const defaultGridWidth =
//       Math.sqrt(maxElements) % 2 === 0
//         ? Math.floor(Math.sqrt(maxElements))
//         : Math.floor(Math.sqrt(maxElements / 2));

//     const defaultGridHeight = maxElements / defaultGridWidth;

//     const settings: SettingsInterface = {
//       // TOTAL ELEMENT AND GRID SETTINGS
//       // The number of elements to be sorted. Must equal gridWidth * gridHeight || Workgroup Size * Workgroups * 2.
//       // When changed, all relevant values within the settings object are reset to their defaults at the beginning of a sort with n elements.
//       'Total Elements': maxElements,
//       // The width of the screen in cells.
//       'Grid Width': defaultGridWidth,
//       // The height of the screen in cells.
//       'Grid Height': defaultGridHeight,
//       // Grid Dimensions as string
//       'Grid Dimensions': `${defaultGridWidth}x${defaultGridHeight}`,

//       // INVOCATION, WORKGROUP SIZE, AND WORKGROUP DISPATCH SETTINGS
//       // The size of a workgroup, or the number of invocations executed within each workgroup
//       // Determined algorithmically based on 'Size Limit', maxInvocationsX, and the current number of elements to sort
//       'Workgroup Size': maxInvocationsX,
//       // An artifical constraint on the maximum workgroup size/maximumn invocations per workgroup as specified by device.limits.maxComputeWorkgroupSizeX
//       'Size Limit': maxInvocationsX,
//       // Total workgroups that are dispatched during each step of the bitonic sort
//       'Workgroups Per Step': maxElements / (maxInvocationsX * 2),

//       // HOVER SETTINGS
//       // The element/cell in the element visualizer directly beneath the mouse cursor
//       'Hovered Cell': 0,
//       // The element/cell in the element visualizer that the hovered cell will swap with in the next execution step of the bitonic sort.
//       'Swapped Cell': 1,

//       // STEP INDEX, STEP TYPE, AND STEP SWAP SPAN SETTINGS
//       // The index of the current step in the bitonic sort.
//       'Step Index': 0,
//       // The total number of steps required to sort the displayed elements.
//       'Total Steps': getNumSteps(maxElements),
//       // A string that condenses 'Step Index' and 'Total Steps' into a single GUI Controller display element.
//       'Current Step': `0 of 91`,
//       // The category of the previously executed step. Always begins the bitonic sort with a value of 'NONE' and ends with a value of 'DISPERSE_LOCAL'
//       'Prev Step': 'NONE',
//       // The category of the next step that will be executed. Always begins the bitonic sort with a value of 'FLIP_LOCAL' and ends with a value of 'NONE'
//       'Next Step': 'FLIP_LOCAL',
//       // The maximum span of a swap operation in the sort's previous step.
//       'Prev Swap Span': 0,
//       // The maximum span of a swap operation in the sort's upcoming step.
//       'Next Swap Span': 2,

//       // ANIMATION LOOP AND FUNCTION SETTINGS
//       // A flag that designates whether we will dispatch a workload this frame.
//       executeStep: false,
//       // A function that randomizes the values of each element.
//       // When called, all relevant values within the settings object are reset to their defaults at the beginning of a sort with n elements.
//       'Randomize Values': () => {
//         return;
//       },
//       // A function that manually executes a single step of the bitonic sort.
//       'Execute Sort Step': () => {
//         return;
//       },
//       // A function that logs the values of each element as an array to the browser's console.
//       'Log Elements': () => {
//         return;
//       },
//       // A function that automatically executes each step of the bitonic sort at an interval determined by 'Auto Sort Speed'
//       'Auto Sort': () => {
//         return;
//       },
//       // The speed at which each step of the bitonic sort will be executed after 'Auto Sort' has been called.
//       'Auto Sort Speed': 50,

//       // MISCELLANEOUS SETTINGS
//       'Display Mode': 'Elements',
//       // An atomic value representing the total number of swap operations executed over the course of the bitonic sort.
//       'Total Swaps': 0,

//       // TIMESTAMP SETTINGS
//       // NOTE: Timestep values below all are calculated in terms of milliseconds rather than the nanoseconds a timestamp query set usually outputs.
//       // Time taken to execute the previous step of the bitonic sort in milliseconds
//       'Step Time': '0ms',
//       stepTime: 0,
//       // Total taken to colletively execute each step of the complete bitonic sort, represented in milliseconds.
//       'Sort Time': '0ms',
//       sortTime: 0,
//       // Average time taken to complete a bitonic sort with the current combination of n 'Total Elements' and x 'Size Limit'
//       'Average Sort Time': '0ms',
//       // A string to number map that maps a string representation of the current 'Total Elements' + 'Size Limit' configuration to a number
//       // representing the total number of sorts that have been executed under that same configuration.
//       configToCompleteSwapsMap: {
//         '8192 256': {
//           sorts: 0,
//           time: 0,
//         },
//       },
//       // Current key into configToCompleteSwapsMap
//       configKey: '8192 256',
//     };

//     // Initialize initial elements array
//     let elements = new Uint32Array(
//       Array.from({ length: settings['Total Elements'] }, (_, i) => i)
//     );

//     // Initialize elementsBuffer and elementsStagingBuffer
//     const elementsBufferSize =
//       Float32Array.BYTES_PER_ELEMENT * totalElementOptions[0];
//     // Initialize input, output, staging buffers
//     const elementsInputBuffer = device.createBuffer({
//       size: elementsBufferSize,
//       usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
//     });
//     const elementsOutputBuffer = device.createBuffer({
//       size: elementsBufferSize,
//       usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC,
//     });
//     const elementsStagingBuffer = device.createBuffer({
//       size: elementsBufferSize,
//       usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
//     });

//     // Initialize atomic swap buffer on GPU and CPU. Counts number of swaps actually performed by
//     // compute shader (when value at index x is greater than value at index y)
//     const atomicSwapsOutputBuffer = device.createBuffer({
//       size: Uint32Array.BYTES_PER_ELEMENT,
//       usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC,
//     });
//     const atomicSwapsStagingBuffer = device.createBuffer({
//       size: Uint32Array.BYTES_PER_ELEMENT,
//       usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
//     });

//     // Create uniform buffer for compute shader
//     const computeUniformsBuffer = device.createBuffer({
//       // width, height, blockHeight, algo
//       size: Float32Array.BYTES_PER_ELEMENT * 4,
//       usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
//     });

//     const computeBGCluster = createBindGroupCluster(
//       [0, 1, 2, 3],
//       [
//         GPUShaderStage.COMPUTE | GPUShaderStage.FRAGMENT,
//         GPUShaderStage.COMPUTE,
//         GPUShaderStage.COMPUTE | GPUShaderStage.FRAGMENT,
//         GPUShaderStage.COMPUTE,
//       ],
//       ['buffer', 'buffer', 'buffer', 'buffer'],
//       [
//         { type: 'read-only-storage' },
//         { type: 'storage' },
//         { type: 'uniform' },
//         { type: 'storage' },
//       ],
//       [
//         [
//           { buffer: elementsInputBuffer },
//           { buffer: elementsOutputBuffer },
//           { buffer: computeUniformsBuffer },
//           { buffer: atomicSwapsOutputBuffer },
//         ],
//       ],
//       'BitonicSort',
//       device
//     );

//     let computePipeline = device.createComputePipeline({
//       layout: device.createPipelineLayout({
//         bindGroupLayouts: [computeBGCluster.bindGroupLayout],
//       }),
//       compute: {
//         module: device.createShaderModule({
//           code: NaiveBitonicCompute(settings['Workgroup Size']),
//         }),
//       },
//     });

//     // Simple pipeline that zeros out an atomic value at group 0 binding 3
//     const atomicToZeroComputePipeline = device.createComputePipeline({
//       layout: device.createPipelineLayout({
//         bindGroupLayouts: [computeBGCluster.bindGroupLayout],
//       }),
//       compute: {
//         module: device.createShaderModule({
//           code: atomicToZero,
//         }),
//       },
//     });

//     // Create bitonic debug renderer
//     const renderPassDescriptor: GPURenderPassDescriptor = {
//       colorAttachments: [
//         {
//           view: undefined, // Assigned later

//           clearValue: [0.1, 0.4, 0.5, 1.0],
//           loadOp: 'clear',
//           storeOp: 'store',
//         },
//       ],
//     };

//     const bitonicDisplayRenderer = new BitonicDisplayRenderer(
//       device,
//       presentationFormat,
//       renderPassDescriptor,
//       computeBGCluster,
//       'BitonicDisplay'
//     );

//     const resetTimeInfo = () => {
//       settings.stepTime = 0;
//       settings.sortTime = 0;
//       stepTimeController.setValue('0ms');
//       sortTimeController.setValue(`0ms`);
//       const nanCheck =
//         settings.configToCompleteSwapsMap[settings.configKey].time /
//         settings.configToCompleteSwapsMap[settings.configKey].sorts;
//       const ast = nanCheck ? nanCheck : 0;
//       averageSortTimeController.setValue(`${ast.toFixed(5)}ms`);
//     };

//     const resetExecutionInformation = () => {
//       // The workgroup size is either elements / 2 or Size Limit
//       workgroupSizeController.setValue(
//         Math.min(settings['Total Elements'] / 2, settings['Size Limit'])
//       );

//       // Dispatch a workgroup for every (Size Limit * 2) elements
//       const workgroupsPerStep =
//         (settings['Total Elements'] - 1) / (settings['Size Limit'] * 2);

//       workgroupsPerStepController.setValue(Math.ceil(workgroupsPerStep));

//       // Reset step Index and number of steps based on elements size
//       settings['Step Index'] = 0;
//       settings['Total Steps'] = getNumSteps(settings['Total Elements']);
//       currentStepController.setValue(
//         `${settings['Step Index']} of ${settings['Total Steps']}`
//       );

//       // Get new width and height of screen display in cells
//       const newCellWidth =
//         Math.sqrt(settings['Total Elements']) % 2 === 0
//           ? Math.floor(Math.sqrt(settings['Total Elements']))
//           : Math.floor(Math.sqrt(settings['Total Elements'] / 2));
//       const newCellHeight = settings['Total Elements'] / newCellWidth;
//       settings['Grid Width'] = newCellWidth;
//       settings['Grid Height'] = newCellHeight;
//       gridDimensionsController.setValue(`${newCellWidth}x${newCellHeight}`);

//       // Set prevStep to None (restart) and next step to FLIP
//       prevStepController.setValue('NONE');
//       nextStepController.setValue('FLIP_LOCAL');

//       // Reset block heights
//       prevBlockHeightController.setValue(0);
//       nextBlockHeightController.setValue(2);

//       // Reset Total Swaps by setting atomic value to 0
//       const commandEncoder = device.createCommandEncoder();
//       const computePassEncoder = commandEncoder.beginComputePass();
//       computePassEncoder.setPipeline(atomicToZeroComputePipeline);
//       computePassEncoder.setBindGroup(0, computeBGCluster.bindGroups[0]);
//       computePassEncoder.dispatchWorkgroups(1);
//       computePassEncoder.end();
//       device.queue.submit([commandEncoder.finish()]);
//       totalSwapsController.setValue(0);

//       highestBlockHeight = 2;
//     };

//     const randomizeElementArray = () => {
//       let currentIndex = elements.length;
//       // While there are elements to shuffle
//       while (currentIndex !== 0) {
//         // Pick a remaining element
//         const randomIndex = Math.floor(Math.random() * currentIndex);
//         currentIndex -= 1;
//         [elements[currentIndex], elements[randomIndex]] = [
//           elements[randomIndex],
//           elements[currentIndex],
//         ];
//       }
//     };

//     const resizeElementArray = () => {
//       // Recreate elements array with new length
//       elements = new Uint32Array(
//         Array.from({ length: settings['Total Elements'] }, (_, i) => i)
//       );

//       resetExecutionInformation();

//       // Create new shader invocation with workgroupSize that reflects number of invocations
//       computePipeline = device.createComputePipeline({
//         layout: device.createPipelineLayout({
//           bindGroupLayouts: [computeBGCluster.bindGroupLayout],
//         }),
//         compute: {
//           module: device.createShaderModule({
//             code: NaiveBitonicCompute(
//               Math.min(settings['Total Elements'] / 2, settings['Size Limit'])
//             ),
//           }),
//         },
//       });
//       // Randomize array elements
//       randomizeElementArray();
//       highestBlockHeight = 2;
//     };

//     randomizeElementArray();

//     const setSwappedCell = () => {
//       let swappedIndex: number;
//       switch (settings['Next Step']) {
//         case 'FLIP_LOCAL':
//         case 'FLIP_GLOBAL':
//           {
//             const blockHeight = settings['Next Swap Span'];
//             const p2 = Math.floor(settings['Hovered Cell'] / blockHeight) + 1;
//             const p3 = settings['Hovered Cell'] % blockHeight;
//             swappedIndex = blockHeight * p2 - p3 - 1;
//             swappedCellController.setValue(swappedIndex);
//           }
//           break;
//         case 'DISPERSE_LOCAL':
//           {
//             const blockHeight = settings['Next Swap Span'];
//             const halfHeight = blockHeight / 2;
//             swappedIndex =
//               settings['Hovered Cell'] % blockHeight < halfHeight
//                 ? settings['Hovered Cell'] + halfHeight
//                 : settings['Hovered Cell'] - halfHeight;
//             swappedCellController.setValue(swappedIndex);
//           }
//           break;
//         case 'NONE': {
//           swappedIndex = settings['Hovered Cell'];
//           swappedCellController.setValue(swappedIndex);
//         }
//         default:
//           {
//             swappedIndex = settings['Hovered Cell'];
//             swappedCellController.setValue(swappedIndex);
//           }
//           break;
//       }
//     };

//     let autoSortIntervalID: ReturnType<typeof setInterval> | null = null;
//     const endSortInterval = () => {
//       if (autoSortIntervalID !== null) {
//         clearInterval(autoSortIntervalID);
//         autoSortIntervalID = null;
//       }
//     };
//     const startSortInterval = () => {
//       const currentIntervalSpeed = settings['Auto Sort Speed'];
//       autoSortIntervalID = setInterval(() => {
//         if (settings['Next Step'] === 'NONE') {
//           clearInterval(autoSortIntervalID);
//           autoSortIntervalID = null;
//           sizeLimitController.domElement.style.pointerEvents = 'auto';
//         }
//         if (settings['Auto Sort Speed'] !== currentIntervalSpeed) {
//           clearInterval(autoSortIntervalID);
//           autoSortIntervalID = null;
//           startSortInterval();
//         }
//         settings.executeStep = true;
//         setSwappedCell();
//       }, settings['Auto Sort Speed']);
//     };

//     // At top level, information about resources used to execute the compute shader
//     // i.e elements sorted, invocations per workgroup, and workgroups dispatched
//     const computeResourcesFolder = gui.addFolder('Compute Resources');
//     computeResourcesFolder
//       .add(settings, 'Total Elements', totalElementOptions)
//       .onChange(() => {
//         endSortInterval();
//         resizeElementArray();
//         sizeLimitController.domElement.style.pointerEvents = 'auto';
//         // Create new config key for current element + size limit configuration
//         const currConfigKey = `${settings['Total Elements']} ${settings['Size Limit']}`;
//         // If configKey doesn't exist in the map, create it.
//         if (!settings.configToCompleteSwapsMap[currConfigKey]) {
//           settings.configToCompleteSwapsMap[currConfigKey] = {
//             sorts: 0,
//             time: 0,
//           };
//         }
//         settings.configKey = currConfigKey;
//         resetTimeInfo();
//       });
//     const sizeLimitController = computeResourcesFolder
//       .add(settings, 'Size Limit', sizeLimitOptions)
//       .onChange(() => {
//         // Change total workgroups per step and size of a workgroup based on arbitrary constraint
//         // imposed by size limit.
//         const constraint = Math.min(
//           settings['Total Elements'] / 2,
//           settings['Size Limit']
//         );
//         const workgroupsPerStep =
//           (settings['Total Elements'] - 1) / (settings['Size Limit'] * 2);
//         workgroupSizeController.setValue(constraint);
//         workgroupsPerStepController.setValue(Math.ceil(workgroupsPerStep));
//         // Apply new compute resources values to the sort's compute pipeline
//         computePipeline = computePipeline = device.createComputePipeline({
//           layout: device.createPipelineLayout({
//             bindGroupLayouts: [computeBGCluster.bindGroupLayout],
//           }),
//           compute: {
//             module: device.createShaderModule({
//               code: NaiveBitonicCompute(
//                 Math.min(settings['Total Elements'] / 2, settings['Size Limit'])
//               ),
//             }),
//           },
//         });
//         // Create new config key for current element + size limit configuration
//         const currConfigKey = `${settings['Total Elements']} ${settings['Size Limit']}`;
//         // If configKey doesn't exist in the map, create it.
//         if (!settings.configToCompleteSwapsMap[currConfigKey]) {
//           settings.configToCompleteSwapsMap[currConfigKey] = {
//             sorts: 0,
//             time: 0,
//           };
//         }
//         settings.configKey = currConfigKey;
//         resetTimeInfo();
//       });
//     const workgroupSizeController = computeResourcesFolder.add(
//       settings,
//       'Workgroup Size'
//     );
//     const workgroupsPerStepController = computeResourcesFolder.add(
//       settings,
//       'Workgroups Per Step'
//     );

//     computeResourcesFolder.open();

//     // Folder with functions that control the execution of the sort
//     const controlFolder = gui.addFolder('Sort Controls');
//     controlFolder.add(settings, 'Execute Sort Step').onChange(() => {
//       // Size Limit locked upon sort
//       sizeLimitController.domElement.style.pointerEvents = 'none';
//       endSortInterval();
//       settings.executeStep = true;
//     });
//     controlFolder.add(settings, 'Randomize Values').onChange(() => {
//       endSortInterval();
//       randomizeElementArray();
//       resetExecutionInformation();
//       resetTimeInfo();
//       // Unlock workgroup size limit controller since sort has stopped
//       sizeLimitController.domElement.style.pointerEvents = 'auto';
//     });
//     controlFolder
//       .add(settings, 'Log Elements')
//       .onChange(() => console.log(elements));
//     controlFolder.add(settings, 'Auto Sort').onChange(() => {
//       // Invocation Limit locked upon sort
//       sizeLimitController.domElement.style.pointerEvents = 'none';
//       startSortInterval();
//     });
//     controlFolder.add(settings, 'Auto Sort Speed', 50, 1000).step(50);
//     controlFolder.open();

//     // Information about grid display
//     const gridFolder = gui.addFolder('Grid Information');
//     gridFolder.add(settings, 'Display Mode', ['Elements', 'Swap Highlight']);
//     const gridDimensionsController = gridFolder.add(
//       settings,
//       'Grid Dimensions'
//     );
//     const hoveredCellController = gridFolder
//       .add(settings, 'Hovered Cell')
//       .onChange(setSwappedCell);
//     const swappedCellController = gridFolder.add(settings, 'Swapped Cell');

//     // Additional Information about the execution state of the sort
//     const executionInformationFolder = gui.addFolder('Execution Information');
//     const currentStepController = executionInformationFolder.add(
//       settings,
//       'Current Step'
//     );
//     const prevStepController = executionInformationFolder.add(
//       settings,
//       'Prev Step'
//     );
//     const nextStepController = executionInformationFolder.add(
//       settings,
//       'Next Step'
//     );
//     const totalSwapsController = executionInformationFolder.add(
//       settings,
//       'Total Swaps'
//     );
//     const prevBlockHeightController = executionInformationFolder.add(
//       settings,
//       'Prev Swap Span'
//     );
//     const nextBlockHeightController = executionInformationFolder.add(
//       settings,
//       'Next Swap Span'
//     );

//     // Timestamp information
//     const timestampFolder = gui.addFolder('Timestamp Info');
//     const stepTimeController = timestampFolder.add(settings, 'Step Time');
//     const sortTimeController = timestampFolder.add(settings, 'Sort Time');
//     const averageSortTimeController = timestampFolder.add(
//       settings,
//       'Average Sort Time'
//     );

//     // Adjust styles of Function List Elements within GUI
//     const liFunctionElements = document.getElementsByClassName('cr function');
//     for (let i = 0; i < liFunctionElements.length; i++) {
//       (liFunctionElements[i].children[0] as HTMLElement).style.display = 'flex';
//       (liFunctionElements[i].children[0] as HTMLElement).style.justifyContent =
//         'center';
//       (
//         liFunctionElements[i].children[0].children[1] as HTMLElement
//       ).style.position = 'absolute';
//     }

//     // Mouse listener that determines values of hoveredCell and swappedCell
//     canvas.addEventListener('mousemove', (event) => {
//       const currWidth = canvas.getBoundingClientRect().width;
//       const currHeight = canvas.getBoundingClientRect().height;
//       const cellSize: [number, number] = [
//         currWidth / settings['Grid Width'],
//         currHeight / settings['Grid Height'],
//       ];
//       const xIndex = Math.floor(event.offsetX / cellSize[0]);
//       const yIndex =
//         settings['Grid Height'] - 1 - Math.floor(event.offsetY / cellSize[1]);
//       hoveredCellController.setValue(yIndex * settings['Grid Width'] + xIndex);
//       settings['Hovered Cell'] = yIndex * settings['Grid Width'] + xIndex;
//     });

//     // Deactivate interaction with select GUI elements
//     sizeLimitController.domElement.style.pointerEvents = 'none';
//     workgroupsPerStepController.domElement.style.pointerEvents = 'none';
//     hoveredCellController.domElement.style.pointerEvents = 'none';
//     swappedCellController.domElement.style.pointerEvents = 'none';
//     currentStepController.domElement.style.pointerEvents = 'none';
//     prevStepController.domElement.style.pointerEvents = 'none';
//     prevBlockHeightController.domElement.style.pointerEvents = 'none';
//     nextStepController.domElement.style.pointerEvents = 'none';
//     nextBlockHeightController.domElement.style.pointerEvents = 'none';
//     workgroupSizeController.domElement.style.pointerEvents = 'none';
//     gridDimensionsController.domElement.style.pointerEvents = 'none';
//     totalSwapsController.domElement.style.pointerEvents = 'none';
//     stepTimeController.domElement.style.pointerEvents = 'none';
//     sortTimeController.domElement.style.pointerEvents = 'none';
//     averageSortTimeController.domElement.style.pointerEvents = 'none';
//     gui.width = 325;

//     let highestBlockHeight = 2;

//     startSortInterval();

//     async function frame() {
//       // Write elements buffer
//       device.queue.writeBuffer(
//         elementsInputBuffer,
//         0,
//         elements.buffer,
//         elements.byteOffset,
//         elements.byteLength
//       );

//       const dims = new Float32Array([
//         settings['Grid Width'],
//         settings['Grid Height'],
//       ]);
//       const stepDetails = new Uint32Array([
//         StepEnum[settings['Next Step']],
//         settings['Next Swap Span'],
//       ]);
//       device.queue.writeBuffer(
//         computeUniformsBuffer,
//         0,
//         dims.buffer,
//         dims.byteOffset,
//         dims.byteLength
//       );

//       device.queue.writeBuffer(computeUniformsBuffer, 8, stepDetails);

//       renderPassDescriptor.colorAttachments[0].view = context
//         .getCurrentTexture()
//         .createView();

//       const commandEncoder = device.createCommandEncoder();
//       bitonicDisplayRenderer.startRun(commandEncoder, {
//         highlight: settings['Display Mode'] === 'Elements' ? 0 : 1,
//       });
//       if (
//         settings.executeStep &&
//         highestBlockHeight < settings['Total Elements'] * 2
//       ) {
//         let computePassEncoder: GPUComputePassEncoder;
//         if (timestampQueryAvailable) {
//           computePassEncoder = commandEncoder.beginComputePass({
//             timestampWrites: {
//               querySet,
//               beginningOfPassWriteIndex: 0,
//               endOfPassWriteIndex: 1,
//             },
//           });
//         } else {
//           computePassEncoder = commandEncoder.beginComputePass();
//         }
//         computePassEncoder.setPipeline(computePipeline);
//         computePassEncoder.setBindGroup(0, computeBGCluster.bindGroups[0]);
//         computePassEncoder.dispatchWorkgroups(settings['Workgroups Per Step']);
//         computePassEncoder.end();
//         // Resolve time passed in between beginning and end of computePass
//         if (timestampQueryAvailable) {
//           commandEncoder.resolveQuerySet(
//             querySet,
//             0,
//             2,
//             timestampQueryResolveBuffer,
//             0
//           );
//           commandEncoder.copyBufferToBuffer(
//             timestampQueryResolveBuffer,
//             timestampQueryResultBuffer
//           );
//         }
//         settings['Step Index'] = settings['Step Index'] + 1;
//         currentStepController.setValue(
//           `${settings['Step Index']} of ${settings['Total Steps']}`
//         );
//         prevStepController.setValue(settings['Next Step']);
//         prevBlockHeightController.setValue(settings['Next Swap Span']);
//         nextBlockHeightController.setValue(settings['Next Swap Span'] / 2);
//         // Each cycle of a bitonic sort contains a flip operation followed by multiple disperse operations
//         // Next Swap Span will equal one when the sort needs to begin a new cycle of flip and disperse operations
//         if (settings['Next Swap Span'] === 1) {
//           // The next cycle's flip operation will have a maximum swap span 2 times that of the previous cycle
//           highestBlockHeight *= 2;
//           if (highestBlockHeight === settings['Total Elements'] * 2) {
//             // The next cycle's maximum swap span exceeds the total number of elements. Therefore, the sort is over.
//             // Accordingly, there will be no next step.
//             nextStepController.setValue('NONE');
//             // And if there is no next step, then there are no swaps, and no block range within which two elements are swapped.
//             nextBlockHeightController.setValue(0);
//             // Finally, with our sort completed, we can increment the number of total completed sorts executed with n 'Total Elements'
//             // and x 'Size Limit', which will allow us to calculate the average time of all sorts executed with this specific
//             // configuration of compute resources
//             settings.configToCompleteSwapsMap[settings.configKey].sorts += 1;
//           } else if (highestBlockHeight > settings['Workgroup Size'] * 2) {
//             // The next cycle's maximum swap span exceeds the range of a single workgroup, so our next flip will operate on global indices.
//             nextStepController.setValue('FLIP_GLOBAL');
//             nextBlockHeightController.setValue(highestBlockHeight);
//           } else {
//             // The next cycle's maximum swap span can be executed on a range of indices local to the workgroup.
//             nextStepController.setValue('FLIP_LOCAL');
//             nextBlockHeightController.setValue(highestBlockHeight);
//           }
//         } else {
//           // Otherwise, execute the next disperse operation
//           if (settings['Next Swap Span'] > settings['Workgroup Size'] * 2) {
//             nextStepController.setValue('DISPERSE_GLOBAL');
//           } else {
//             nextStepController.setValue('DISPERSE_LOCAL');
//           }
//         }

//         // Copy GPU accessible buffers to CPU accessible buffers
//         commandEncoder.copyBufferToBuffer(
//           elementsOutputBuffer,
//           elementsStagingBuffer
//         );
//         commandEncoder.copyBufferToBuffer(
//           atomicSwapsOutputBuffer,
//           atomicSwapsStagingBuffer
//         );
//       }
//       device.queue.submit([commandEncoder.finish()]);

//       if (
//         settings.executeStep &&
//         highestBlockHeight < settings['Total Elements'] * 4
//       ) {
//         // Copy GPU element data to CPU
//         await elementsStagingBuffer.mapAsync(
//           GPUMapMode.READ,
//           0,
//           elementsBufferSize
//         );
//         const copyElementsBuffer = elementsStagingBuffer.getMappedRange(
//           0,
//           elementsBufferSize
//         );
//         // Copy atomic swaps data to CPU
//         await atomicSwapsStagingBuffer.mapAsync(
//           GPUMapMode.READ,
//           0,
//           Uint32Array.BYTES_PER_ELEMENT
//         );
//         const copySwapsBuffer = atomicSwapsStagingBuffer.getMappedRange(
//           0,
//           Uint32Array.BYTES_PER_ELEMENT
//         );
//         const elementsData = copyElementsBuffer.slice(
//           0,
//           Uint32Array.BYTES_PER_ELEMENT * settings['Total Elements']
//         );
//         const swapsData = copySwapsBuffer.slice(
//           0,
//           Uint32Array.BYTES_PER_ELEMENT
//         );
//         // Extract data
//         const elementsOutput = new Uint32Array(elementsData);
//         totalSwapsController.setValue(new Uint32Array(swapsData)[0]);
//         elementsStagingBuffer.unmap();
//         atomicSwapsStagingBuffer.unmap();
//         // Elements output becomes elements input, swap accumulate
//         elements = elementsOutput;
//         setSwappedCell();

//         // Handle timestamp query stuff
//         if (timestampQueryAvailable) {
//           // Copy timestamp query result buffer data to CPU
//           await timestampQueryResultBuffer.mapAsync(
//             GPUMapMode.READ,
//             0,
//             2 * BigInt64Array.BYTES_PER_ELEMENT
//           );
//           const copyTimestampResult = new BigInt64Array(
//             timestampQueryResultBuffer.getMappedRange()
//           );
//           // Calculate new step, sort, and average sort times
//           const newStepTime =
//             Number(copyTimestampResult[1] - copyTimestampResult[0]) / 1000000;
//           const newSortTime = settings.sortTime + newStepTime;
//           // Apply calculated times to settings object as both number and 'ms' appended string
//           settings.stepTime = newStepTime;
//           settings.sortTime = newSortTime;
//           stepTimeController.setValue(`${newStepTime.toFixed(5)}ms`);
//           sortTimeController.setValue(`${newSortTime.toFixed(5)}ms`);
//           // Calculate new average sort upon end of final execution step of a full bitonic sort.
//           if (highestBlockHeight === settings['Total Elements'] * 2) {
//             // Lock off access to this larger if block..not best architected solution but eh
//             highestBlockHeight *= 2;
//             settings.configToCompleteSwapsMap[settings.configKey].time +=
//               newSortTime;
//             const averageSortTime =
//               settings.configToCompleteSwapsMap[settings.configKey].time /
//               settings.configToCompleteSwapsMap[settings.configKey].sorts;
//             averageSortTimeController.setValue(
//               `${averageSortTime.toFixed(5)}ms`
//             );
//           }
//           timestampQueryResultBuffer.unmap();
//           // Get correct range of data from CPU copy of GPU Data
//         }
//       }
//       settings.executeStep = false;
//       requestAnimationFrame(frame);
//     }
//     requestAnimationFrame(frame);
//   }
// ).then((init) => {
//   const canvas = document.querySelector('canvas') as HTMLCanvasElement;
//   const gui = new GUI();

//   init({ canvas, gui });
// });
