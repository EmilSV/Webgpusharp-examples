// Two planes close to each other for depth precision test
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using Setup;
using ImGuiNET;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

const int geometryVertexSize = 4 * 8; // Byte size of one geometry vertex.
const int geometryPositionOffset = 0;
const int geometryColorOffset = 4 * 4; // Byte offset of geometry vertex color attribute.
const int geometryDrawCount = 6 * 2;

const float d = 0.0001f; // half distance between two planes
const float o = 0.5f; // half x offset to shift planes so they are only partially overlaping

float[] geometryVertexArray = [
  // float4 position, float4 color
  -1 - o, -1, d, 1, 1, 0, 0, 1,
  1 - o, -1, d, 1, 1, 0, 0, 1,
  -1 - o, 1, d, 1, 1, 0, 0, 1,
  1 - o, -1, d, 1, 1, 0, 0, 1,
  1 - o, 1, d, 1, 1, 0, 0, 1,
  -1 - o, 1, d, 1, 1, 0, 0, 1,

  -1 + o, -1, -d, 1, 0, 1, 0, 1,
  1 + o, -1, -d, 1, 0, 1, 0, 1,
  -1 + o, 1, -d, 1, 0, 1, 0, 1,
  1 + o, -1, -d, 1, 0, 1, 0, 1,
  1 + o, 1, -d, 1, 0, 1, 0, 1,
  -1 + o, 1, -d, 1, 0, 1, 0, 1,
];

const int xCount = 1;
const int yCount = 5;
const int numInstances = xCount * yCount;
const int matrixFloatCount = 16; // 4x4 matrix
const int matrixStride = 4 * matrixFloatCount; // 64;

Matrix4x4 depthRangeRemapMatrix = Matrix4x4.Identity;
depthRangeRemapMatrix[2, 2] = -1;
depthRangeRemapMatrix[3, 2] = 1;


DepthBufferMode[] depthBufferModes = [
    DepthBufferMode.Default,
    DepthBufferMode.Reversed,
];

var depthCompareFuncs = FrozenDictionary.ToFrozenDictionary<DepthBufferMode, CompareFunction>([
    new (DepthBufferMode.Default, CompareFunction.Less),
    new (DepthBufferMode.Reversed, CompareFunction.Greater)
]);

var depthClearValues = FrozenDictionary.ToFrozenDictionary<DepthBufferMode, float>([
    new (DepthBufferMode.Default, 1.0f),
    new (DepthBufferMode.Reversed, 0.0f)
]);

const int WIDTH = 600;
const int HEIGHT = 600;

return Run("Reversed Z", WIDTH, HEIGHT, async (instance, surface, guiContext, onFrame) =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();
    var vertexWGSL = ResourceUtils.GetEmbeddedResource("ReversedZ.shaders.vertex.wgsl");
    var fragmentWGSL = ResourceUtils.GetEmbeddedResource("ReversedZ.shaders.fragment.wgsl");
    var vertexDepthPrePassWGSL = ResourceUtils.GetEmbeddedResource("ReversedZ.shaders.vertexDepthPrePass.wgsl");
    var vertexTextureQuadWGSL = ResourceUtils.GetEmbeddedResource("ReversedZ.shaders.vertexTextureQuad.wgsl");
    var fragmentTextureQuadWGSL = ResourceUtils.GetEmbeddedResource("ReversedZ.shaders.fragmentTextureQuad.wgsl");
    var vertexPrecisionErrorPassWGSL = ResourceUtils.GetEmbeddedResource("ReversedZ.shaders.vertexPrecisionErrorPass.wgsl");
    var fragmentPrecisionErrorPassWGSL = ResourceUtils.GetEmbeddedResource("ReversedZ.shaders.fragmentPrecisionErrorPass.wgsl");

    var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });

    var device = await adapter.RequestDeviceAsync(
        new()
        {
            UncapturedErrorCallback = (type, message) =>
            {
                var messageString = Encoding.UTF8.GetString(message);
                Console.Error.WriteLine($"Uncaptured error: {type} {messageString}");
                Environment.Exit(1);
            },
            DeviceLostCallback = (reason, message) =>
            {
                var messageString = Encoding.UTF8.GetString(message);
                Console.Error.WriteLine($"Device lost: {reason} {messageString}");
            },
        }
    );

    var queue = device.GetQueue();

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
            AlphaMode = CompositeAlphaMode.Auto,
        }
    );

    var verticesBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)System.Buffer.ByteLength(geometryVertexArray),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    });

    verticesBuffer.GetMappedRange<float>(data =>
    {
        geometryVertexArray.CopyTo(data);
    });
    verticesBuffer.Unmap();

    const TextureFormat depthBufferFormat = TextureFormat.Depth32Float;

    var depthTextureBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.Depth,
                },
            },
        ],
    });

    // Model, view, projection matrices
    var uniformBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex,
                Buffer = new()
                {
                    Type = BufferBindingType.Uniform,
                },
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Vertex,
                Buffer = new()
                {
                    Type = BufferBindingType.Uniform,
                },
            },
        ],
    });

    var depthPrePassRenderPipelineLayout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = [
            uniformBindGroupLayout,
        ],
    });

    FrozenDictionary<DepthBufferMode, RenderPipeline> CreateDepthPrePassRenderPipelines()
    {
        // depthPrePass is used to render scene to the depth texture
        // this is not needed if you just want to use reversed z to render a scene
        var depthPrePassRenderPipelineDescriptorBase = new RenderPipelineDescriptor()
        {
            Layout = depthPrePassRenderPipelineLayout,
            Vertex = ref InlineInit(new VertexState()
            {
                Module = device!.CreateShaderModuleWGSL(new()
                {
                    Code = vertexDepthPrePassWGSL!
                }),
                Buffers =
                [
                    new()
                    {
                        ArrayStride = geometryVertexSize,
                        Attributes =
                        [
                            new()
                            {
                                // position
                                ShaderLocation = 0,
                                Offset = geometryPositionOffset,
                                Format = VertexFormat.Float32x4,
                            },
                        ],
                    },
                ],
            }),
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                CullMode = CullMode.Back,
            },
        };

        var depthStencil = new DepthStencilState()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = depthBufferFormat,
        };

        depthPrePassRenderPipelineDescriptorBase.DepthStencil = depthStencil with
        {
            DepthCompare = depthCompareFuncs![DepthBufferMode.Default]
        };

        var defaultPipeline = device.CreateRenderPipeline(depthPrePassRenderPipelineDescriptorBase);

        depthPrePassRenderPipelineDescriptorBase.DepthStencil = depthStencil with
        {
            DepthCompare = depthCompareFuncs![DepthBufferMode.Reversed]
        };

        var reversedPipeline = device.CreateRenderPipeline(depthPrePassRenderPipelineDescriptorBase);

        return FrozenDictionary.ToFrozenDictionary<DepthBufferMode, RenderPipeline>([
            new (DepthBufferMode.Default, defaultPipeline),
            new (DepthBufferMode.Reversed, reversedPipeline)
        ]);
    }

    var depthPrePassPipelines = CreateDepthPrePassRenderPipelines();

    // precisionPass is to draw precision error as color of depth value stored in depth buffer
    // compared to that directly calcualated in the shader
    var precisionPassRenderPipelineLayout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = [
            uniformBindGroupLayout,
            depthTextureBindGroupLayout,
        ],
    });

    FrozenDictionary<DepthBufferMode, RenderPipeline> CreatePrecisionPassRenderPipelines()
    {
        var precisionPassRenderPipelineDescriptorBase = new RenderPipelineDescriptor()
        {
            Layout = precisionPassRenderPipelineLayout,
            Vertex = ref InlineInit(new VertexState()
            {
                Module = device!.CreateShaderModuleWGSL(new()
                {
                    Code = vertexPrecisionErrorPassWGSL!
                }),
                Buffers =
                [
                    new()
                    {
                        ArrayStride = geometryVertexSize,
                        Attributes =
                        [
                            new()
                            {
                                // position
                                ShaderLocation = 0,
                                Offset = geometryPositionOffset,
                                Format = VertexFormat.Float32x4,
                            },
                        ],
                    },
                ],
            }),
            Fragment = new FragmentState()
            {
                Module = device!.CreateShaderModuleWGSL(new()
                {
                    Code = fragmentPrecisionErrorPassWGSL!
                }),
                Targets = [
                    new()
                    {
                        Format = surfaceFormat
                    }
                ],
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                CullMode = CullMode.Back,
            },
        };

        var DepthStencil = new DepthStencilState()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = depthBufferFormat,
        };

        precisionPassRenderPipelineDescriptorBase.DepthStencil = DepthStencil with
        {
            DepthCompare = depthCompareFuncs![DepthBufferMode.Default]
        };

        var defaultPipeline = device.CreateRenderPipeline(precisionPassRenderPipelineDescriptorBase);

        precisionPassRenderPipelineDescriptorBase.DepthStencil = DepthStencil with
        {
            DepthCompare = depthCompareFuncs![DepthBufferMode.Reversed]
        };

        var reversedPipeline = device.CreateRenderPipeline(precisionPassRenderPipelineDescriptorBase);

        return FrozenDictionary.ToFrozenDictionary<DepthBufferMode, RenderPipeline>([
            new (DepthBufferMode.Default, defaultPipeline),
            new (DepthBufferMode.Reversed, reversedPipeline)
        ]);

    }

    var precisionPassPipelines = CreatePrecisionPassRenderPipelines();


    var colorPassRenderPiplineLayout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = [
            uniformBindGroupLayout,
        ],
    });

    FrozenDictionary<DepthBufferMode, RenderPipeline> CreateColorPassRenderPipelines()
    {
        var colorPassRenderPipelineDescriptorBase = new RenderPipelineDescriptor()
        {
            Layout = colorPassRenderPiplineLayout,
            Vertex = ref InlineInit(new VertexState()
            {
                Module = device!.CreateShaderModuleWGSL(new()
                {
                    Code = vertexWGSL!
                }),
                Buffers =
                [
                    new()
                    {
                        ArrayStride = geometryVertexSize,
                        Attributes =
                        [
                            new()
                            {
                                // position
                                ShaderLocation = 0,
                                Offset = geometryPositionOffset,
                                Format = VertexFormat.Float32x4,
                            },
                            new()
                            {
                                // color
                                ShaderLocation = 1,
                                Offset = geometryColorOffset,
                                Format = VertexFormat.Float32x4,
                            },
                        ],
                    },
                ],
            }),
            Fragment = new FragmentState()
            {
                Module = device!.CreateShaderModuleWGSL(new()
                {
                    Code = fragmentWGSL!
                }),
                Targets = [
                    new()
                    {
                        Format = surfaceFormat
                    }
                ],
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                CullMode = CullMode.Back,
            },
        };

        var DepthStencil = new DepthStencilState()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = depthBufferFormat,
        };

        colorPassRenderPipelineDescriptorBase.DepthStencil = DepthStencil with
        {
            DepthCompare = depthCompareFuncs![DepthBufferMode.Default]
        };

        var defaultPipeline = device.CreateRenderPipeline(colorPassRenderPipelineDescriptorBase);

        colorPassRenderPipelineDescriptorBase.DepthStencil = DepthStencil with
        {
            DepthCompare = depthCompareFuncs![DepthBufferMode.Reversed]
        };

        var reversedPipeline = device.CreateRenderPipeline(colorPassRenderPipelineDescriptorBase);

        return FrozenDictionary.ToFrozenDictionary<DepthBufferMode, RenderPipeline>([
            new (DepthBufferMode.Default, defaultPipeline),
            new (DepthBufferMode.Reversed, reversedPipeline)
        ]);
    }

    var colorPassPipelines = CreateColorPassRenderPipelines();
    // textureQuadPass is draw a full screen quad of depth texture
    // to see the difference of depth value using reversed z compared to default depth buffer usage
    // 0.0 will be the furthest and 1.0 will be the closest
    var textureQuadPassPiplineLayout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = [
            depthTextureBindGroupLayout,
        ],
    });

    var textureQuadPassPipline = device.CreateRenderPipeline(new()
    {
        Layout = textureQuadPassPiplineLayout,
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device!.CreateShaderModuleWGSL(new()
            {
                Code = vertexTextureQuadWGSL!
            }),
        }),
        Fragment = new FragmentState()
        {
            Module = device!.CreateShaderModuleWGSL(new()
            {
                Code = fragmentTextureQuadWGSL!
            }),
            Targets = [
                new()
                {
                    Format = surfaceFormat
                }
            ],
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,
        },
    });

    var depthTexture = device.CreateTexture(new()
    {
        Label = "Depth Texture",
        Size = new(WIDTH, HEIGHT),
        Format = depthBufferFormat,
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
    });

    var depthTextureView = depthTexture.CreateView();

    var defaultDepthTexture = device.CreateTexture(new()
    {
        Label = "Default Depth Texture",
        Size = new(WIDTH, HEIGHT),
        Format = depthBufferFormat,
        Usage = TextureUsage.RenderAttachment,
    });

    var defaultDepthTextureView = defaultDepthTexture.CreateView();

    var depthTextureBindGroup = device.CreateBindGroup(new()
    {
        Layout = depthTextureBindGroupLayout,
        Entries = [
            new BindGroupEntry()
            {
                Binding = 0,
                TextureView = depthTextureView!
            },
        ],
    });

    const int uniformBufferSize = numInstances * matrixStride;
    var uniformBuffer = device.CreateBuffer(new()
    {
        Size = uniformBufferSize,
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var cameraMatrixBuffer = device.CreateBuffer(new()
    {
        Size = 4 * 16, // 4x4 matrix
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var cameraMatrixReversedDepthBuffer = device.CreateBuffer(new()
    {
        Size = 4 * 16, // 4x4 matrix
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });


    var uniformBindGroupDefault = device.CreateBindGroup(new()
    {
        Layout = uniformBindGroupLayout,
        Entries = [
            new BindGroupEntry()
            {
                Binding = 0,
                Buffer = uniformBuffer,
            },
            new BindGroupEntry()
            {
                Binding = 1,
                Buffer = cameraMatrixBuffer,
            },
        ],
    });

    var uniformBindGroupReversed = device.CreateBindGroup(new()
    {
        Layout = uniformBindGroupLayout,
        Entries = [
            new BindGroupEntry()
            {
                Binding = 0,
                Buffer = uniformBuffer,
            },
            new BindGroupEntry()
            {
                Binding = 1,
                Buffer = cameraMatrixReversedDepthBuffer,
            },
        ],
    });


    var modelMatrices = new Matrix4x4[numInstances];

    int m = 0;
    for (int x = 0; x < xCount; x++)
    {
        for (int y = 0; y < yCount; y++)
        {
            float z = -800.0f * m;
            float s = 1.0f + 50.0f * m;
            var newMatrix = Matrix4x4.CreateTranslation(
                new(
                    x - xCount / 2 + 0.5f,
                    (4.0f - 0.2f * z) * (y - yCount / 2 + 1.0f),
                    z
                )
            );
            newMatrix.Scale(new(s, s, s));
            modelMatrices[m] = newMatrix;
            m++;
        }
    }

    var viewMatrix = Matrix4x4.CreateTranslation(new(0, 0, -12));

    var aspect = (0.5f * WIDTH) / HEIGHT;

    var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
        2.0f * MathF.PI / 5.0f,
        aspect,
        5.0f,
        9999.0f
    );

    var viewProjectionMatrix = viewMatrix * projectionMatrix;

    var reversedRangeViewProjectionMatrix = viewProjectionMatrix * depthRangeRemapMatrix;

    queue.WriteBuffer(cameraMatrixBuffer, 0, viewProjectionMatrix);
    queue.WriteBuffer(
        cameraMatrixReversedDepthBuffer,
        0,
        reversedRangeViewProjectionMatrix
    );

    BindGroup GetUniformBindGroup(DepthBufferMode depthBufferMode) =>
        depthBufferMode switch
        {
            DepthBufferMode.Default => uniformBindGroupDefault!,
            DepthBufferMode.Reversed => uniformBindGroupReversed!,
            _ => throw new ArgumentOutOfRangeException(nameof(depthBufferMode), depthBufferMode, null)
        };

    RenderPassEncoder BeginDepthPrePass(CommandEncoder commandEncoder, DepthBufferMode depthBufferMode) =>
        commandEncoder.BeginRenderPass(new()
        {
            ColorAttachments = [],
            DepthStencilAttachment = new RenderPassDepthStencilAttachment()
            {
                View = depthTextureView!,
                DepthClearValue = depthClearValues![depthBufferMode],
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            }
        });
    RenderPassEncoder BeginDrawPass(CommandEncoder commandEncoder, DepthBufferMode depthBufferMode, TextureView attachment) =>
        commandEncoder.BeginRenderPass(new()
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment()
                {
                    View = attachment,
                    LoadOp = depthBufferMode switch {
                        DepthBufferMode.Default => LoadOp.Clear,
                        DepthBufferMode.Reversed => LoadOp.Load,
                        _ => throw new ArgumentOutOfRangeException(nameof(depthBufferMode), depthBufferMode, null)
                    },
                    ClearValue = depthBufferMode switch {
                        DepthBufferMode.Default => new(0.0, 0.0, 0.5, 1.0),
                        DepthBufferMode.Reversed => default,
                        _ => throw new ArgumentOutOfRangeException(nameof(depthBufferMode), depthBufferMode, null)
                    },
                    StoreOp = StoreOp.Store,
                }
            ],
            DepthStencilAttachment = new RenderPassDepthStencilAttachment()
            {
                View = defaultDepthTextureView!,
                DepthClearValue = depthClearValues![depthBufferMode],
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            }
        });
    RenderPassEncoder BeginTextureQuadPass(
        CommandEncoder commandEncoder, DepthBufferMode depthBufferMode,
        TextureView attachment) =>
        commandEncoder.BeginRenderPass(new()
        {
            ColorAttachments = [
                new RenderPassColorAttachment()
                {
                    View = attachment,
                    ClearValue=  depthBufferMode switch {
                        DepthBufferMode.Default => new(0.0, 0.0, 0.5, 1.0),
                        DepthBufferMode.Reversed => default,
                        _ => throw new ArgumentOutOfRangeException(nameof(depthBufferMode), depthBufferMode, null)
                    },
                    LoadOp = depthBufferMode switch {
                        DepthBufferMode.Default => LoadOp.Clear,
                        DepthBufferMode.Reversed => LoadOp.Load,
                        _ => throw new ArgumentOutOfRangeException(nameof(depthBufferMode), depthBufferMode, null)
                    },
                    StoreOp = StoreOp.Store,
                }
            ],
        });

    void UpdateTransformationMatrix(Span<Matrix4x4> matrices)
    {
        float now = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;

        for (int i = 0; i < numInstances; i++)
        {
            ref var modelMatrix = ref matrices![i];
            modelMatrix.Rotate(new(MathF.Sin(now), MathF.Cos(now), 0), MathF.PI / 180 * 30);
        }
    }

    void UpdateCameraMatrix()
    {
        var viewMatrix = Matrix4x4.CreateTranslation(new(0, 0, -12));

        var aspect = (0.5f * WIDTH) / HEIGHT;

        var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
            2.0f * MathF.PI / 5.0f,
            aspect,
            5.0f,
            9999.0f
        );

        var viewProjectionMatrix = viewMatrix * projectionMatrix;

        var reversedRangeViewProjectionMatrix = viewProjectionMatrix * depthRangeRemapMatrix;

        queue.WriteBuffer(cameraMatrixBuffer, 0, viewProjectionMatrix);
        queue.WriteBuffer(
            cameraMatrixReversedDepthBuffer,
            0,
            reversedRangeViewProjectionMatrix
        );

    }

    var settings = new Settings()
    {
        Mode = Settings.ModeType.Color
    };

    onFrame(() =>
    {
        UpdateCameraMatrix();
        Span<Matrix4x4> matrixOutput = stackalloc Matrix4x4[numInstances];
        modelMatrices.CopyTo(matrixOutput);
        UpdateTransformationMatrix(matrixOutput);
        queue.WriteBuffer(uniformBuffer, 0, matrixOutput);

        var attachment = surface.GetCurrentTexture().Texture!.CreateView();
        var commandEncoder = device.CreateCommandEncoder();

        if (settings.Mode == Settings.ModeType.Color)
        {
            for (var m = 0; m < depthBufferModes.Length; m++)
            {
                var depthBufferMode = depthBufferModes[m];
                var colorPass = BeginDrawPass(commandEncoder, depthBufferMode, attachment);
                colorPass.SetPipeline(colorPassPipelines[depthBufferMode]);
                colorPass.SetBindGroup(0, GetUniformBindGroup(depthBufferMode));
                colorPass.SetVertexBuffer(0, verticesBuffer);
                colorPass.SetViewport(
                    x: (uint)(WIDTH * m / 2),
                    y: 0,
                    width: WIDTH / 2,
                    height: HEIGHT,
                    minDepth: 0,
                    maxDepth: 1
                );
                colorPass.Draw(geometryDrawCount, numInstances, 0, 0);
                colorPass.End();
            }
        }
        else if (settings.Mode == Settings.ModeType.PrecisionError)
        {
            for (var m = 0; m < depthBufferModes.Length; m++)
            {
                var depthBufferMode = depthBufferModes[m];
                {
                    var depthPrePass = BeginDepthPrePass(commandEncoder, depthBufferMode);
                    depthPrePass.SetPipeline(depthPrePassPipelines[depthBufferMode]);
                    depthPrePass.SetBindGroup(0, GetUniformBindGroup(depthBufferMode));
                    depthPrePass.SetVertexBuffer(0, verticesBuffer);
                    depthPrePass.SetViewport(
                        x: (uint)(WIDTH * m / 2),
                        y: 0,
                        width: WIDTH / 2,
                        height: HEIGHT,
                        minDepth: 0,
                        maxDepth: 1
                    );
                    depthPrePass.Draw(geometryDrawCount, numInstances, 0, 0);
                    depthPrePass.End();
                }
                {
                    var precisionErrorPass = BeginDrawPass(commandEncoder, depthBufferMode, attachment);
                    precisionErrorPass.SetPipeline(precisionPassPipelines[depthBufferMode]);
                    precisionErrorPass.SetBindGroup(0, GetUniformBindGroup(depthBufferMode));
                    precisionErrorPass.SetBindGroup(1, depthTextureBindGroup);
                    precisionErrorPass.SetVertexBuffer(0, verticesBuffer);
                    precisionErrorPass.SetViewport(
                        x: (uint)(WIDTH * m / 2),
                        y: 0,
                        width: WIDTH / 2,
                        height: HEIGHT,
                        minDepth: 0,
                        maxDepth: 1
                    );
                    precisionErrorPass.Draw(geometryDrawCount, numInstances, 0, 0);
                    precisionErrorPass.End();
                }
            }
        }
        else
        {
            // depth texture quad
            for (var m = 0; m < depthBufferModes.Length; m++)
            {
                var depthBufferMode = depthBufferModes[m];
                {
                    var depthPrePass = BeginDepthPrePass(commandEncoder, depthBufferMode);
                    depthPrePass.SetPipeline(depthPrePassPipelines[depthBufferMode]);
                    depthPrePass.SetBindGroup(0, GetUniformBindGroup(depthBufferMode));
                    depthPrePass.SetVertexBuffer(0, verticesBuffer);
                    depthPrePass.SetViewport(
                        x: (uint)(WIDTH * m / 2),
                        y: 0,
                        width: WIDTH / 2,
                        height: HEIGHT,
                        minDepth: 0,
                        maxDepth: 1
                    );
                    depthPrePass.Draw(geometryDrawCount, numInstances, 0, 0);
                    depthPrePass.End();
                }
                {
                    var depthTextureQuadPass = BeginTextureQuadPass(commandEncoder, depthBufferMode, attachment);
                    depthTextureQuadPass.SetPipeline(textureQuadPassPipline);
                    depthTextureQuadPass.SetBindGroup(0, depthTextureBindGroup);
                    depthTextureQuadPass.SetViewport(
                        x: (uint)(WIDTH * m / 2),
                        y: 0,
                        width: WIDTH / 2,
                        height: HEIGHT,
                        minDepth: 0,
                        maxDepth: 1
                    );
                    depthTextureQuadPass.Draw(6);
                    depthTextureQuadPass.End();
                }
            }
        }

        // draw the gui
        var guiBuffer = settings.DrawGUI(guiContext, surface);

        queue.Submit([commandEncoder.Finish(), guiBuffer]);
        surface.Present();
    });
});

enum DepthBufferMode
{
    Default = 0,
    Reversed,
}

sealed class Settings
{
    public enum ModeType
    {
        Color,
        PrecisionError,
        DepthTexture,
    }

    public ModeType Mode = ModeType.Color;

    public CommandBuffer DrawGUI(GuiContext guiContext, Surface surface)
    {
        guiContext.NewFrame();
        ImGui.SetNextWindowPos(new(400, 500));
        ImGui.SetNextWindowSize(new(200, 100));
        ImGui.SetNextWindowBgAlpha(0.3f);
        ImGui.Begin("Settings",
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse
        );
        ImGuiUtils.EnumDropdown("Mode", ref Mode);
        ImGui.End();
        guiContext.EndFrame();

        return guiContext.Render(surface)!.Value!;
    }
}