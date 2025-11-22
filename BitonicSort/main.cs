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

    uint adapterWorkgroupLimit = limits.MaxComputeWorkgroupSizeX;
    uint adapterInvocationLimit = limits.MaxComputeInvocationsPerWorkgroup;
    uint requestedWorkgroupLimit = 256u;

    var requiredFeatures = new List<FeatureName>();
    if (adapter.HasFeature(FeatureName.TimestampQuery))
    {
        requiredFeatures.Add(FeatureName.TimestampQuery);
    }

    var device = await adapter.RequestDeviceAsync(new()
    {
        RequiredFeatures = CollectionsMarshal.AsSpan(requiredFeatures),
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

    uint maxWorkgroupSize = requestedWorkgroupLimit;
    uint maxElements = maxWorkgroupSize * 32;

    List<uint> totalElementOptions = new();
    for (uint value = maxElements; value >= 4; value /= 2)
    {
        totalElementOptions.Add(value);
    }

    List<uint> sizeLimitOptions = new();
    for (uint value = maxWorkgroupSize; value >= 2; value /= 2)
    {
        sizeLimitOptions.Add(value);
    }

    var totalElementLabels = totalElementOptions.ConvertAll(static v => v.ToString()).ToArray();
    var sizeLimitLabels = sizeLimitOptions.ConvertAll(static v => v.ToString()).ToArray();

    var gridDims = BitonicMath.GetGridDimensions(maxElements);
    var settings = new BitonicSettings(maxElements, gridDims.Width, gridDims.Height, maxWorkgroupSize);
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
    (uint TotalElements, uint SizeLimit) currentConfigKey = (0, 0);
    int totalElementsIndex = 0;
    int sizeLimitIndex = 0;

    var random = new Random();
    var hostElements = new uint[maxElements];

    var elementBufferSize = (ulong)(maxElements * sizeof(uint));
    var elementsInputBuffer = device.CreateBuffer(new()
    {
        Size = elementBufferSize,
        Usage = BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc,
    });
    var elementsOutputBuffer = device.CreateBuffer(new()
    {
        Size = elementBufferSize,
        Usage = BufferUsage.Storage | BufferUsage.CopySrc,
    });
    var elementsReadbackBuffer = device.CreateBuffer(new()
    {
        Size = elementBufferSize,
        Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
    });
    var computeUniformsBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<ComputeUniforms>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });
    var swapCounterBuffer = device.CreateBuffer(new()
    {
        Size = sizeof(uint),
        Usage = BufferUsage.Storage | BufferUsage.CopySrc,
    });
    var swapReadbackBuffer = device.CreateBuffer(new()
    {
        Size = sizeof(uint),
        Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
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

    ComputePipeline? computePipeline = null;

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
        currentConfigKey = (settings.TotalElements, settings.SizeLimit);
        if (!configStats.ContainsKey(currentConfigKey))
        {
            configStats[currentConfigKey] = new ConfigStats();
        }
        var stats = configStats[currentConfigKey];
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
            hostElements[i] = (uint)i;
        }
        for (int i = length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (hostElements[i], hostElements[j]) = (hostElements[j], hostElements[i]);
        }
        queue.WriteBuffer(elementsInputBuffer, 0, hostElements.AsSpan(0, length));
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

        var stats = configStats[currentConfigKey];
        stats.Sorts++;
        stats.TotalTimeMs += settings.SortTimeMs;
        configStats[currentConfigKey] = stats;
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
