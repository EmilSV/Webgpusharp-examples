using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
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
var atomicToZeroWGSL = ResourceUtils.GetEmbeddedResource("BitonicSort.shaders.atomicToZero.wgsl", assembly);

static uint GetNumSteps(uint elements)
{
    var n = Math.Log2(elements);
    return (uint)(n * (n + 1) / 2);
}

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

    var settings = new BitonicSettings()
    {
        TotalElements = maxElements,
        GridWidth = defaultGridWidth,
        GridHeight = defaultGridHeight,
        WorkgroupSize = maxWorkgroupSize,
        SizeLimit = maxWorkgroupSize,
        WorkgroupsPerStep = maxElements / (maxWorkgroupSize * 2),
        TotalSteps = GetNumSteps(maxElements),
    };

    uint highestBlockHeight = 2;
    bool sizeLimitLocked = false;
    bool pendingAverageUpdate = false;
    bool sortCompleted = false;
    bool elementsReadbackPending = false;
    bool swapReadbackPending = false;
    int totalElementsIndex = 0;
    int sizeLimitIndex = 0;

    // Initialize initial elements array
    var elements = Enumerable.Range(0, (int)settings.TotalElements).Select(i => (uint)i).ToArray();

    // Initialize elementsBuffer and elementsStagingBuffer
    var elementBufferSize = (ulong)(totalElementOptions[0] * sizeof(float));

    // Initialize input, output, staging buffers
    var elementsInputBuffer = device.CreateBuffer(new()
    {
        Label = "BitonicSort.ElementsInputBuffer",
        Size = elementBufferSize,
        Usage = BufferUsage.Storage | BufferUsage.CopyDst,
    });
    var elementsOutputBuffer = device.CreateBuffer(new()
    {
        Label = "BitonicSort.ElementsOutputBuffer",
        Size = elementBufferSize,
        Usage = BufferUsage.Storage | BufferUsage.CopySrc,
    });
    var elementsStagingBuffer = device.CreateBuffer(new()
    {
        Label = "BitonicSort.ElementsStagingBuffer",
        Size = elementBufferSize,
        Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
    });

    var atomicSwapsOutputBuffer = device.CreateBuffer(new()
    {
        Label = "BitonicSort.AtomicSwapsOutputBuffer",
        Size = sizeof(uint),
        Usage = BufferUsage.Storage | BufferUsage.CopySrc,
    });

    var atomicSwapsStagingBuffer = device.CreateBuffer(new()
    {
        Label = "BitonicSort.AtomicSwapsStagingBuffer",
        Size = sizeof(uint),
        Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
    });

    var computeUniformsBuffer = device.CreateBuffer(new()
    {
        Label = "BitonicSort.ComputeUniformsBuffer",
        Size = (ulong)Unsafe.SizeOf<ComputeUniforms>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });


    var computeBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Label = "BitonicSort.ComputeBindGroupLayout",
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
        Label = "BitonicSort.ComputeBindGroup",
        Layout = computeBindGroupLayout,
        Entries =
        [
            new() { Binding = 0, Buffer = elementsInputBuffer },
            new() { Binding = 1, Buffer = elementsOutputBuffer },
            new() { Binding = 2, Buffer = computeUniformsBuffer },
            new() { Binding = 3, Buffer = atomicSwapsOutputBuffer },
        ],
    });

    var computePipeline = device.CreateComputePipeline(new()
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
            Layout = device.CreatePipelineLayout(new()
            {
                BindGroupLayouts = new[] { computeBindGroupLayout },
            }),
            Compute = new()
            {
                Module = device.CreateShaderModuleWGSL(new() { Code = BitonicCompute.NaiveBitonicCompute(settings.WorkgroupSize) })
            },
        });
    }

    var atomicToZeroComputePipeline = device.CreateComputePipeline(new()
    {
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts = new[] { computeBindGroupLayout },
        }),
        Compute = new()
        {
            Module = device.CreateShaderModuleWGSL(new() { Code = atomicToZeroWGSL }),
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
        if (pendingAverageUpdate && sortCompleted)
        {
            RegisterSortCompletion();
            pendingAverageUpdate = false;
            sortCompleted = false;
        }
    });
    timestampSupported = timestampRecorder.Supported;


    void ResetTimeInfo()
    {
        settings.StepTimeMs = 0;
        settings.SortTimeMs = 0;
        (uint Sorts, double TotalTimeMs) stats;
        if (!settings.ConfigToCompleteSwapsMap.TryGetValue(settings.ConfigKey, out stats))
        {
            stats.TotalTimeMs = 0;
            stats.Sorts = 0;
            settings.ConfigToCompleteSwapsMap[settings.ConfigKey] = stats;
        }
        var ast = stats.TotalTimeMs / stats.Sorts;
        if (double.IsNaN(ast))
        {
            ast = 0;
        }
        settings.AverageSortTimeMs = ast;
    }

    void RandomizeElementArray()
    {
        var currentIndex = elements.Length;
        // While there are elements to shuffle
        while (currentIndex != 0)
        {
            // Pick a remaining element
            int randomIndex = Random.Shared.Next(currentIndex);
            currentIndex--;
            (elements[currentIndex], elements[randomIndex]) = (elements[randomIndex], elements[currentIndex]);
        }
    }



    void ResetExecutionInformation()
    {
        // The workgroup size is either elements / 2 or Size Limit
        settings.WorkgroupSize = Math.Min(settings.TotalElements / 2, settings.SizeLimit);

        settings.WorkgroupsPerStep = settings.TotalElements / (settings.WorkgroupSize * 2);

        // Reset step Index and number of steps based on elements size
        settings.StepIndex = 0;
        settings.TotalSteps = GetNumSteps(settings.TotalElements);

        // Get new width and height of screen display in cells
        uint newCellWidth = (uint)(
            Math.Sqrt(settings.TotalElements) % 2 == 0
                ? Math.Floor(Math.Sqrt(settings.TotalElements))
                : Math.Floor(Math.Sqrt(settings.TotalElements / 2.0)));

        uint newCellHeight = settings.TotalElements / newCellWidth;
        settings.GridWidth = newCellWidth;
        settings.GridHeight = newCellHeight;

        // Set prevStep to None (restart) and next step to FLIP
        settings.PrevStep = StepType.None;
        settings.NextStep = StepType.FlipLocal;

        settings.PrevSwapSpan = 0;
        settings.NextSwapSpan = 2;

        var commandEncoder = device.CreateCommandEncoder();
        var computePassEncoder = commandEncoder.BeginComputePass();
        computePassEncoder.SetPipeline(atomicToZeroComputePipeline);
        computePassEncoder.SetBindGroup(0, computeBindGroup);
        computePassEncoder.DispatchWorkgroups(1);
        computePassEncoder.End();
        queue.Submit(commandEncoder.Finish());
        settings.TotalSwaps = 0;

        highestBlockHeight = 2;
    }

    void ResizeElementArray()
    {
        elements = Enumerable.Range(0, (int)settings.TotalElements).Select(i => (uint)i).ToArray();

        ResetExecutionInformation();
        // Create new shader invocation with workgroupSize that reflects number of invocations
        computePipeline = device.CreateComputePipeline(new()
        {
            Layout = device.CreatePipelineLayout(new()
            {
                BindGroupLayouts = [computeBindGroupLayout],
            }),
            Compute = new()
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = BitonicCompute.NaiveBitonicCompute(
                        Math.Min(settings.TotalElements / 2, settings.SizeLimit)
                    )
                })
            },
        });

        RandomizeElementArray();
        highestBlockHeight = 2;
    }

    RandomizeElementArray();

    void SetSwappedCell()
    {
        switch (settings.NextStep)
        {
            case StepType.FlipLocal:
            case StepType.FlipGlobal:
                {
                    var blockHeight = settings.NextSwapSpan;
                    var p2 = (settings.HoveredCell / blockHeight) + 1;
                    var p3 = settings.HoveredCell % blockHeight;
                    settings.SwappedCell = (int)(blockHeight * p2 - p3 - 1);
                }
                break;
            case StepType.DisperseLocal:
                {
                    var blockHeight = settings.NextSwapSpan;
                    var halfHeight = blockHeight / 2;
                    settings.SwappedCell = (int)(settings.HoveredCell % blockHeight < halfHeight
                        ? settings.HoveredCell + halfHeight
                        : settings.HoveredCell - halfHeight);
                }
                break;
            case StepType.None:
            default:
                settings.SwappedCell = settings.HoveredCell;
                break;
        }
    }


    Timer? autoSortTimer = null;
    void EndSortTimer()
    {
        autoSortTimer?.Dispose();
        autoSortTimer = null;
    }

    void StartSortTimer()
    {
        var currentTimerSpeed = settings.AutoSortSpeedMs;
        var currentTimerTimeSpan = TimeSpan.FromMilliseconds(currentTimerSpeed);
        autoSortTimer = new Timer(_ =>
        {
            if (settings.NextStep == StepType.None)
            {
                EndSortTimer();
                sizeLimitLocked = false;
                return;
            }
            if (settings.AutoSortSpeedMs != currentTimerSpeed)
            {
                EndSortTimer();
                StartSortTimer();
                return;
            }
            settings.ExecuteStep = true;
            SetSwappedCell();
        }, null, currentTimerTimeSpan, currentTimerTimeSpan);
    }

    runContext.Input.OnMouseMotion += events =>
    {
        if (ImGui.GetIO().WantCaptureMouse)
        {
            return;
        }

        var cellWidth = WindowWidth / (double)settings.GridWidth;
        var cellHeight = WindowHeight / (double)settings.GridHeight;
        var xIndex = Math.Floor(events.x / cellWidth);
        var yIndex = settings.GridHeight - 1 - Math.Floor(events.y / cellHeight);
        settings.HoveredCell = (int)(yIndex * settings.GridWidth + xIndex);
        SetSwappedCell();
    };

    void RegisterSortCompletion()
    {
        var key = settings.ConfigKey;
        if (!settings.ConfigToCompleteSwapsMap.TryGetValue(key, out var stats))
        {
            stats = (0, 0);
        }
        stats.Sorts += 1;
        stats.TotalTimeMs += settings.SortTimeMs;
        settings.ConfigToCompleteSwapsMap[key] = stats;
        settings.AverageSortTimeMs = stats.TotalTimeMs / stats.Sorts;
    }

    CommandBuffer DrawImGui(Surface surface)
    {
        guiContext.NewFrame();

        ImGui.SetNextWindowBgAlpha(0.9f);
        ImGui.SetNextWindowPos(new(0, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new(325, 600), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Bitonic Sort"))
        {
            if (ImGui.CollapsingHeader("Compute Resources", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // Total Elements
                if (ImGui.Combo("Total Elements", ref totalElementsIndex, totalElementLabels, totalElementLabels.Length))
                {
                    EndSortTimer();
                    settings.TotalElements = totalElementOptions[totalElementsIndex];
                    ResizeElementArray();
                    sizeLimitLocked = false;
                    ResetTimeInfo();
                }

                // Size Limit
                ImGui.BeginDisabled(sizeLimitLocked);
                if (ImGui.Combo("Size Limit", ref sizeLimitIndex, sizeLimitLabels, sizeLimitLabels.Length))
                {
                    settings.SizeLimit = sizeLimitOptions[sizeLimitIndex];
                    // Change total workgroups per step and size of a workgroup based on arbitrary constraint
                    settings.WorkgroupSize = Math.Min(settings.TotalElements / 2, settings.SizeLimit);
                    settings.WorkgroupsPerStep = (uint)Math.Ceiling(settings.TotalElements / (double)(settings.SizeLimit * 2));
                    // Apply new compute resources values to the sort's compute pipeline
                    computePipeline = CreateBitonicPipeline();
                    ResetTimeInfo();
                }
                ImGui.EndDisabled();

                // Workgroup Size (readonly)
                ImGui.BeginDisabled();
                int wgSize = (int)settings.WorkgroupSize;
                ImGui.InputInt("Workgroup Size", ref wgSize);
                ImGui.EndDisabled();

                // Workgroups Per Step (readonly)
                ImGui.BeginDisabled();
                int wgPerStep = (int)settings.WorkgroupsPerStep;
                ImGui.InputInt("Workgroups Per Step", ref wgPerStep);
                ImGui.EndDisabled();
            }

            if (ImGui.CollapsingHeader("Sort Controls", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // Execute Sort Step
                if (ImGui.Button("Execute Sort Step", new Vector2(-1, 0)))
                {
                    sizeLimitLocked = true;
                    EndSortTimer();
                    settings.ExecuteStep = true;
                }

                // Randomize Values
                if (ImGui.Button("Randomize Values", new Vector2(-1, 0)))
                {
                    EndSortTimer();
                    RandomizeElementArray();
                    ResetExecutionInformation();
                    ResetTimeInfo();
                    sizeLimitLocked = false;
                }

                // Log Elements
                if (ImGui.Button("Log Elements", new Vector2(-1, 0)))
                {
                    Console.WriteLine("[" + string.Join(", ", elements.Take((int)settings.TotalElements)) + "]");
                }

                // Auto Sort
                if (ImGui.Button("Auto Sort", new Vector2(-1, 0)))
                {
                    sizeLimitLocked = true;
                    StartSortTimer();
                }

                // Auto Sort Speed
                ImGui.SliderInt("Auto Sort Speed (ms)", ref settings.AutoSortSpeedMs, 50, 1000);
            }

            if (ImGui.CollapsingHeader("Grid Information", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // Display Mode
                ImGuiUtils.EnumDropdown("Display Mode", ref settings.DisplayMode);

                // Grid Dimensions (readonly)
                ImGui.BeginDisabled();
                string gridDims = $"{settings.GridWidth}x{settings.GridHeight}";
                ImGui.InputText("Grid Dimensions", ref gridDims, 50);
                ImGui.EndDisabled();

                // Hovered Cell (readonly)
                ImGui.BeginDisabled();
                int hoveredCell = settings.HoveredCell;
                ImGui.InputInt("Hovered Cell", ref hoveredCell);
                ImGui.EndDisabled();

                // Swapped Cell (readonly)
                ImGui.BeginDisabled();
                int swappedCell = settings.SwappedCell;
                ImGui.InputInt("Swapped Cell", ref swappedCell);
                ImGui.EndDisabled();
            }

            if (ImGui.CollapsingHeader("Execution Information", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // Current Step (readonly)
                ImGui.BeginDisabled();
                string currentStep = $"{settings.StepIndex} of {settings.TotalSteps}";
                ImGui.InputText("Current Step", ref currentStep, 50);
                ImGui.EndDisabled();

                // Prev Step (readonly)
                ImGui.BeginDisabled();
                string prevStep = settings.PrevStep.ToString();
                ImGui.InputText("Prev Step", ref prevStep, 50);
                ImGui.EndDisabled();

                // Next Step (readonly)
                ImGui.BeginDisabled();
                string nextStep = settings.NextStep.ToString();
                ImGui.InputText("Next Step", ref nextStep, 50);
                ImGui.EndDisabled();

                // Total Swaps (readonly)
                ImGui.BeginDisabled();
                int totalSwaps = (int)settings.TotalSwaps;
                ImGui.InputInt("Total Swaps", ref totalSwaps);
                ImGui.EndDisabled();

                // Prev Swap Span (readonly)
                ImGui.BeginDisabled();
                int prevSwapSpan = (int)settings.PrevSwapSpan;
                ImGui.InputInt("Prev Swap Span", ref prevSwapSpan);
                ImGui.EndDisabled();

                // Next Swap Span (readonly)
                ImGui.BeginDisabled();
                int nextSwapSpan = (int)settings.NextSwapSpan;
                ImGui.InputInt("Next Swap Span", ref nextSwapSpan);
                ImGui.EndDisabled();
            }

            if (ImGui.CollapsingHeader("Timestamp Info", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // Step Time (readonly)
                ImGui.BeginDisabled();
                string stepTime = $"{settings.StepTimeMs:F5}ms";
                ImGui.InputText("Step Time", ref stepTime, 50);
                ImGui.EndDisabled();

                // Sort Time (readonly)
                ImGui.BeginDisabled();
                string sortTime = $"{settings.SortTimeMs:F5}ms";
                ImGui.InputText("Sort Time", ref sortTime, 50);
                ImGui.EndDisabled();

                // Average Sort Time (readonly)
                ImGui.BeginDisabled();
                string avgSortTime = $"{settings.AverageSortTimeMs:F5}ms";
                ImGui.InputText("Average Sort Time", ref avgSortTime, 50);
                ImGui.EndDisabled();
            }

            ImGui.End();
        }

        guiContext.EndFrame();
        return guiContext.Render(surface)!.Value!;
    }

    async void FrameAsync(double deltaTimeMs)
    {
        // Write elements buffer
        var activeElements = elements.AsSpan(0, (int)settings.TotalElements);
        queue.WriteBuffer(elementsInputBuffer, 0, activeElements);

        // Write compute uniforms
        var computeUniforms = new ComputeUniforms
        {
            Width = settings.GridWidth,
            Height = settings.GridHeight,
            Algo = (uint)settings.NextStep,
            BlockHeight = settings.NextSwapSpan,
        };
        queue.WriteBuffer(computeUniformsBuffer, computeUniforms);

        var surfaceTexture = surface.GetCurrentTexture();
        var surfaceTextureView = surfaceTexture.Texture!.CreateView();

        var commandEncoder = device.CreateCommandEncoder();

        // Execute sort step if needed
        if (settings.ExecuteStep && highestBlockHeight < settings.TotalElements * 2)
        {
            var computePassEncoder = timestampRecorder.BeginPass(commandEncoder);
            computePassEncoder.SetPipeline(computePipeline);
            computePassEncoder.SetBindGroup(0, computeBindGroup);
            computePassEncoder.DispatchWorkgroups(settings.WorkgroupsPerStep);
            computePassEncoder.End();

            // Resolve timestamps
            timestampRecorder.Resolve(commandEncoder);

            // Update step information
            settings.StepIndex++;
            settings.PrevStep = settings.NextStep;
            settings.PrevSwapSpan = settings.NextSwapSpan;
            settings.NextSwapSpan /= 2;

            // Determine next step type
            if (settings.NextSwapSpan == 0)
            {
                // Begin new cycle
                highestBlockHeight *= 2;
                settings.NextSwapSpan = highestBlockHeight;

                if (highestBlockHeight == settings.TotalElements * 2)
                {
                    // Sort complete
                    settings.NextStep = StepType.None;
                    settings.NextSwapSpan = 0;
                    pendingAverageUpdate = true;
                    sortCompleted = true;
                }
                else if (highestBlockHeight > settings.WorkgroupSize * 2)
                {
                    settings.NextStep = StepType.FlipGlobal;
                }
                else
                {
                    settings.NextStep = StepType.FlipLocal;
                }
            }
            else
            {
                // Continue disperse operations
                if (settings.NextSwapSpan > settings.WorkgroupSize * 2)
                {
                    settings.NextStep = StepType.DisperseGlobal;
                }
                else
                {
                    settings.NextStep = StepType.DisperseLocal;
                }
            }

            // Copy GPU accessible buffers to CPU accessible buffers
            commandEncoder.CopyBufferToBuffer(elementsOutputBuffer, 0, elementsStagingBuffer, 0, elementBufferSize);
            commandEncoder.CopyBufferToBuffer(atomicSwapsOutputBuffer, 0, atomicSwapsStagingBuffer, 0, sizeof(uint));

            elementsReadbackPending = true;
            swapReadbackPending = true;
        }

        // Render display
        displayRenderer.Render(commandEncoder, surfaceTextureView, computeBindGroup, settings.DisplayMode);

        // Draw ImGui
        var imguiCommandBuffer = DrawImGui(surface);

        // Submit commands
        queue.Submit([commandEncoder.Finish(), imguiCommandBuffer]);

        surface.Present();

        // Handle async readbacks
        if (elementsReadbackPending)
        {
            _ = elementsStagingBuffer.MapAsync(MapMode.Read).ContinueWith(task =>
            {
                if (task.Result == MapAsyncStatus.Success)
                {
                    elementsStagingBuffer.GetConstMappedRange<uint>(0, (nuint)settings.TotalElements, data =>
                    {
                        data.CopyTo(elements.AsSpan(0, (int)settings.TotalElements));
                    });
                    elementsStagingBuffer.Unmap();
                    SetSwappedCell();
                }
            });
            elementsReadbackPending = false;
        }

        if (swapReadbackPending)
        {
            _ = atomicSwapsStagingBuffer.MapAsync(MapMode.Read).ContinueWith(task =>
            {
                if (task.Result == MapAsyncStatus.Success)
                {
                    atomicSwapsStagingBuffer.GetConstMappedRange<uint>(0, 1, data =>
                    {
                        settings.TotalSwaps = data[0];
                    });
                    atomicSwapsStagingBuffer.Unmap();
                }
            });
            swapReadbackPending = false;
        }

        // Try to download timestamp results
        timestampRecorder.TryDownload();

        settings.ExecuteStep = false;
    }

    runContext.OnFrame += () =>
    {
        FrameAsync(0);
    };
});
