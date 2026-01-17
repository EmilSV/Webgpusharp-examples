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
                            Blend = new BlendState()
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
        // In TypeScript, this would be window.devicePixelRatio
        // For this demo, we simulate a high DPI display (2.0x) to demonstrate
        // the difference between the two memory strategies
        float devicePixelRatio = 1.0f;

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
        ulong maxLinesSupported = device.GetLimits().MaxStorageBufferBindingSize / bytesPerline;
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
                Size = (ulong)(numSlices * device.GetLimits().MinUniformBufferOffsetAlignment),
                Usage = BufferUsage.Uniform,
                MappedAtCreation = true,
                Label = "sliceInfoBuffer",
            }
        )!;

        sliceInfoBuffer.GetMappedRange<int>(data =>
        {
            // This assumes minUniformBufferOffsetAlignment is a multiple of 4
            int stride = (int)(device.GetLimits().MinUniformBufferOffsetAlignment / sizeof(int));
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
                            Size = device.GetLimits().MinUniformBufferOffsetAlignment,
                        },
                ],
                Label = "translucentBindGroup",
            }
        )!;

        var compositeBindGroup = device.CreateBindGroup(
            new()
            {
                Layout = compositePipeline.GetBindGroupLayout(0)!,
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
                            Size = device.GetLimits().MinUniformBufferOffsetAlignment,
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

            long unixTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            double time = unixTime / 5000.0;
            double rad = (Math.PI * (time)) % (2.0 * Math.PI);
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

            var texture = surface.GetCurrentTexture().Texture!;
            var textureView = texture.CreateView();

            var commandEncoder = device.CreateCommandEncoder();

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
                commandEncoder.CopyBufferToBuffer(headsInitBuffer, 0, headsBuffer, 0, headsBuffer.GetSize());

                uint scissorX = 0;
                uint scissorY = (uint)(slice * sliceHeight);
                uint scissorWidth = canvasWidth;
                uint scissorHeight = Math.Min((uint)((slice + 1) * sliceHeight), canvasHeight) - (uint)(slice * sliceHeight);

                // Draw the translucent objects
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
                translucentPassEncoder.SetScissorRect(scissorX, scissorY, scissorWidth, scissorHeight);

                translucentPassEncoder.SetPipeline(translucentPipeline);
                translucentPassEncoder.SetBindGroup(
                    0,
                    translucentBindGroup,
                    [(uint)(slice * device.GetLimits().MinUniformBufferOffsetAlignment)]
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
                compositePassEncoder.SetScissorRect(scissorX, scissorY, scissorWidth, scissorHeight);

                compositePassEncoder.SetPipeline(compositePipeline);
                compositePassEncoder.SetBindGroup(
                    0,
                    compositeBindGroup,
                    [(uint)(slice * device.GetLimits().MinUniformBufferOffsetAlignment)]
                );
                compositePassEncoder.Draw(6);
                compositePassEncoder.End();
            }

            guiContext.NewFrame();

            settings.Draw(() => { doDraw = Configure(); });

            guiContext.EndFrame();

            var guiCommandBuffer = guiContext.Render(surface)!.Value;

            queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);

            surface.Present();
        };
    }

    runContext.OnFrame += () =>
    {
        doDraw();
    };
});

static ulong RoundUp(ulong n, ulong k)
{
    return (ulong)Math.Ceiling((double)n / k) * k;
}

class Settings
{
    public MemoryStrategy MemoryStrategy = MemoryStrategy.MultiPass;

    public void Draw(Action onSettingsChanged)
    {
        ImGui.SetNextWindowPos(new(0, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new(350, 80), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.3f);
        ImGui.Begin(
            "Settings",
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
        );

        if (ImGuiUtils.EnumDropdown("Memory Strategy", ref MemoryStrategy))
        {
            onSettingsChanged();
        }

        ImGui.End();
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
#pragma warning disable IDE0051
    private InlineArray2<float> _pad0; // at byte offset 72
#pragma warning restore IDE0051
}

struct Index3Ushort
{
    public ushort X;
    public ushort Y;
    public ushort Z;
}


// import { mat4, vec3 } from 'wgpu-matrix';
// import { GUI } from 'dat.gui';

// import { quitIfWebGPUNotAvailable, quitIfLimitLessThan } from '../util';
// import { mesh } from '../../meshes/teapot';

// import opaqueWGSL from './opaque.wgsl';
// import translucentWGSL from './translucent.wgsl';
// import compositeWGSL from './composite.wgsl';

// function roundUp(n: number, k: number): number {
//   return Math.ceil(n / k) * k;
// }

// const canvas = document.querySelector('canvas') as HTMLCanvasElement;
// const adapter = await navigator.gpu?.requestAdapter({
//   featureLevel: 'compatibility',
// });
// const limits: Record<string, GPUSize32> = {};
// quitIfLimitLessThan(adapter, 'maxStorageBuffersInFragmentStage', 2, limits);
// const device = await adapter?.requestDevice({
//   requiredLimits: limits,
// });
// quitIfWebGPUNotAvailable(adapter, device);

// const context = canvas.getContext('webgpu');
// const presentationFormat = navigator.gpu.getPreferredCanvasFormat();

// context.configure({
//   device,
//   format: presentationFormat,
//   alphaMode: 'opaque',
// });

// const params = new URLSearchParams(window.location.search);

// const settings = {
//   memoryStrategy: params.get('memoryStrategy') || 'multipass',
// };

// // Create the model vertex buffer
// const vertexBuffer = device.createBuffer({
//   size: 3 * mesh.positions.length * Float32Array.BYTES_PER_ELEMENT,
//   usage: GPUBufferUsage.VERTEX,
//   mappedAtCreation: true,
//   label: 'vertexBuffer',
// });
// {
//   const mapping = new Float32Array(vertexBuffer.getMappedRange());
//   for (let i = 0; i < mesh.positions.length; ++i) {
//     mapping.set(mesh.positions[i], 3 * i);
//   }
//   vertexBuffer.unmap();
// }

// // Create the model index buffer
// const indexCount = mesh.triangles.length * 3;
// const indexBuffer = device.createBuffer({
//   size: indexCount * Uint16Array.BYTES_PER_ELEMENT,
//   usage: GPUBufferUsage.INDEX,
//   mappedAtCreation: true,
//   label: 'indexBuffer',
// });
// {
//   const mapping = new Uint16Array(indexBuffer.getMappedRange());
//   for (let i = 0; i < mesh.triangles.length; ++i) {
//     mapping.set(mesh.triangles[i], 3 * i);
//   }
//   indexBuffer.unmap();
// }

// // Uniforms contains:
// // * modelViewProjectionMatrix: mat4x4f
// // * maxStorableFragments: u32
// // * targetWidth: u32
// const uniformsSize = roundUp(
//   16 * Float32Array.BYTES_PER_ELEMENT + 2 * Uint32Array.BYTES_PER_ELEMENT,
//   16
// );

// const uniformBuffer = device.createBuffer({
//   size: uniformsSize,
//   usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
//   label: 'uniformBuffer',
// });

// const opaqueModule = device.createShaderModule({
//   code: opaqueWGSL,
//   label: 'opaqueModule',
// });

// const opaquePipeline = device.createRenderPipeline({
//   layout: 'auto',
//   vertex: {
//     module: opaqueModule,
//     buffers: [
//       {
//         arrayStride: 3 * Float32Array.BYTES_PER_ELEMENT,
//         attributes: [
//           {
//             // position
//             format: 'float32x3',
//             offset: 0,
//             shaderLocation: 0,
//           },
//         ],
//       },
//     ],
//   },
//   fragment: {
//     module: opaqueModule,
//     targets: [
//       {
//         format: presentationFormat,
//       },
//     ],
//   },
//   primitive: {
//     topology: 'triangle-list',
//   },
//   depthStencil: {
//     depthWriteEnabled: true,
//     depthCompare: 'less',
//     format: 'depth24plus',
//   },
//   label: 'opaquePipeline',
// });

// const opaquePassDescriptor: GPURenderPassDescriptor = {
//   colorAttachments: [
//     {
//       view: undefined,
//       clearValue: [0, 0, 0, 1.0],
//       loadOp: 'clear',
//       storeOp: 'store',
//     },
//   ],
//   depthStencilAttachment: {
//     view: undefined,
//     depthClearValue: 1.0,
//     depthLoadOp: 'clear',
//     depthStoreOp: 'store',
//   },
//   label: 'opaquePassDescriptor',
// };

// const opaqueBindGroup = device.createBindGroup({
//   layout: opaquePipeline.getBindGroupLayout(0),
//   entries: [
//     {
//       binding: 0,
//       resource: {
//         buffer: uniformBuffer,
//         size: 16 * Float32Array.BYTES_PER_ELEMENT,
//         label: 'modelViewProjection',
//       },
//     },
//   ],
//   label: 'opaquePipeline',
// });

// const translucentModule = device.createShaderModule({
//   code: translucentWGSL,
//   label: 'translucentModule',
// });

// const translucentBindGroupLayout = device.createBindGroupLayout({
//   label: 'translucentBindGroupLayout',
//   entries: [
//     {
//       binding: 0,
//       visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'uniform',
//       },
//     },
//     {
//       binding: 1,
//       visibility: GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'storage',
//       },
//     },
//     {
//       binding: 2,
//       visibility: GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'storage',
//       },
//     },
//     {
//       binding: 3,
//       visibility: GPUShaderStage.FRAGMENT,
//       texture: { sampleType: 'unfilterable-float' },
//     },
//     {
//       binding: 4,
//       visibility: GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'uniform',
//         hasDynamicOffset: true,
//       },
//     },
//   ],
// });

// const translucentPipeline = device.createRenderPipeline({
//   layout: device.createPipelineLayout({
//     bindGroupLayouts: [translucentBindGroupLayout],
//     label: 'translucentPipelineLayout',
//   }),
//   vertex: {
//     module: translucentModule,
//     buffers: [
//       {
//         arrayStride: 3 * Float32Array.BYTES_PER_ELEMENT,
//         attributes: [
//           {
//             format: 'float32x3',
//             offset: 0,
//             shaderLocation: 0,
//           },
//         ],
//       },
//     ],
//   },
//   fragment: {
//     module: translucentModule,
//     targets: [
//       {
//         format: presentationFormat,
//         writeMask: 0x0,
//       },
//     ],
//   },
//   primitive: {
//     topology: 'triangle-list',
//   },
//   label: 'translucentPipeline',
// });

// const translucentPassDescriptor: GPURenderPassDescriptor = {
//   colorAttachments: [
//     {
//       loadOp: 'load',
//       storeOp: 'store',
//       view: undefined,
//     },
//   ],
//   label: 'translucentPassDescriptor',
// };

// const compositeModule = device.createShaderModule({
//   code: compositeWGSL,
//   label: 'compositeModule',
// });

// const compositeBindGroupLayout = device.createBindGroupLayout({
//   label: 'compositeBindGroupLayout',
//   entries: [
//     {
//       binding: 0,
//       visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'uniform',
//       },
//     },
//     {
//       binding: 1,
//       visibility: GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'storage',
//       },
//     },
//     {
//       binding: 2,
//       visibility: GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'storage',
//       },
//     },
//     {
//       binding: 3,
//       visibility: GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'uniform',
//         hasDynamicOffset: true,
//       },
//     },
//   ],
// });

// const compositePipeline = device.createRenderPipeline({
//   layout: device.createPipelineLayout({
//     bindGroupLayouts: [compositeBindGroupLayout],
//     label: 'compositePipelineLayout',
//   }),
//   vertex: {
//     module: compositeModule,
//   },
//   fragment: {
//     module: compositeModule,
//     targets: [
//       {
//         format: presentationFormat,
//         blend: {
//           color: {
//             srcFactor: 'one',
//             operation: 'add',
//             dstFactor: 'one-minus-src-alpha',
//           },
//           alpha: {},
//         },
//       },
//     ],
//   },
//   primitive: {
//     topology: 'triangle-list',
//   },
//   label: 'compositePipeline',
// });

// const compositePassDescriptor: GPURenderPassDescriptor = {
//   colorAttachments: [
//     {
//       view: undefined,
//       loadOp: 'load',
//       storeOp: 'store',
//     },
//   ],
//   label: 'compositePassDescriptor',
// };

// const configure = () => {
//   let devicePixelRatio = window.devicePixelRatio;

//   // The default maximum storage buffer binding size is 128Mib. The amount
//   // of memory we need to store transparent fragments depends on the size
//   // of the canvas and the average number of layers per fragment we want to
//   // support. When the devicePixelRatio is 1, we know that 128Mib is enough
//   // to store 4 layers per pixel at 600x600. However, when the device pixel
//   // ratio is high enough we will exceed this limit.
//   //
//   // We provide 2 choices of mitigations to this issue:
//   // 1) Clamp the device pixel ratio to a value which we know will not break
//   //    the limit. The tradeoff here is that the canvas resolution will not
//   //    match the native resolution and therefore may have a reduction in
//   //    quality.
//   // 2) Break the frame into a series of horizontal slices using the scissor
//   //    functionality and process a single slice at a time. This limits memory
//   //    usage because we only need enough memory to process the dimensions
//   //    of the slice. The tradeoff is the performance reduction due to multiple
//   //    passes.
//   if (settings.memoryStrategy === 'clamp-pixel-ratio') {
//     devicePixelRatio = Math.min(window.devicePixelRatio, 3);
//   }

//   canvas.width = canvas.clientWidth * devicePixelRatio;
//   canvas.height = canvas.clientHeight * devicePixelRatio;

//   const depthTexture = device.createTexture({
//     size: [canvas.width, canvas.height],
//     format: 'depth24plus',
//     usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
//     label: 'depthTexture',
//   });

//   const depthTextureView = depthTexture.createView({
//     label: 'depthTextureView',
//   });

//   // Determines how much memory is allocated to store linked-list elements
//   const averageLayersPerFragment = 4;

//   // Each element stores
//   // * color : vec4f
//   // * depth : f32
//   // * index of next element in the list : u32
//   const linkedListElementSize =
//     5 * Float32Array.BYTES_PER_ELEMENT + 1 * Uint32Array.BYTES_PER_ELEMENT;

//   // We want to keep the linked-list buffer size under the maxStorageBufferBindingSize.
//   // Split the frame into enough slices to meet that constraint.
//   const bytesPerline =
//     canvas.width * averageLayersPerFragment * linkedListElementSize;
//   const maxLinesSupported = Math.floor(
//     device.limits.maxStorageBufferBindingSize / bytesPerline
//   );
//   const numSlices = Math.ceil(canvas.height / maxLinesSupported);
//   const sliceHeight = Math.ceil(canvas.height / numSlices);
//   const linkedListBufferSize = sliceHeight * bytesPerline;

//   const linkedListBuffer = device.createBuffer({
//     size: linkedListBufferSize,
//     usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
//     label: 'linkedListBuffer',
//   });

//   // To slice up the frame we need to pass the starting fragment y position of the slice.
//   // We do this using a uniform buffer with a dynamic offset.
//   const sliceInfoBuffer = device.createBuffer({
//     size: numSlices * device.limits.minUniformBufferOffsetAlignment,
//     usage: GPUBufferUsage.UNIFORM,
//     mappedAtCreation: true,
//     label: 'sliceInfoBuffer',
//   });
//   {
//     const mapping = new Int32Array(sliceInfoBuffer.getMappedRange());

//     // This assumes minUniformBufferOffsetAlignment is a multiple of 4
//     const stride =
//       device.limits.minUniformBufferOffsetAlignment /
//       Int32Array.BYTES_PER_ELEMENT;
//     for (let i = 0; i < numSlices; ++i) {
//       mapping[i * stride] = i * sliceHeight;
//     }
//     sliceInfoBuffer.unmap();
//   }

//   // `Heads` struct contains the start index of the linked-list of translucent fragments
//   // for a given pixel.
//   // * numFragments : u32
//   // * data : array<u32>
//   const headsBuffer = device.createBuffer({
//     size: (1 + canvas.width * sliceHeight) * Uint32Array.BYTES_PER_ELEMENT,
//     usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
//     label: 'headsBuffer',
//   });

//   const headsInitBuffer = device.createBuffer({
//     size: (1 + canvas.width * sliceHeight) * Uint32Array.BYTES_PER_ELEMENT,
//     usage: GPUBufferUsage.COPY_SRC,
//     mappedAtCreation: true,
//     label: 'headsInitBuffer',
//   });
//   {
//     const buffer = new Uint32Array(headsInitBuffer.getMappedRange());

//     for (let i = 0; i < buffer.length; ++i) {
//       buffer[i] = 0xffffffff;
//     }

//     headsInitBuffer.unmap();
//   }

//   const translucentBindGroup = device.createBindGroup({
//     layout: translucentBindGroupLayout,
//     entries: [
//       {
//         binding: 0,
//         resource: {
//           buffer: uniformBuffer,
//         },
//       },
//       {
//         binding: 1,
//         resource: {
//           buffer: headsBuffer,
//         },
//       },
//       {
//         binding: 2,
//         resource: {
//           buffer: linkedListBuffer,
//         },
//       },
//       {
//         binding: 3,
//         resource: depthTextureView,
//       },
//       {
//         binding: 4,
//         resource: {
//           buffer: sliceInfoBuffer,
//           size: device.limits.minUniformBufferOffsetAlignment,
//         },
//       },
//     ],
//     label: 'translucentBindGroup',
//   });

//   const compositeBindGroup = device.createBindGroup({
//     layout: compositePipeline.getBindGroupLayout(0),
//     entries: [
//       {
//         binding: 0,
//         resource: {
//           buffer: uniformBuffer,
//         },
//       },
//       {
//         binding: 1,
//         resource: {
//           buffer: headsBuffer,
//         },
//       },
//       {
//         binding: 2,
//         resource: {
//           buffer: linkedListBuffer,
//         },
//       },
//       {
//         binding: 3,
//         resource: {
//           buffer: sliceInfoBuffer,
//           size: device.limits.minUniformBufferOffsetAlignment,
//         },
//       },
//     ],
//   });

//   opaquePassDescriptor.depthStencilAttachment.view = depthTextureView;

//   // Rotates the camera around the origin based on time.
//   function getCameraViewProjMatrix() {
//     const aspect = canvas.width / canvas.height;

//     const projectionMatrix = mat4.perspective(
//       (2 * Math.PI) / 5,
//       aspect,
//       1,
//       2000.0
//     );

//     const upVector = vec3.fromValues(0, 1, 0);
//     const origin = vec3.fromValues(0, 0, 0);
//     const eyePosition = vec3.fromValues(0, 5, -100);

//     const rad = Math.PI * (Date.now() / 5000);
//     const rotation = mat4.rotateY(mat4.translation(origin), rad);
//     vec3.transformMat4(eyePosition, rotation, eyePosition);

//     const viewMatrix = mat4.lookAt(eyePosition, origin, upVector);

//     const viewProjMatrix = mat4.multiply(projectionMatrix, viewMatrix);
//     return viewProjMatrix;
//   }

//   return function doDraw() {
//     // update the uniform buffer
//     {
//       const buffer = new ArrayBuffer(uniformBuffer.size);

//       new Float32Array(buffer).set(getCameraViewProjMatrix());
//       new Uint32Array(buffer, 16 * Float32Array.BYTES_PER_ELEMENT).set([
//         averageLayersPerFragment * canvas.width * sliceHeight,
//         canvas.width,
//       ]);

//       device.queue.writeBuffer(uniformBuffer, 0, buffer);
//     }

//     const commandEncoder = device.createCommandEncoder();
//     const textureView = context.getCurrentTexture().createView();

//     // Draw the opaque objects
//     opaquePassDescriptor.colorAttachments[0].view = textureView;
//     const opaquePassEncoder =
//       commandEncoder.beginRenderPass(opaquePassDescriptor);
//     opaquePassEncoder.setPipeline(opaquePipeline);
//     opaquePassEncoder.setBindGroup(0, opaqueBindGroup);
//     opaquePassEncoder.setVertexBuffer(0, vertexBuffer);
//     opaquePassEncoder.setIndexBuffer(indexBuffer, 'uint16');
//     opaquePassEncoder.drawIndexed(mesh.triangles.length * 3, 8);
//     opaquePassEncoder.end();

//     for (let slice = 0; slice < numSlices; ++slice) {
//       // initialize the heads buffer
//       commandEncoder.copyBufferToBuffer(headsInitBuffer, headsBuffer);

//       const scissorX = 0;
//       const scissorY = slice * sliceHeight;
//       const scissorWidth = canvas.width;
//       const scissorHeight =
//         Math.min((slice + 1) * sliceHeight, canvas.height) -
//         slice * sliceHeight;

//       // Draw the translucent objects
//       translucentPassDescriptor.colorAttachments[0].view = textureView;
//       const translucentPassEncoder = commandEncoder.beginRenderPass(
//         translucentPassDescriptor
//       );

//       // Set the scissor to only process a horizontal slice of the frame
//       translucentPassEncoder.setScissorRect(
//         scissorX,
//         scissorY,
//         scissorWidth,
//         scissorHeight
//       );

//       translucentPassEncoder.setPipeline(translucentPipeline);
//       translucentPassEncoder.setBindGroup(0, translucentBindGroup, [
//         slice * device.limits.minUniformBufferOffsetAlignment,
//       ]);
//       translucentPassEncoder.setVertexBuffer(0, vertexBuffer);
//       translucentPassEncoder.setIndexBuffer(indexBuffer, 'uint16');
//       translucentPassEncoder.drawIndexed(mesh.triangles.length * 3, 8);
//       translucentPassEncoder.end();

//       // Composite the opaque and translucent objects
//       compositePassDescriptor.colorAttachments[0].view = textureView;
//       const compositePassEncoder = commandEncoder.beginRenderPass(
//         compositePassDescriptor
//       );

//       // Set the scissor to only process a horizontal slice of the frame
//       compositePassEncoder.setScissorRect(
//         scissorX,
//         scissorY,
//         scissorWidth,
//         scissorHeight
//       );

//       compositePassEncoder.setPipeline(compositePipeline);
//       compositePassEncoder.setBindGroup(0, compositeBindGroup, [
//         slice * device.limits.minUniformBufferOffsetAlignment,
//       ]);
//       compositePassEncoder.draw(6);
//       compositePassEncoder.end();
//     }

//     device.queue.submit([commandEncoder.finish()]);
//   };
// };

// let doDraw = configure();

// const updateSettings = () => {
//   doDraw = configure();
// };

// const gui = new GUI();
// gui
//   .add(settings, 'memoryStrategy', ['multipass', 'clamp-pixel-ratio'])
//   .onFinishChange(updateSettings);

// function frame() {
//   doDraw();

//   requestAnimationFrame(frame);
// }

// requestAnimationFrame(frame);
