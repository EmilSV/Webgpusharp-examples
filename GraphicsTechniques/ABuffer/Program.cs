using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 600;
const int HEIGHT = 600;

var executingAssembly = Assembly.GetExecutingAssembly();
var opaqueWGSL = ResourceUtils.GetEmbeddedResource("ABuffer.shaders.opaque.wgsl", executingAssembly);
var translucentWGSL = ResourceUtils.GetEmbeddedResource("ABuffer.shaders.translucent.wgsl", executingAssembly);
var compositeWGSL = ResourceUtils.GetEmbeddedResource("ABuffer.shaders.composite.wgsl", executingAssembly);
var settings = new Settings();
var mesh = await Teapot.LoadMeshAsync();

return Run("A-Buffer", WIDTH, HEIGHT, async runContext =>
{
    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();


    var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });

    // Check that we have at least 2 storage buffers in fragment stage
    if (adapter?.GetLimits() is not { MaxStorageBuffersPerShaderStage: >= 2 })
    {
        Console.Error.WriteLine("Device does not support required limits: maxStorageBuffersPerShaderStage >= 2");
        Environment.Exit(1);
    }

    var device = await adapter.RequestDeviceAsync(
        new()
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
    );

    var deviceLimits = device.GetLimits();
    var queue = device.GetQueue()!;
    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

    guiContext.SetupIMGUI(device, surfaceFormat);

    surface.Configure(
        new()
        {
            Width = WIDTH,
            Height = HEIGHT,
            Usage = TextureUsage.RenderAttachment,
            Format = surfaceFormat,
            Device = device,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Opaque,
        }
    );

    // Create the model vertex buffer
    var vertexBuffer = device.CreateBuffer(
        new()
        {
            Label = "vertexBuffer",
            Size = mesh.Positions.GetSizeInBytes(),
            Usage = BufferUsage.Vertex,
            MappedAtCreation = true,
        }
    );

    vertexBuffer.GetMappedRange<Vector3>(data =>
    {
        mesh.Positions.CopyTo(data);
    });
    vertexBuffer.Unmap();

    // Create the model index buffer
    var indexBuffer = device.CreateBuffer(
        new()
        {
            Label = "indexBuffer",
            Size = (ulong)Unsafe.SizeOf<Index3Ushort>() * (ulong)mesh.Triangles.Length,
            Usage = BufferUsage.Index,
            MappedAtCreation = true,
        }
    );

    indexBuffer.GetMappedRange<Index3Ushort>(data =>
    {
        for (int i = 0; i < mesh.Triangles.Length; i++)
        {
            data[i] = new Index3Ushort
            {
                X = (ushort)mesh.Triangles[i].X,
                Y = (ushort)mesh.Triangles[i].Y,
                Z = (ushort)mesh.Triangles[i].Z,
            };
        }
    });
    indexBuffer.Unmap();

    var uniformBuffer = device.CreateBuffer(
        new()
        {
            Label = "uniformBuffer",
            Size = (ulong)Unsafe.SizeOf<Uniforms>(),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        }
    );

    var opaqueModule = device.CreateShaderModuleWGSL("opaqueModule",
        new()
        {
            Code = opaqueWGSL,
        }
    )!;

    var opaquePipeline = device.CreateRenderPipelineSync(
        new()
        {
            Layout = null, // Auto-layout
            Vertex = new()
            {
                Module = opaqueModule,
                Buffers =
                [
                    new()
                        {
                            ArrayStride = 3 * sizeof(float),
                            Attributes =
                            [
                                new()
                                {
                                    Format = VertexFormat.Float32x3,
                                    Offset = 0,
                                    ShaderLocation = 0,
                                },
                            ],
                        },
                ],
            },
            Fragment = new FragmentState()
            {
                Module = opaqueModule,
                Targets = [new() { Format = surfaceFormat }],
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
            },
            DepthStencil = new DepthStencilState()
            {
                DepthWriteEnabled = OptionalBool.True,
                DepthCompare = CompareFunction.Less,
                Format = TextureFormat.Depth24Plus,
            },
            Label = "opaquePipeline",
        }
    )!;

    var opaqueBindGroup = device.CreateBindGroup(
        new()
        {
            Layout = opaquePipeline.GetBindGroupLayout(0)!,
            Entries =
            [
                new()
                    {
                        Binding = 0,
                        Buffer = uniformBuffer,
                        Size = 16 * sizeof(float),
                    },
            ],
            Label = "opaqueBindGroup",
        }
    )!;

    var translucentModule = device.CreateShaderModuleWGSL("translucentModule",
        new()
        {
            Code = translucentWGSL,
        }
    )!;

    var translucentBindGroupLayout = device.CreateBindGroupLayout(
        new()
        {
            Label = "translucentBindGroupLayout",
            Entries =
            [
                new()
                    {
                        Binding = 0,
                        Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                        Buffer = new() { Type = BufferBindingType.Uniform },
                    },
                    new()
                    {
                        Binding = 1,
                        Visibility = ShaderStage.Fragment,
                        Buffer = new() { Type = BufferBindingType.Storage },
                    },
                    new()
                    {
                        Binding = 2,
                        Visibility = ShaderStage.Fragment,
                        Buffer = new() { Type = BufferBindingType.Storage },
                    },
                    new()
                    {
                        Binding = 3,
                        Visibility = ShaderStage.Fragment,
                        Texture = new() { SampleType = TextureSampleType.UnfilterableFloat },
                    },
                    new()
                    {
                        Binding = 4,
                        Visibility = ShaderStage.Fragment,
                        Buffer = new()
                        {
                            Type = BufferBindingType.Uniform,
                            HasDynamicOffset = true,
                        },
                    },
            ],
        }
    )!;

    var translucentPipeline = device.CreateRenderPipelineSync(
        new()
        {
            Layout = device.CreatePipelineLayout(
                new()
                {
                    BindGroupLayouts = [translucentBindGroupLayout],
                    Label = "translucentPipelineLayout",
                }
            ),
            Vertex = new()
            {
                Module = translucentModule,
                Buffers =
                [
                    new()
                    {
                        ArrayStride = 3 * sizeof(float),
                        Attributes =
                        [
                            new()
                            {
                                Format = VertexFormat.Float32x3,
                                Offset = 0,
                                ShaderLocation = 0,
                            },
                        ],
                    },
                ],
            },
            Fragment = new FragmentState()
            {
                Module = translucentModule,
                Targets =
                [
                    new()
                        {
                            Format = surfaceFormat,
                            WriteMask = ColorWriteMask.None,
                        },
                ],
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
            },
            Label = "translucentPipeline",
        }
    )!;

    var compositeModule = device.CreateShaderModuleWGSL("compositeModule",
        new()
        {
            Code = compositeWGSL
        }
    )!;

    var compositeBindGroupLayout = device.CreateBindGroupLayout(
        new()
        {
            Label = "compositeBindGroupLayout",
            Entries =
            [
                new()
                {
                    Binding = 0,
                    Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                    Buffer = new() { Type = BufferBindingType.Uniform },
                },
                new()
                {
                    Binding = 1,
                    Visibility = ShaderStage.Fragment,
                    Buffer = new() { Type = BufferBindingType.Storage },
                },
                new()
                {
                    Binding = 2,
                    Visibility = ShaderStage.Fragment,
                    Buffer = new() { Type = BufferBindingType.Storage },
                },
                new()
                {
                    Binding = 3,
                    Visibility = ShaderStage.Fragment,
                    Buffer = new()
                    {
                        Type = BufferBindingType.Uniform,
                        HasDynamicOffset = true,
                    },
                },
            ],
        }
    )!;

    var compositePipeline = device.CreateRenderPipelineSync(
        new()
        {
            Layout = device.CreatePipelineLayout(
                new()
                {
                    BindGroupLayouts = [compositeBindGroupLayout],
                    Label = "compositePipelineLayout",
                }
            ),
            Vertex = new()
            {
                Module = compositeModule,
            },
            Fragment = new FragmentState()
            {
                Module = compositeModule,
                Targets =
                [
                    new()
                        {
                            Format = surfaceFormat,
                            Blend = new()
                            {
                                Color = new()
                                {
                                    SrcFactor = BlendFactor.One,
                                    Operation = BlendOperation.Add,
                                    DstFactor = BlendFactor.OneMinusSrcAlpha,
                                },
                                Alpha = new(),
                            },
                        },
                ],
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
            },
            Label = "compositePipeline",
        }
    )!;

    Action doDraw = Configure();

    Action Configure()
    {
        float devicePixelRatio = runContext.GetDevicePixelRatio();

        // The default maximum storage buffer binding size is 128Mib. The amount
        // of memory we need to store transparent fragments depends on the size
        // of the canvas and the average number of layers per fragment we want to
        // support. When the devicePixelRatio is 1, we know that 128Mib is enough
        // to store 4 layers per pixel at 600x600. However, when the device pixel
        // ratio is high enough we will exceed this limit.
        //
        // We provide 2 choices of mitigations to this issue:
        // 1) Clamp the device pixel ratio to a value which we know will not break
        //    the limit. The tradeoff here is that the canvas resolution will not
        //    match the native resolution and therefore may have a reduction in
        //    quality.
        // 2) Break the frame into a series of horizontal slices using the scissor
        //    functionality and process a single slice at a time. This limits memory
        //    usage because we only need enough memory to process the dimensions
        //    of the slice. The tradeoff is the performance reduction due to multiple
        //    passes.
        if (settings.MemoryStrategy == MemoryStrategy.ClampPixelRatio)
        {
            devicePixelRatio = MathF.Min(devicePixelRatio, 1.0f);
        }

        uint canvasWidth = (uint)(WIDTH * devicePixelRatio);
        uint canvasHeight = (uint)(HEIGHT * devicePixelRatio);

        var depthTexture = device.CreateTexture(
            new()
            {
                Size = new(canvasWidth, canvasHeight),
                Format = TextureFormat.Depth24Plus,
                Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
                Label = "depthTexture",
            }
        )!;

        var depthTextureView = depthTexture.CreateView(
            new()
            {
                Label = "depthTextureView",
            }
        )!;

        // Determines how much memory is allocated to store linked-list elements
        const int averageLayersPerFragment = 4;

        // Each element stores
        // * color : vec4f
        // * depth : f32
        // * index of next element in the list : u32
        const int linkedListElementSize = 5 * sizeof(float) + 1 * sizeof(uint);

        // We want to keep the linked-list buffer size under the maxStorageBufferBindingSize.
        // Split the frame into enough slices to meet that constraint.
        ulong bytesPerline = canvasWidth * averageLayersPerFragment * linkedListElementSize;
        ulong maxLinesSupported = deviceLimits.MaxStorageBufferBindingSize / bytesPerline;
        int numSlices = (int)Math.Ceiling((double)canvasHeight / maxLinesSupported);
        uint sliceHeight = (uint)Math.Ceiling((double)canvasHeight / numSlices);
        ulong linkedListBufferSize = sliceHeight * bytesPerline;

        var linkedListBuffer = device.CreateBuffer(
            new()
            {
                Size = linkedListBufferSize,
                Usage = BufferUsage.Storage | BufferUsage.CopyDst,
                Label = "linkedListBuffer",
            }
        )!;

        // To slice up the frame we need to pass the starting fragment y position of the slice.
        // We do this using a uniform buffer with a dynamic offset.
        var sliceInfoBuffer = device.CreateBuffer(
            new()
            {
                Size = (ulong)numSlices * deviceLimits.MinUniformBufferOffsetAlignment,
                Usage = BufferUsage.Uniform,
                MappedAtCreation = true,
                Label = "sliceInfoBuffer",
            }
        )!;

        sliceInfoBuffer.GetMappedRange<int>(data =>
        {
            // This assumes minUniformBufferOffsetAlignment is a multiple of 4
            int stride = (int)(deviceLimits.MinUniformBufferOffsetAlignment / sizeof(int));
            for (int i = 0; i < numSlices; i++)
            {
                data[i * stride] = (int)(i * sliceHeight);
            }
        });
        sliceInfoBuffer.Unmap();

        // `Heads` struct contains the start index of the linked-list of translucent fragments
        // for a given pixel.
        // * numFragments : u32
        // * data : array<u32>
        var headsBuffer = device.CreateBuffer(
            new()
            {
                Size = (1 + canvasWidth * sliceHeight) * sizeof(uint),
                Usage = BufferUsage.Storage | BufferUsage.CopyDst,
                Label = "headsBuffer",
            }
        )!;

        var headsInitBuffer = device.CreateBuffer(
            new()
            {
                Size = (1 + canvasWidth * sliceHeight) * sizeof(uint),
                Usage = BufferUsage.CopySrc,
                MappedAtCreation = true,
                Label = "headsInitBuffer",
            }
        )!;

        headsInitBuffer.GetMappedRange<uint>(data =>
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = 0xFFFFFFFF;
            }
        });
        headsInitBuffer.Unmap();

        var translucentBindGroup = device.CreateBindGroup(
            new()
            {
                Layout = translucentBindGroupLayout,
                Entries =
                [
                    new()
                        {
                            Binding = 0,
                            Buffer = uniformBuffer,
                        },
                        new()
                        {
                            Binding = 1,
                            Buffer = headsBuffer,
                        },
                        new()
                        {
                            Binding = 2,
                            Buffer = linkedListBuffer,
                        },
                        new()
                        {
                            Binding = 3,
                            TextureView = depthTextureView,
                        },
                        new()
                        {
                            Binding = 4,
                            Buffer = sliceInfoBuffer,
                            Size = deviceLimits.MinUniformBufferOffsetAlignment,
                        },
                ],
                Label = "translucentBindGroup",
            }
        )!;

        var compositeBindGroup = device.CreateBindGroup(
            new()
            {
                Layout = compositePipeline.GetBindGroupLayout(0),
                Entries =
                [
                    new()
                        {
                            Binding = 0,
                            Buffer = uniformBuffer,
                        },
                        new()
                        {
                            Binding = 1,
                            Buffer = headsBuffer,
                        },
                        new()
                        {
                            Binding = 2,
                            Buffer = linkedListBuffer,
                        },
                        new()
                        {
                            Binding = 3,
                            Buffer = sliceInfoBuffer,
                            Size = deviceLimits.MinUniformBufferOffsetAlignment,
                        },
                ],
            }
        )!;

        // Rotates the camera around the origin based on time.
        Matrix4x4 GetCameraViewProjMatrix()
        {
            float aspect = canvasWidth / (float)canvasHeight;

            var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
                (float)(2.0f * Math.PI / 5.0f),
                aspect,
                1f,
                2000f
            );

            var upVector = new Vector3(0, 1, 0);
            var origin = new Vector3(0, 0, 0);
            var eyePosition = new Vector3(0, 5, -100);

            double rad = Math.PI * (Environment.TickCount64 / 5000.0) % (2.0 * Math.PI);
            var rotation = Matrix4x4.CreateRotationY((float)rad);
            eyePosition = Vector3.Transform(eyePosition, rotation);

            var viewMatrix = Matrix4x4.CreateLookAt(eyePosition, origin, upVector);

            var viewProjMatrix = viewMatrix * projectionMatrix;
            return viewProjMatrix;
        }

        return () =>
        {
            // Update the uniform buffer
            queue.WriteBuffer(uniformBuffer, new Uniforms
            {
                ModelViewProjectionMatrix = GetCameraViewProjMatrix(),
                MaxStorableFragments = averageLayersPerFragment * canvasWidth * sliceHeight,
                TargetWidth = canvasWidth,
            });

            var commandEncoder = device.CreateCommandEncoder();


            var texture = surface.GetCurrentTexture().Texture!;
            var textureView = texture.CreateView();

            // Draw the opaque objects
            var opaquePassEncoder = commandEncoder.BeginRenderPass(
                new()
                {
                    ColorAttachments =
                    [
                        new()
                            {
                                View = textureView,
                                ClearValue = new Color(0, 0, 0, 1.0),
                                LoadOp = LoadOp.Clear,
                                StoreOp = StoreOp.Store,
                            },
                    ],
                    DepthStencilAttachment = new RenderPassDepthStencilAttachment()
                    {
                        View = depthTextureView,
                        DepthClearValue = 1.0f,
                        DepthLoadOp = LoadOp.Clear,
                        DepthStoreOp = StoreOp.Store,
                    },
                    Label = "opaquePassDescriptor",
                }
            );
            opaquePassEncoder.SetPipeline(opaquePipeline);
            opaquePassEncoder.SetBindGroup(0, opaqueBindGroup);
            opaquePassEncoder.SetVertexBuffer(0, vertexBuffer);
            opaquePassEncoder.SetIndexBuffer(indexBuffer, IndexFormat.Uint16);
            opaquePassEncoder.DrawIndexed((uint)(mesh.Triangles.Length * 3), 8);
            opaquePassEncoder.End();

            for (int slice = 0; slice < numSlices; slice++)
            {
                // Initialize the heads buffer
                commandEncoder.CopyBufferToBuffer(headsInitBuffer, headsBuffer);

                uint scissorX = 0;
                uint scissorY = (uint)(slice * sliceHeight);
                uint scissorWidth = canvasWidth;
                uint scissorHeight = (uint)(Math.Min((slice + 1) * sliceHeight, canvasHeight) - (slice * sliceHeight));

                // Draw the translucent objects½
                var translucentPassEncoder = commandEncoder.BeginRenderPass(
                    new()
                    {
                        ColorAttachments =
                        [
                            new()
                                {
                                    LoadOp = LoadOp.Load,
                                    StoreOp = StoreOp.Store,
                                    View = textureView,
                                },
                        ],
                        Label = "translucentPassDescriptor",
                    }
                );

                // Set the scissor to only process a horizontal slice of the frame
                translucentPassEncoder.SetScissorRect(
                    x: scissorX,
                    y: scissorY,
                    width: scissorWidth,
                    height: scissorHeight
                );

                translucentPassEncoder.SetPipeline(translucentPipeline);
                translucentPassEncoder.SetBindGroup(
                    groupIndex: 0,
                    group: translucentBindGroup,
                    dynamicOffsets: [(uint)(slice * deviceLimits.MinUniformBufferOffsetAlignment)]
                );
                translucentPassEncoder.SetVertexBuffer(0, vertexBuffer);
                translucentPassEncoder.SetIndexBuffer(indexBuffer, IndexFormat.Uint16);
                translucentPassEncoder.DrawIndexed((uint)(mesh.Triangles.Length * 3), 8);
                translucentPassEncoder.End();

                // Composite the opaque and translucent objects
                var compositePassEncoder = commandEncoder.BeginRenderPass(
                    new()
                    {
                        ColorAttachments =
                        [
                            new()
                                {
                                    View = textureView,
                                    LoadOp = LoadOp.Load,
                                    StoreOp = StoreOp.Store,
                                },
                        ],
                        Label = "compositePassDescriptor",
                    }
                );

                // Set the scissor to only process a horizontal slice of the frame
                compositePassEncoder.SetScissorRect(
                    x: scissorX,
                    y: scissorY,
                    width: scissorWidth,
                    height: scissorHeight
                );

                compositePassEncoder.SetPipeline(compositePipeline);
                compositePassEncoder.SetBindGroup(
                    groupIndex: 0,
                    group: compositeBindGroup,
                    dynamicOffsets: [(uint)(slice * device.GetLimits().MinUniformBufferOffsetAlignment)]
                );
                compositePassEncoder.Draw(6);
                compositePassEncoder.End();
            }

            var guiCommandBuffer = settings.Draw(guiContext, surface, out bool settingsChanged);
            if (settingsChanged)
            {
                doDraw = Configure();
            }

            queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);

            surface.Present();
        };
    }

    runContext.OnFrame += () =>
    {
        doDraw();
    };
});

class Settings
{
    public MemoryStrategy MemoryStrategy = MemoryStrategy.MultiPass;

    public CommandBuffer Draw(GuiContext guiContext, Surface surface, out bool settingsChanged)
    {
        guiContext.NewFrame();
        settingsChanged = false;
        ImGui.SetNextWindowPos(new(0, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new(350, 80), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.3f);
        ImGui.Begin(
            "Settings",
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
        );

        if (ImGuiUtils.EnumDropdown("Memory Strategy", ref MemoryStrategy))
        {
            settingsChanged = true;
        }

        ImGui.End();
        guiContext.EndFrame();
        return guiContext.Render(surface)!.Value;
    }
}

enum MemoryStrategy
{
    MultiPass,
    ClampPixelRatio,
}

struct Uniforms
{
    public Matrix4x4 ModelViewProjectionMatrix; // at byte offset 0
    public uint MaxStorableFragments; // at byte offset 64
    public uint TargetWidth; // at byte offset 68
#pragma warning disable CS0169, IDE0051
    private InlineArray2<float> _pad0; // at byte offset 72
#pragma warning restore CS0169,IDE0051
}

struct Index3Ushort
{
    public ushort X;
    public ushort Y;
    public ushort Z;
}
