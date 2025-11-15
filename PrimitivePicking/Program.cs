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

const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = (float)WIDTH / HEIGHT;

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

var asm = Assembly.GetExecutingAssembly();
var computePickPrimitive = ToBytes(asm.GetManifestResourceStream("PrimitivePicking.shaders.computePickPrimitive.wgsl")!);
var vertexForwardRendering = ToBytes(asm.GetManifestResourceStream("PrimitivePicking.shaders.vertexForwardRendering.wgsl")!);
var fragmentForwardRendering = ToBytes(asm.GetManifestResourceStream("PrimitivePicking.shaders.fragmentForwardRendering.wgsl")!);
var vertexTextureQuad = ToBytes(asm.GetManifestResourceStream("PrimitivePicking.shaders.vertexTextureQuad.wgsl")!);
var fragmentPrimitivesDebugView = ToBytes(asm.GetManifestResourceStream("PrimitivePicking.shaders.fragmentPrimitivesDebugView.wgsl")!);
var teapotMesh = await Teapot.LoadMeshAsync();
var startTimeStamp = Stopwatch.GetTimestamp();
var settings = new Settings();


CommandBuffer DrawGui(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(340, 0));
    ImGui.SetNextWindowSize(new(300, 80));
    ImGui.Begin("Primitive Picking",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize
    );

    ImGuiUtils.EnumDropdown("Mode", ref settings.Mode);
    ImGui.Checkbox("Rotate", ref settings.Rotate);

    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}


return Run("Primitive Picking", WIDTH, HEIGHT, async runContext =>
{
    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();
    var input = runContext.Input;

    var adapter = await instance.RequestAdapterAsync(new()
    {
        PowerPreference = PowerPreference.HighPerformance,
        CompatibleSurface = surface,
    });

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

        RequiredFeatures = [FeatureName.PrimitiveIndex],
    });

    var queue = device.GetQueue();

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

    // Create the model vertex buffer.
    var vertexBuffer = device.CreateBuffer(new()
    {
        // position: vec3, normal: vec3
        Size = (ulong)(teapotMesh.Positions.Length * Unsafe.SizeOf<VertexArgs>()),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    });
    {
        Debug.Assert(teapotMesh.Positions.Length == teapotMesh.Normals.Length);
        vertexBuffer.GetMappedRange<VertexArgs>(data =>
        {
            for (int i = 0; i < teapotMesh.Positions.Length; i++)
            {
                data[i] = new()
                {
                    Position = teapotMesh.Positions[i],
                    Normal = teapotMesh.Normals[i],
                };
            }
        });
        vertexBuffer.Unmap();
    }




    var indexCount = teapotMesh.Triangles.Length * 3;
    // Create the model index buffer.
    var indexBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(indexCount * Unsafe.SizeOf<IndexData>()),
        Usage = BufferUsage.Index,
        MappedAtCreation = true,
    });
    {
        indexBuffer.GetMappedRange<IndexData>(data =>
        {
            for (int i = 0; i < teapotMesh.Triangles.Length; i++)
            {
                var (a, b, c) = teapotMesh.Triangles[i];
                data[i] = new()
                {
                    X = (ushort)a,
                    Y = (ushort)b,
                    Z = (ushort)c,
                };
            }
        });
        indexBuffer.Unmap();
    }


    // Render targets

    // The primitive index for each triangle will be written out to this texture.
    // Using a r32uint texture ensures we can store the full range of primitive indices.
    var primitiveIndexTexture = device.CreateTexture(new()
    {
        Size = new(WIDTH, HEIGHT),
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
        Format = TextureFormat.R32Uint,
    });
    var depthTexture = device.CreateTexture(new()
    {
        Size = new(WIDTH, HEIGHT),
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
    });

    var vertexBuffers = new VertexBufferLayout[]
    {
        new()
        {
            ArrayStride = (uint)Unsafe.SizeOf<VertexArgs>(),
            Attributes = [
                new()
                {
                    // position
                    ShaderLocation = 0,
                    Offset = (uint)Marshal.OffsetOf<VertexArgs>(nameof(VertexArgs.Position)),
                    Format = VertexFormat.Float32x3,
                },
                new()
                {
                    // normal
                    ShaderLocation = 1,
                    Offset = (uint)Marshal.OffsetOf<VertexArgs>(nameof(VertexArgs.Normal)),
                    Format = VertexFormat.Float32x3,
                },
            ],
        },
    };

    var primitive = new PrimitiveState
    {
        Topology = PrimitiveTopology.TriangleList,
        // Using `none` because the teapot has gaps that you can see the backfaces through.
        CullMode = CullMode.None,
    };

    var forwardRenderingPipeline = device.CreateRenderPipeline(new()
    {
        Layout = null, // Auto-layout
        Vertex = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexForwardRendering,
            }),
            Buffers = vertexBuffers,
        },
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentForwardRendering,
            }),
            Targets = [
                // color
                new()
                {
                    Format = surfaceFormat,
                },
                // primitive-id
                new()
                {
                    Format = TextureFormat.R32Uint,
                },
            ],
        },
        DepthStencil = new()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth24Plus,
        },
        Primitive = primitive,
    });


    var primitiveTextureBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries = [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.Uint,
                },
            },
        ],
    });


    var primitivesDebugViewPipeline = device.CreateRenderPipeline(new()
    {
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts = [primitiveTextureBindGroupLayout],
        }),
        Vertex = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexTextureQuad,
            }),
        },
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentPrimitivesDebugView,
            }),
            Targets = [
                new()
                {
                    Format = surfaceFormat,
                },
            ],
        },
        Primitive = primitive,
    });

var pickBindGroupLayout = device.CreateBindGroupLayout(new()
{
    Entries = [
        new()
            {
                Binding = 0,
                Visibility = ShaderStage.Compute,
                Buffer = new() { Type = BufferBindingType.Storage },
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Compute,
                Texture = new()
                {
                    SampleType = TextureSampleType.Uint,
                },
            },
        ],
});


var pickPipeline = device.CreateComputePipeline(new()
{
    Layout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = [pickBindGroupLayout],
    }),
    Compute = new()
    {
        Module = device.CreateShaderModuleWGSL(new()
        {
            Code = computePickPrimitive,
        }),
    },
});

var forwardRenderPassDescriptor = new RenderPassDescriptor
{
    ColorAttachments = [
        new()
            {
                // view is acquired and set in render loop.
                View = null,

                ClearValue = new Color(0.0f, 0.0f, 1.0f, 1.0f),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
            new()
            {
                View = primitiveIndexTexture.CreateView(),

                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
        ],
    DepthStencilAttachment = new()
    {
        View = depthTexture.CreateView(),

        DepthClearValue = 1.0f,
        DepthLoadOp = LoadOp.Clear,
        DepthStoreOp = StoreOp.Store,
    },
};

var textureQuadPassDescriptor = new RenderPassDescriptor
{
    ColorAttachments = [
        new()
            {
                // view is acquired and set in render loop.
                View = null,

                ClearValue = new Color(0, 0, 0, 1),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
        ],
};


var modelUniformBuffer = device.CreateBuffer(new()
{
    Size = (ulong)Unsafe.SizeOf<UniformFrameData>(),
    Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
});

var frameUniformBuffer = device.CreateBuffer(new()
{
    Size = (ulong)Unsafe.SizeOf<FullFrame>(),
    Usage = BufferUsage.Uniform | BufferUsage.CopyDst | BufferUsage.Storage,
});

var sceneUniformBindGroup = device.CreateBindGroup(new()
{
    Layout = forwardRenderingPipeline.GetBindGroupLayout(0),
    Entries = [
        new()
            {
                Binding = 0,
                Buffer =  modelUniformBuffer
            },
            new()
            {
                Binding = 1,
                Buffer = frameUniformBuffer
            },
        ],
});


var primitiveTextureBindGroup = device.CreateBindGroup(new()
{
    Layout = primitiveTextureBindGroupLayout,
    Entries = [
        new()
            {
                Binding = 0,
                TextureView = primitiveIndexTexture.CreateView(),
            },
        ],
});

var pickBindGroup = device.CreateBindGroup(new()
{
    Layout = pickBindGroupLayout,
    Entries = [
        new()
            {
                Binding = 0,
                Buffer = frameUniformBuffer
            },
            new()
            {
                Binding = 1,
                TextureView = primitiveIndexTexture.CreateView(),
            },
        ],
});

var eyePosition = new Vector3(0, 12, -25);
var upVector = Vector3.UnitY;
var origin = Vector3.Zero;

var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
    fieldOfView: (float)(2 * Math.PI) / 5,
    aspectRatio: ASPECT,
    nearPlaneDistance: 1,
    farPlaneDistance: 2000.0f
);

// Move the model so it's centered.
var modelMatrix = Matrix4x4.CreateTranslation(0, 0, 0);
var invertTransposeModelMatrix = Matrix4x4.Invert(modelMatrix, out var inverted) ?
    Matrix4x4.Transpose(inverted) :
    throw new InvalidOperationException("Could not invert model matrix");

var normalModelData = invertTransposeModelMatrix;
queue.WriteBuffer(modelUniformBuffer, 0, new Uniform
{
    ModelMatrix = modelMatrix,
    NormalModelMatrix = normalModelData,
});


var pickCoord = new Vector2(0, 0);
input.OnMouseMotion += eventData =>
{
    pickCoord.X = eventData.x;
    pickCoord.Y = eventData.y;
};


float rad = 0;
Matrix4x4 GetCameraViewProjMatrix()
{
    if (settings.Rotate)
    {
        float elapsed = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalMilliseconds / 1000.0f;
        rad = MathF.PI * (elapsed / 10f);
    }
    var rotation = Matrix4x4.CreateTranslation(origin);
    rotation.RotateY(rad);
    var rotatedEyePosition = Vector3.Transform(eyePosition, rotation);
    var viewMatrix = Matrix4x4.CreateLookAt(rotatedEyePosition, origin, upVector);

    return viewMatrix * projectionMatrix;
}


void Frame()
{
    var cameraViewProj = GetCameraViewProjMatrix();
    var cameraInvViewProj = Matrix4x4.Invert(cameraViewProj, out var inv) ?
    inv : throw new InvalidOperationException("Could not invert view projection matrix");

    UniformFrameData frame = new()
    {
        ViewProjectionMatrix = cameraViewProj,
        InvViewProjectionMatrix = cameraInvViewProj,
        PickCoord = pickCoord
    };

    queue.WriteBuffer(frameUniformBuffer, 0, frame);

    var commandEncoder = device.CreateCommandEncoder();
    {
        var forwardRenderPassDescriptor = new RenderPassDescriptor
        {
            ColorAttachments = [
                new()
                    {
                        // view is acquired and set in render loop.
                        View = surface.GetCurrentTexture().Texture!.CreateView(),

                        ClearValue = new(0.0f, 0.0f, 1.0f, 1.0f),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                    },
                    new()
                    {
                        View = primitiveIndexTexture.CreateView(),

                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                    },
                ],
            DepthStencilAttachment = new()
            {
                View = depthTexture.CreateView(),

                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            },
        };

        // Forward rendering pass
        var forwardPass = commandEncoder.BeginRenderPass(forwardRenderPassDescriptor);
        forwardPass.SetPipeline(forwardRenderingPipeline);
        forwardPass.SetBindGroup(0, sceneUniformBindGroup);
        forwardPass.SetVertexBuffer(0, vertexBuffer);
        forwardPass.SetIndexBuffer(indexBuffer, IndexFormat.Uint16);
        forwardPass.DrawIndexed((uint)(indexCount));
        forwardPass.End();
    }
    {
        if (settings.Mode == Settings.ModeType.PrimitiveIndexes)
        {
            var textureQuadPassDescriptor = new RenderPassDescriptor
            {
                ColorAttachments = [
                    new()
                        {
                            // view is acquired and set in render loop.
                            View = surface.GetCurrentTexture().Texture!.CreateView(),

                            ClearValue = new(0, 0, 0, 1),
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                        },
                    ],
            };

            // Primitive Index debug view
            // Overwrites the canvas texture with a visualization of the primitive
            // index for each primitive
            var debugViewPass = commandEncoder.BeginRenderPass(textureQuadPassDescriptor);
            debugViewPass.SetPipeline(primitivesDebugViewPipeline);
            debugViewPass.SetBindGroup(0, primitiveTextureBindGroup);
            debugViewPass.Draw(6);
            debugViewPass.End();
        }
    }
    {
        // Picking pass. Executes a single instance of a compute shader that loads
        // the primitive index at the pointer coordinates from the primitive index
        // texture written in the forward pass. The selected primitive index is
        // saved in the frameUniformBuffer and used for highlighting on the next
        // render. This means that the highlighted primitive is always a frame behind.
        var pickPass = commandEncoder.BeginComputePass();
        pickPass.SetPipeline(pickPipeline);
        pickPass.SetBindGroup(0, pickBindGroup);
        pickPass.DispatchWorkgroups(1);
        pickPass.End();
    }

    var guiCommandBuffer = DrawGui(guiContext, surface);

    queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);
    surface.Present();
}
runContext.OnFrame += Frame;
});


class Settings
{
    public enum ModeType
    {
        Rendering,
        PrimitiveIndexes,
    }

    public ModeType Mode = ModeType.Rendering;
    public bool Rotate = true;
}

struct IndexData
{
    public ushort X;
    public ushort Y;
    public ushort Z;
}

struct VertexArgs
{
    public Vector3 Position;
    public Vector3 Normal;
}

struct Uniform
{
    public Matrix4x4 ModelMatrix;
    public Matrix4x4 NormalModelMatrix;
}

struct UniformFrameData
{
    public Matrix4x4 ViewProjectionMatrix;
    public Matrix4x4 InvViewProjectionMatrix;
    public Vector2 PickCoord;
}

struct FullFrame
{
    public Matrix4x4 ViewProjectionMatrix;
    public Matrix4x4 InvViewProjectionMatrix;
    public Vector2 PickCoord;
    public uint PickedPrimitive;
    private float _padding; // Padding to make the size a multiple of 16 bytes
}

// import { mat4, vec2, vec3 } from 'wgpu-matrix';
// import { GUI } from 'dat.gui';
// import { mesh } from '../../meshes/teapot';

// import computePickPrimitive from './computePickPrimitive.wgsl';
// import vertexForwardRendering from './vertexForwardRendering.wgsl';
// import fragmentForwardRendering from './fragmentForwardRendering.wgsl';
// import vertexTextureQuad from './vertexTextureQuad.wgsl';
// import fragmentPrimitivesDebugView from './fragmentPrimitivesDebugView.wgsl';
// import { quitIfWebGPUNotAvailable, quitIfFeaturesNotAvailable } from '../util';

// const canvas = document.querySelector('canvas') as HTMLCanvasElement;
// const adapter = await navigator.gpu?.requestAdapter({
//   featureLevel: 'compatibility',
// });

// const requiredFeatures: GPUFeatureName[] = ['primitive-index'];
// quitIfFeaturesNotAvailable(adapter, requiredFeatures);

// const device = await adapter.requestDevice({
//   requiredFeatures,
// });
// quitIfWebGPUNotAvailable(adapter, device);

// const context = canvas.getContext('webgpu') as GPUCanvasContext;

// const devicePixelRatio = window.devicePixelRatio;
// canvas.width = canvas.clientWidth * devicePixelRatio;
// canvas.height = canvas.clientHeight * devicePixelRatio;
// const aspect = canvas.width / canvas.height;
// const presentationFormat = navigator.gpu.getPreferredCanvasFormat();
// context.configure({
//   device,
//   format: presentationFormat,
// });

// // Create the model vertex buffer.
// const kVertexStride = 6;
// const vertexBuffer = device.createBuffer({
//   // position: vec3, normal: vec3
//   size: mesh.positions.length * kVertexStride * Float32Array.BYTES_PER_ELEMENT,
//   usage: GPUBufferUsage.VERTEX,
//   mappedAtCreation: true,
// });
// {
//   const mapping = new Float32Array(vertexBuffer.getMappedRange());
//   for (let i = 0; i < mesh.positions.length; ++i) {
//     mapping.set(mesh.positions[i], kVertexStride * i);
//     mapping.set(mesh.normals[i], kVertexStride * i + 3);
//   }
//   vertexBuffer.unmap();
// }

// // Create the model index buffer.
// const indexCount = mesh.triangles.length * 3;
// const indexBuffer = device.createBuffer({
//   size: indexCount * Uint16Array.BYTES_PER_ELEMENT,
//   usage: GPUBufferUsage.INDEX,
//   mappedAtCreation: true,
// });
// {
//   const mapping = new Uint16Array(indexBuffer.getMappedRange());
//   for (let i = 0; i < mesh.triangles.length; ++i) {
//     mapping.set(mesh.triangles[i], 3 * i);
//   }
//   indexBuffer.unmap();
// }

// // Render targets

// // The primitive index for each triangle will be written out to this texture.
// // Using a r32uint texture ensures we can store the full range of primitive indices.
// const primitiveIndexTexture = device.createTexture({
//   size: [canvas.width, canvas.height],
//   usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
//   format: 'r32uint',
// });
// const depthTexture = device.createTexture({
//   size: [canvas.width, canvas.height],
//   format: 'depth24plus',
//   usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
// });

// const vertexBuffers: Iterable<GPUVertexBufferLayout> = [
//   {
//     arrayStride: Float32Array.BYTES_PER_ELEMENT * kVertexStride,
//     attributes: [
//       {
//         // position
//         shaderLocation: 0,
//         offset: 0,
//         format: 'float32x3',
//       },
//       {
//         // normal
//         shaderLocation: 1,
//         offset: Float32Array.BYTES_PER_ELEMENT * 3,
//         format: 'float32x3',
//       },
//     ],
//   },
// ];

// const primitive: GPUPrimitiveState = {
//   topology: 'triangle-list',
//   // Using `none` because the teapot has gaps that you can see the backfaces through.
//   cullMode: 'none',
// };

// const forwardRenderingPipeline = device.createRenderPipeline({
//   layout: 'auto',
//   vertex: {
//     module: device.createShaderModule({
//       code: vertexForwardRendering,
//     }),
//     buffers: vertexBuffers,
//   },
//   fragment: {
//     module: device.createShaderModule({
//       code: fragmentForwardRendering,
//     }),
//     targets: [
//       // color
//       { format: presentationFormat },
//       // primitive-id
//       { format: 'r32uint' },
//     ],
//   },
//   depthStencil: {
//     depthWriteEnabled: true,
//     depthCompare: 'less',
//     format: 'depth24plus',
//   },
//   primitive,
// });

// const primitiveTextureBindGroupLayout = device.createBindGroupLayout({
//   entries: [
//     {
//       binding: 0,
//       visibility: GPUShaderStage.FRAGMENT,
//       texture: {
//         sampleType: 'uint',
//       },
//     },
//   ],
// });

// const primitivesDebugViewPipeline = device.createRenderPipeline({
//   layout: device.createPipelineLayout({
//     bindGroupLayouts: [primitiveTextureBindGroupLayout],
//   }),
//   vertex: {
//     module: device.createShaderModule({
//       code: vertexTextureQuad,
//     }),
//   },
//   fragment: {
//     module: device.createShaderModule({
//       code: fragmentPrimitivesDebugView,
//     }),
//     targets: [
//       {
//         format: presentationFormat,
//       },
//     ],
//   },
//   primitive,
// });

// const pickBindGroupLayout = device.createBindGroupLayout({
//   entries: [
//     {
//       binding: 0,
//       visibility: GPUShaderStage.COMPUTE,
//       buffer: { type: 'storage' },
//     },
//     {
//       binding: 1,
//       visibility: GPUShaderStage.COMPUTE,
//       texture: {
//         sampleType: 'uint',
//       },
//     },
//   ],
// });

// const pickPipeline = device.createComputePipeline({
//   layout: device.createPipelineLayout({
//     bindGroupLayouts: [pickBindGroupLayout],
//   }),
//   compute: {
//     module: device.createShaderModule({
//       code: computePickPrimitive,
//     }),
//   },
// });

// const forwardRenderPassDescriptor: GPURenderPassDescriptor = {
//   colorAttachments: [
//     {
//       // view is acquired and set in render loop.
//       view: undefined,

//       clearValue: [0.0, 0.0, 1.0, 1.0],
//       loadOp: 'clear',
//       storeOp: 'store',
//     },
//     {
//       view: primitiveIndexTexture.createView(),

//       loadOp: 'clear',
//       storeOp: 'store',
//     },
//   ],
//   depthStencilAttachment: {
//     view: depthTexture.createView(),

//     depthClearValue: 1.0,
//     depthLoadOp: 'clear',
//     depthStoreOp: 'store',
//   },
// };

// const textureQuadPassDescriptor: GPURenderPassDescriptor = {
//   colorAttachments: [
//     {
//       // view is acquired and set in render loop.
//       view: undefined,

//       clearValue: [0, 0, 0, 1],
//       loadOp: 'clear',
//       storeOp: 'store',
//     },
//   ],
// };

// const settings = {
//   mode: 'rendering',
//   rotate: true,
// };
// const gui = new GUI();
// gui.add(settings, 'mode', ['rendering', 'primitive indexes']);
// gui.add(settings, 'rotate');

// const kMatrixSizeBytes = Float32Array.BYTES_PER_ELEMENT * 16;
// const kPickUniformsSizeBytes = Float32Array.BYTES_PER_ELEMENT * 4;

// const modelUniformBuffer = device.createBuffer({
//   size: kMatrixSizeBytes * 2, // two 4x4 matrix
//   usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
// });

// const frameUniformBuffer = device.createBuffer({
//   size: kMatrixSizeBytes * 2 + kPickUniformsSizeBytes, // two 4x4 matrix + a vec4's worth of picking uniforms
//   usage:
//     GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST | GPUBufferUsage.STORAGE,
// });

// const sceneUniformBindGroup = device.createBindGroup({
//   layout: forwardRenderingPipeline.getBindGroupLayout(0),
//   entries: [
//     {
//       binding: 0,
//       resource: {
//         buffer: modelUniformBuffer,
//       },
//     },
//     {
//       binding: 1,
//       resource: {
//         buffer: frameUniformBuffer,
//       },
//     },
//   ],
// });

// const primitiveTextureBindGroup = device.createBindGroup({
//   layout: primitiveTextureBindGroupLayout,
//   entries: [
//     {
//       binding: 0,
//       resource: primitiveIndexTexture.createView(),
//     },
//   ],
// });

// const pickBindGroup = device.createBindGroup({
//   layout: pickBindGroupLayout,
//   entries: [
//     {
//       binding: 0,
//       resource: frameUniformBuffer,
//     },
//     {
//       binding: 1,
//       resource: primitiveIndexTexture.createView(),
//     },
//   ],
// });

// //--------------------

// // Scene matrices
// const eyePosition = vec3.fromValues(0, 12, -25);
// const upVector = vec3.fromValues(0, 1, 0);
// const origin = vec3.fromValues(0, 0, 0);

// const projectionMatrix = mat4.perspective((2 * Math.PI) / 5, aspect, 1, 2000.0);

// // Move the model so it's centered.
// const modelMatrix = mat4.translation([0, 0, 0]);
// device.queue.writeBuffer(modelUniformBuffer, 0, modelMatrix);
// const invertTransposeModelMatrix = mat4.invert(modelMatrix);
// mat4.transpose(invertTransposeModelMatrix, invertTransposeModelMatrix);
// const normalModelData = invertTransposeModelMatrix;
// device.queue.writeBuffer(
//   modelUniformBuffer,
//   64,
//   normalModelData.buffer,
//   normalModelData.byteOffset,
//   normalModelData.byteLength
// );

// // Pointer tracking
// const pickCoord = vec2.fromValues(0, 0);
// function onPointerEvent(event: PointerEvent) {
//   // Only track the primary pointer
//   if (event.isPrimary) {
//     const clientRect = (event.target as Element).getBoundingClientRect();
//     // Get the pixel offset from the top-left of the canvas element.
//     pickCoord[0] = (event.clientX - clientRect.x) * devicePixelRatio;
//     pickCoord[1] = (event.clientY - clientRect.y) * devicePixelRatio;
//   }
// }
// canvas.addEventListener('pointerenter', onPointerEvent);
// canvas.addEventListener('pointermove', onPointerEvent);

// // Rotates the camera around the origin based on time.
// let rad = 0;
// function getCameraViewProjMatrix() {
//   if (settings.rotate) {
//     rad = Math.PI * (Date.now() / 10000);
//   }
//   const rotation = mat4.rotateY(mat4.translation(origin), rad);
//   const rotatedEyePosition = vec3.transformMat4(eyePosition, rotation);

//   const viewMatrix = mat4.lookAt(rotatedEyePosition, origin, upVector);

//   return mat4.multiply(projectionMatrix, viewMatrix);
// }

// function frame() {
//   const cameraViewProj = getCameraViewProjMatrix();
//   device.queue.writeBuffer(
//     frameUniformBuffer,
//     0,
//     cameraViewProj.buffer,
//     cameraViewProj.byteOffset,
//     cameraViewProj.byteLength
//   );
//   const cameraInvViewProj = mat4.invert(cameraViewProj);
//   device.queue.writeBuffer(
//     frameUniformBuffer,
//     64,
//     cameraInvViewProj.buffer,
//     cameraInvViewProj.byteOffset,
//     cameraInvViewProj.byteLength
//   );
//   device.queue.writeBuffer(
//     frameUniformBuffer,
//     128,
//     pickCoord.buffer,
//     pickCoord.byteOffset,
//     pickCoord.byteLength
//   );

//   const commandEncoder = device.createCommandEncoder();
//   {
//     // Forward rendering pass
//     forwardRenderPassDescriptor.colorAttachments[0].view = context
//       .getCurrentTexture()
//       .createView();
//     const forwardPass = commandEncoder.beginRenderPass(
//       forwardRenderPassDescriptor
//     );
//     forwardPass.setPipeline(forwardRenderingPipeline);
//     forwardPass.setBindGroup(0, sceneUniformBindGroup);
//     forwardPass.setVertexBuffer(0, vertexBuffer);
//     forwardPass.setIndexBuffer(indexBuffer, 'uint16');
//     forwardPass.drawIndexed(indexCount);
//     forwardPass.end();
//   }
//   {
//     if (settings.mode === 'primitive indexes') {
//       // Primitive Index debug view
//       // Overwrites the canvas texture with a visualization of the primitive
//       // index for each primitive
//       textureQuadPassDescriptor.colorAttachments[0].view = context
//         .getCurrentTexture()
//         .createView();
//       const debugViewPass = commandEncoder.beginRenderPass(
//         textureQuadPassDescriptor
//       );
//       debugViewPass.setPipeline(primitivesDebugViewPipeline);
//       debugViewPass.setBindGroup(0, primitiveTextureBindGroup);
//       debugViewPass.draw(6);
//       debugViewPass.end();
//     }
//   }
//   {
//     // Picking pass. Executes a single instance of a compute shader that loads
//     // the primitive index at the pointer coordinates from the primitive index
//     // texture written in the forward pass. The selected primitive index is
//     // saved in the frameUniformBuffer and used for highlighting on the next
//     // render. This means that the highlighted primitive is always a frame behind.
//     const pickPass = commandEncoder.beginComputePass();
//     pickPass.setPipeline(pickPipeline);
//     pickPass.setBindGroup(0, pickBindGroup);
//     pickPass.dispatchWorkgroups(1);
//     pickPass.end();
//   }
//   device.queue.submit([commandEncoder.finish()]);

//   requestAnimationFrame(frame);
// }
// requestAnimationFrame(frame);
