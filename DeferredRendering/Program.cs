using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

const int MAX_NUM_LIGHTS = 1024;
const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = (float)WIDTH / HEIGHT;


var lightExtentMin = new Vector3(-50, -30, -50);
var lightExtentMax = new Vector3(50, 50, 50);

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

var asm = Assembly.GetExecutingAssembly();
var fragmentDeferredRenderingWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.fragmentDeferredRendering.wgsl")!);
var fragmentGBuffersDebugViewWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.fragmentGBuffersDebugView.wgsl")!);
var fragmentWriteGBuffersWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.fragmentWriteGBuffers.wgsl")!);
var lightUpdateWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.lightUpdate.wgsl")!);
var vertexTextureQuadWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.vertexTextureQuad.wgsl")!);
var vertexWriteGBuffersWGSL = ToBytes(asm.GetManifestResourceStream("DeferredRendering.shaders.vertexWriteGBuffers.wgsl")!);
var mesh = await StanfordDragon.LoadMeshAsync();

return Run("Deferred Rendering", WIDTH, HEIGHT, async runContext =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var adapter = await instance.RequestAdapterAsync(new RequestAdapterOptions
    {
        CompatibleSurface = surface,
        FeatureLevel = FeatureLevel.Compatibility,
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
    });
    var queue = device.GetQueue();
    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

    surface.Configure(new SurfaceConfiguration
    {
        Width = WIDTH,
        Height = HEIGHT,
        Usage = TextureUsage.RenderAttachment,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Auto,
    });

    Debug.Assert(
        mesh.Positions.Length == mesh.Normals.Length &&
        mesh.Positions.Length == mesh.Uvs.Length
    );

    // Create the model vertex buffer.
    const int vertexStride = 8;
    var vertexBuffer = device.CreateBuffer(new BufferDescriptor
    {
        Label = "model vertex buffer",
        // position: vec3, normal: vec3, uv: vec2
        Size = (ulong)(mesh.Positions.Length * Unsafe.SizeOf<(Vector3, Vector3, Vector2)>()),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    });
    vertexBuffer.GetMappedRange<(Vector3, Vector3, Vector2)>(data =>
    {
        for (int i = 0; i < mesh.Positions.Length; ++i)
        {
            data[i] = (mesh.Positions[i], mesh.Normals[i], mesh.Uvs[i]);
        }
    });
    vertexBuffer.Unmap();

    // Create the model index buffer.
    var indexCount = mesh.Triangles.Length * 3;
    var indexBuffer = device.CreateBuffer(new()
    {
        Label = "model index buffer",
        Size = (ulong)(indexCount * sizeof(ushort)),
        Usage = BufferUsage.Index,
        MappedAtCreation = true,
    });
    indexBuffer.GetMappedRange<ushort>(data =>
    {
        for (int i = 0; i < mesh.Triangles.Length; ++i)
        {
            data[3 * i + 0] = (ushort)mesh.Triangles[i].X;
            data[3 * i + 1] = (ushort)mesh.Triangles[i].Y;
            data[3 * i + 2] = (ushort)mesh.Triangles[i].Z;
        }
    });
    indexBuffer.Unmap();


    // GBuffer texture render targets
    var gBufferTexture2DFloat16 = device.CreateTexture(new()
    {
        Size = new() { Width = WIDTH, Height = HEIGHT },
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
        Format = TextureFormat.RGBA16Float
    });

    var gBufferTextureAlbedo = device.CreateTexture(new()
    {
        Size = new() { Width = WIDTH, Height = HEIGHT },
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
        Format = TextureFormat.BGRA8Unorm
    });

    var depthTexture = device.CreateTexture(new()
    {
        Size = new() { Width = WIDTH, Height = HEIGHT },
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding
    });


    TextureView[] gBufferTextureViews =
    [
        gBufferTexture2DFloat16.CreateView(new() { Label = "gbuffer texture normal" }),
        gBufferTextureAlbedo.CreateView(new() { Label = "gbuffer texture albedo" }),
        depthTexture.CreateView(new() { Label = "depth normal" }),
    ];

    VertexBufferLayout[] vertexBuffers =
    [
        new()
        {
            ArrayStride = (uint)Unsafe.SizeOf<(Vector3, Vector3, Vector2)>(),
            Attributes =
            [
                new()
                {
                    // position
                    ShaderLocation = 0,
                    Offset = 0,
                    Format = VertexFormat.Float32x3,
                },
                new()
                {
                    // normal
                    ShaderLocation = 1,
                    Offset = (uint)Marshal.OffsetOf<(Vector3, Vector3, Vector2)>(nameof(ValueTuple<Vector3, Vector3, Vector2>.Item2)),
                    Format = VertexFormat.Float32x3,
                },
                new()
                {
                    // uv
                    ShaderLocation = 2,
                    Offset = (uint)Marshal.OffsetOf<(Vector3, Vector3, Vector2)>(nameof(ValueTuple<Vector3, Vector3, Vector2>.Item3)),
                    Format = VertexFormat.Float32x2,
                },
            ],
        },
    ];

    PrimitiveState primitive = new()
    {
        Topology = PrimitiveTopology.TriangleList,
        CullMode = CullMode.Back,
    };

    var writeGBuffersPipeline = device.CreateRenderPipeline(new()
    {
        Label = "write gbuffers",
        Layout = null!,
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexWriteGBuffersWGSL,
            }),
            Buffers = vertexBuffers,
        }),
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentWriteGBuffersWGSL,
            }),
            Targets =
            [
                // normal
                new() { Format = TextureFormat.RGBA16Float },
                // albedo
                new() { Format = TextureFormat.BGRA8Unorm },
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

    var gBufferTexturesBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
            new()
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
        ],
    });

    var lightTexturesBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
            new()
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.UnfilterableFloat,
                },
            },
        ],
    });

    var gBuffersDebugViewPipeline = device.CreateRenderPipeline(new()
    {
        Label = "debug view",
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts = [gBufferTexturesBindGroupLayout],
        }),
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexTextureQuadWGSL,
            }),
        }),
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentGBuffersDebugViewWGSL,
            }),
            Targets =
            [
                new() { Format = surfaceFormat },
            ],
            Constants =
            [
                new( "canvasSizeWidth", WIDTH ),
                new( "canvasSizeHeight", HEIGHT ),
            ],
        },
        Primitive = primitive,
    });

    var deferredRenderPipeline = device.CreateRenderPipeline(new()
    {
        Label = "deferred final",
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts =
            [
                gBufferTexturesBindGroupLayout,
                lightTexturesBindGroupLayout,
            ],
        }),
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexTextureQuadWGSL,
            }),
        }),
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentDeferredRenderingWGSL,
            }),
            Targets =
            [
                new() { Format = surfaceFormat },
            ],
        },
        Primitive = primitive,
    });

    var writeGBufferPassDescriptor = new RenderPassDescriptor
    {
        ColorAttachments =
        [
            new()
            {
                View = gBufferTextureViews[0],

                ClearValue = new Color(0.0f, 0.0f, 1.0f, 1.0f),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
            new()
            {
                View = gBufferTextureViews[1],

                ClearValue = new Color(0, 0, 0, 1),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
        ],
        DepthStencilAttachment = new()
        {
            View = gBufferTextureViews[2],

            DepthClearValue = 1.0f,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
        },
    };

    var textureQuadPassDescriptor = new RenderPassDescriptor
    {
        ColorAttachments =
        [
            new()
            {
                // view is acquired and set in render loop.
                View = null!,

                ClearValue = new Color(0, 0, 0, 1),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            },
        ],
    };

    var settings = new Settings();

    WebGpuSharp.Buffer ConfigUniformBuffer()
    {
        var buffer = device.CreateBuffer(new()
        {
            Label = "config uniforms",
            Size = sizeof(uint),
            MappedAtCreation = true,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        });
        buffer.GetMappedRange<UInt32>(data =>
        {
            data[0] = (uint)settings.NumLights;
        });
        buffer.Unmap();
        return buffer;
    }

    var modelUniformBuffer = device.CreateBuffer(new()
    {
        Label = "model matrix uniform",
        Size = (ulong)(Unsafe.SizeOf<Matrix4x4>() * 2), // two 4x4 matrix
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var cameraUniformBuffer = device.CreateBuffer(new()
    {
        Label = "camera matrix uniform",
        Size = (ulong)(Unsafe.SizeOf<Matrix4x4>() * 2), // two 4x4 matrix
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var sceneUniformBindGroup = device.CreateBindGroup(new()
    {
        Layout = writeGBuffersPipeline.GetBindGroupLayout(0),
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = modelUniformBuffer
            },
            new()
            {
                Binding = 1,
                Buffer = cameraUniformBuffer
            },
        ],
    });

    var gBufferTexturesBindGroup = device.CreateBindGroup(new()
    {
        Layout = gBufferTexturesBindGroupLayout,
        Entries =
        [
            new()
            {
                Binding = 0,
                TextureView = gBufferTextureViews[0],
            },
            new()
            {
                Binding = 1,
                TextureView = gBufferTextureViews[1],
            },
            new()
            {
                Binding = 2,
                TextureView = gBufferTextureViews[2],
            },
        ],
    });

    // Lights data are uploaded in a storage buffer
    // which could be updated/culled/etc. with a compute shader
    var extent = lightExtentMax - lightExtentMin;
    const int lightDataStride = 8;
    var bufferSizeInByte =
        (ulong)(sizeof(float) * lightDataStride * MAX_NUM_LIGHTS);

    var lightsBuffer = device.CreateBuffer(new()
    {
        Label = "lights storage",
        Size = bufferSizeInByte,
        Usage = BufferUsage.Storage,
        MappedAtCreation = true,
    });

    // We randomaly populate lights randomly in a box range
    // And simply move them along y-axis per frame to show they are
    // dynamic lightings
    lightsBuffer.GetMappedRange<float>(lightData =>
    {
        var random = new Random();
        Span<float> tmpVec4 = stackalloc float[4];
        int offset;
        for (int i = 0; i < MAX_NUM_LIGHTS; i++)
        {
            offset = lightDataStride * i;
            // position
            for (int j = 0; j < 3; j++)
            {
                tmpVec4[j] = (float)(random.NextDouble() * extent[j] + lightExtentMin[j]);
            }
            tmpVec4[3] = 1;
            tmpVec4.CopyTo(lightData.Slice(offset));
            // color
            tmpVec4[0] = (float)(random.NextDouble() * 2);
            tmpVec4[1] = (float)(random.NextDouble() * 2);
            tmpVec4[2] = (float)(random.NextDouble() * 2);
            // radius
            tmpVec4[3] = 20.0f;
            tmpVec4.CopyTo(lightData.Slice(offset + 4));
        }
    });

});

enum RenderMode
{
    Rendering,
    GBuffersView,
}

class Settings
{
    public RenderMode Mode { get; set; } = RenderMode.Rendering;
    public int NumLights { get; set; } = 128;
}


// import { mat4, vec3, vec4 } from 'wgpu-matrix';
// import { GUI } from 'dat.gui';
// import { mesh } from '../../meshes/stanfordDragon';

// import lightUpdate from './lightUpdate.wgsl';
// import vertexWriteGBuffers from './vertexWriteGBuffers.wgsl';
// import fragmentWriteGBuffers from './fragmentWriteGBuffers.wgsl';
// import vertexTextureQuad from './vertexTextureQuad.wgsl';
// import fragmentGBuffersDebugView from './fragmentGBuffersDebugView.wgsl';
// import fragmentDeferredRendering from './fragmentDeferredRendering.wgsl';
// import { quitIfWebGPUNotAvailable, quitIfLimitLessThan } from '../util';

// const kMaxNumLights = 1024;
// const lightExtentMin = vec3.fromValues(-50, -30, -50);
// const lightExtentMax = vec3.fromValues(50, 50, 50);

// const canvas = document.querySelector('canvas') as HTMLCanvasElement;
// const adapter = await navigator.gpu?.requestAdapter({
//   featureLevel: 'compatibility',
// });
// const limits: Record<string, GPUSize32> = {};
// quitIfLimitLessThan(adapter, 'maxStorageBuffersInFragmentStage', 1, limits);
// const device = await adapter?.requestDevice({
//   requiredLimits: limits,
// });
// quitIfWebGPUNotAvailable(adapter, device);

// const context = canvas.getContext('webgpu');

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
// const kVertexStride = 8;
// const vertexBuffer = device.createBuffer({
//   label: 'model vertex buffer',
//   // position: vec3, normal: vec3, uv: vec2
//   size: mesh.positions.length * kVertexStride * Float32Array.BYTES_PER_ELEMENT,
//   usage: GPUBufferUsage.VERTEX,
//   mappedAtCreation: true,
// });
// {
//   const mapping = new Float32Array(vertexBuffer.getMappedRange());
//   for (let i = 0; i < mesh.positions.length; ++i) {
//     mapping.set(mesh.positions[i], kVertexStride * i);
//     mapping.set(mesh.normals[i], kVertexStride * i + 3);
//     mapping.set(mesh.uvs[i], kVertexStride * i + 6);
//   }
//   vertexBuffer.unmap();
// }

// // Create the model index buffer.
// const indexCount = mesh.triangles.length * 3;
// const indexBuffer = device.createBuffer({
//   label: 'model index buffer',
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

// // GBuffer texture render targets
// const gBufferTexture2DFloat16 = device.createTexture({
//   size: [canvas.width, canvas.height],
//   usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
//   format: 'rgba16float',
// });
// const gBufferTextureAlbedo = device.createTexture({
//   size: [canvas.width, canvas.height],
//   usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
//   format: 'bgra8unorm',
// });
// const depthTexture = device.createTexture({
//   size: [canvas.width, canvas.height],
//   format: 'depth24plus',
//   usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
// });

// const gBufferTextureViews = [
//   gBufferTexture2DFloat16.createView({ label: 'gbuffer texture normal' }),
//   gBufferTextureAlbedo.createView({ label: 'gbuffer texture albedo' }),
//   depthTexture.createView({ label: 'depth normal' }),
// ];

// const vertexBuffers: Iterable<GPUVertexBufferLayout> = [
//   {
//     arrayStride: Float32Array.BYTES_PER_ELEMENT * 8,
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
//       {
//         // uv
//         shaderLocation: 2,
//         offset: Float32Array.BYTES_PER_ELEMENT * 6,
//         format: 'float32x2',
//       },
//     ],
//   },
// ];

// const primitive: GPUPrimitiveState = {
//   topology: 'triangle-list',
//   cullMode: 'back',
// };

// const writeGBuffersPipeline = device.createRenderPipeline({
//   label: 'write gbuffers',
//   layout: 'auto',
//   vertex: {
//     module: device.createShaderModule({
//       code: vertexWriteGBuffers,
//     }),
//     buffers: vertexBuffers,
//   },
//   fragment: {
//     module: device.createShaderModule({
//       code: fragmentWriteGBuffers,
//     }),
//     targets: [
//       // normal
//       { format: 'rgba16float' },
//       // albedo
//       { format: 'bgra8unorm' },
//     ],
//   },
//   depthStencil: {
//     depthWriteEnabled: true,
//     depthCompare: 'less',
//     format: 'depth24plus',
//   },
//   primitive,
// });

// const gBufferTexturesBindGroupLayout = device.createBindGroupLayout({
//   entries: [
//     {
//       binding: 0,
//       visibility: GPUShaderStage.FRAGMENT,
//       texture: {
//         sampleType: 'unfilterable-float',
//       },
//     },
//     {
//       binding: 1,
//       visibility: GPUShaderStage.FRAGMENT,
//       texture: {
//         sampleType: 'unfilterable-float',
//       },
//     },
//     {
//       binding: 2,
//       visibility: GPUShaderStage.FRAGMENT,
//       texture: {
//         sampleType: 'unfilterable-float',
//       },
//     },
//   ],
// });

// const lightsBufferBindGroupLayout = device.createBindGroupLayout({
//   entries: [
//     {
//       binding: 0,
//       visibility: GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE,
//       buffer: {
//         type: 'read-only-storage',
//       },
//     },
//     {
//       binding: 1,
//       visibility: GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE,
//       buffer: {
//         type: 'uniform',
//       },
//     },
//     {
//       binding: 2,
//       visibility: GPUShaderStage.FRAGMENT,
//       buffer: {
//         type: 'uniform',
//       },
//     },
//   ],
// });

// const gBuffersDebugViewPipeline = device.createRenderPipeline({
//   label: 'debug view',
//   layout: device.createPipelineLayout({
//     bindGroupLayouts: [gBufferTexturesBindGroupLayout],
//   }),
//   vertex: {
//     module: device.createShaderModule({
//       code: vertexTextureQuad,
//     }),
//   },
//   fragment: {
//     module: device.createShaderModule({
//       code: fragmentGBuffersDebugView,
//     }),
//     targets: [
//       {
//         format: presentationFormat,
//       },
//     ],
//     constants: {
//       canvasSizeWidth: canvas.width,
//       canvasSizeHeight: canvas.height,
//     },
//   },
//   primitive,
// });

// const deferredRenderPipeline = device.createRenderPipeline({
//   label: 'deferred final',
//   layout: device.createPipelineLayout({
//     bindGroupLayouts: [
//       gBufferTexturesBindGroupLayout,
//       lightsBufferBindGroupLayout,
//     ],
//   }),
//   vertex: {
//     module: device.createShaderModule({
//       code: vertexTextureQuad,
//     }),
//   },
//   fragment: {
//     module: device.createShaderModule({
//       code: fragmentDeferredRendering,
//     }),
//     targets: [
//       {
//         format: presentationFormat,
//       },
//     ],
//   },
//   primitive,
// });

// const writeGBufferPassDescriptor: GPURenderPassDescriptor = {
//   colorAttachments: [
//     {
//       view: gBufferTextureViews[0],

//       clearValue: [0.0, 0.0, 1.0, 1.0],
//       loadOp: 'clear',
//       storeOp: 'store',
//     },
//     {
//       view: gBufferTextureViews[1],

//       clearValue: [0, 0, 0, 1],
//       loadOp: 'clear',
//       storeOp: 'store',
//     },
//   ],
//   depthStencilAttachment: {
//     view: gBufferTextureViews[2],

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
//   numLights: 128,
// };
// const configUniformBuffer = (() => {
//   const buffer = device.createBuffer({
//     label: 'config uniforms',
//     size: Uint32Array.BYTES_PER_ELEMENT,
//     mappedAtCreation: true,
//     usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
//   });
//   new Uint32Array(buffer.getMappedRange())[0] = settings.numLights;
//   buffer.unmap();
//   return buffer;
// })();

// const gui = new GUI();
// gui.add(settings, 'mode', ['rendering', 'gBuffers view']);
// gui
//   .add(settings, 'numLights', 1, kMaxNumLights)
//   .step(1)
//   .onChange(() => {
//     device.queue.writeBuffer(
//       configUniformBuffer,
//       0,
//       new Uint32Array([settings.numLights])
//     );
//   });

// const modelUniformBuffer = device.createBuffer({
//   label: 'model matrix uniform',
//   size: 4 * 16 * 2, // two 4x4 matrix
//   usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
// });

// const cameraUniformBuffer = device.createBuffer({
//   label: 'camera matrix uniform',
//   size: 4 * 16 * 2, // two 4x4 matrix
//   usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
// });

// const sceneUniformBindGroup = device.createBindGroup({
//   layout: writeGBuffersPipeline.getBindGroupLayout(0),
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
//         buffer: cameraUniformBuffer,
//       },
//     },
//   ],
// });

// const gBufferTexturesBindGroup = device.createBindGroup({
//   layout: gBufferTexturesBindGroupLayout,
//   entries: [
//     {
//       binding: 0,
//       resource: gBufferTextureViews[0],
//     },
//     {
//       binding: 1,
//       resource: gBufferTextureViews[1],
//     },
//     {
//       binding: 2,
//       resource: gBufferTextureViews[2],
//     },
//   ],
// });

// // Lights data are uploaded in a storage buffer
// // which could be updated/culled/etc. with a compute shader
// const extent = vec3.sub(lightExtentMax, lightExtentMin);
// const lightDataStride = 8;
// const bufferSizeInByte =
//   Float32Array.BYTES_PER_ELEMENT * lightDataStride * kMaxNumLights;
// const lightsBuffer = device.createBuffer({
//   label: 'lights storage',
//   size: bufferSizeInByte,
//   usage: GPUBufferUsage.STORAGE,
//   mappedAtCreation: true,
// });

// // We randomaly populate lights randomly in a box range
// // And simply move them along y-axis per frame to show they are
// // dynamic lightings
// const lightData = new Float32Array(lightsBuffer.getMappedRange());
// const tmpVec4 = vec4.create();
// let offset = 0;
// for (let i = 0; i < kMaxNumLights; i++) {
//   offset = lightDataStride * i;
//   // position
//   for (let i = 0; i < 3; i++) {
//     tmpVec4[i] = Math.random() * extent[i] + lightExtentMin[i];
//   }
//   tmpVec4[3] = 1;
//   lightData.set(tmpVec4, offset);
//   // color
//   tmpVec4[0] = Math.random() * 2;
//   tmpVec4[1] = Math.random() * 2;
//   tmpVec4[2] = Math.random() * 2;
//   // radius
//   tmpVec4[3] = 20.0;
//   lightData.set(tmpVec4, offset + 4);
// }
// lightsBuffer.unmap();

// const lightExtentBuffer = device.createBuffer({
//   label: 'light extent uniform',
//   size: 4 * 8,
//   usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
// });
// const lightExtentData = new Float32Array(8);
// lightExtentData.set(lightExtentMin, 0);
// lightExtentData.set(lightExtentMax, 4);
// device.queue.writeBuffer(
//   lightExtentBuffer,
//   0,
//   lightExtentData.buffer,
//   lightExtentData.byteOffset,
//   lightExtentData.byteLength
// );

// const lightUpdateComputePipeline = device.createComputePipeline({
//   label: 'light update',
//   layout: 'auto',
//   compute: {
//     module: device.createShaderModule({
//       code: lightUpdate,
//     }),
//   },
// });
// const lightsBufferBindGroup = device.createBindGroup({
//   layout: lightsBufferBindGroupLayout,
//   entries: [
//     {
//       binding: 0,
//       resource: {
//         buffer: lightsBuffer,
//       },
//     },
//     {
//       binding: 1,
//       resource: {
//         buffer: configUniformBuffer,
//       },
//     },
//     {
//       binding: 2,
//       resource: {
//         buffer: cameraUniformBuffer,
//       },
//     },
//   ],
// });
// const lightsBufferComputeBindGroup = device.createBindGroup({
//   layout: lightUpdateComputePipeline.getBindGroupLayout(0),
//   entries: [
//     {
//       binding: 0,
//       resource: {
//         buffer: lightsBuffer,
//       },
//     },
//     {
//       binding: 1,
//       resource: {
//         buffer: configUniformBuffer,
//       },
//     },
//     {
//       binding: 2,
//       resource: {
//         buffer: lightExtentBuffer,
//       },
//     },
//   ],
// });
// //--------------------

// // Scene matrices
// const eyePosition = vec3.fromValues(0, 50, -100);
// const upVector = vec3.fromValues(0, 1, 0);
// const origin = vec3.fromValues(0, 0, 0);

// const projectionMatrix = mat4.perspective((2 * Math.PI) / 5, aspect, 1, 2000.0);

// // Move the model so it's centered.
// const modelMatrix = mat4.translation([0, -45, 0]);
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

// // Rotates the camera around the origin based on time.
// function getCameraViewProjMatrix() {
//   const rad = Math.PI * (Date.now() / 5000);
//   const rotation = mat4.rotateY(mat4.translation(origin), rad);
//   const rotatedEyePosition = vec3.transformMat4(eyePosition, rotation);

//   const viewMatrix = mat4.lookAt(rotatedEyePosition, origin, upVector);

//   return mat4.multiply(projectionMatrix, viewMatrix);
// }

// function frame() {
//   const cameraViewProj = getCameraViewProjMatrix();
//   device.queue.writeBuffer(
//     cameraUniformBuffer,
//     0,
//     cameraViewProj.buffer,
//     cameraViewProj.byteOffset,
//     cameraViewProj.byteLength
//   );
//   const cameraInvViewProj = mat4.invert(cameraViewProj);
//   device.queue.writeBuffer(
//     cameraUniformBuffer,
//     64,
//     cameraInvViewProj.buffer,
//     cameraInvViewProj.byteOffset,
//     cameraInvViewProj.byteLength
//   );

//   const commandEncoder = device.createCommandEncoder();
//   {
//     // Write position, normal, albedo etc. data to gBuffers
//     const gBufferPass = commandEncoder.beginRenderPass(
//       writeGBufferPassDescriptor
//     );
//     gBufferPass.setPipeline(writeGBuffersPipeline);
//     gBufferPass.setBindGroup(0, sceneUniformBindGroup);
//     gBufferPass.setVertexBuffer(0, vertexBuffer);
//     gBufferPass.setIndexBuffer(indexBuffer, 'uint16');
//     gBufferPass.drawIndexed(indexCount);
//     gBufferPass.end();
//   }
//   {
//     // Update lights position
//     const lightPass = commandEncoder.beginComputePass();
//     lightPass.setPipeline(lightUpdateComputePipeline);
//     lightPass.setBindGroup(0, lightsBufferComputeBindGroup);
//     lightPass.dispatchWorkgroups(Math.ceil(kMaxNumLights / 64));
//     lightPass.end();
//   }
//   {
//     if (settings.mode === 'gBuffers view') {
//       // GBuffers debug view
//       // Left: depth
//       // Middle: normal
//       // Right: albedo (use uv to mimic a checkerboard texture)
//       textureQuadPassDescriptor.colorAttachments[0].view = context
//         .getCurrentTexture()
//         .createView();
//       const debugViewPass = commandEncoder.beginRenderPass(
//         textureQuadPassDescriptor
//       );
//       debugViewPass.setPipeline(gBuffersDebugViewPipeline);
//       debugViewPass.setBindGroup(0, gBufferTexturesBindGroup);
//       debugViewPass.draw(6);
//       debugViewPass.end();
//     } else {
//       // Deferred rendering
//       textureQuadPassDescriptor.colorAttachments[0].view = context
//         .getCurrentTexture()
//         .createView();
//       const deferredRenderingPass = commandEncoder.beginRenderPass(
//         textureQuadPassDescriptor
//       );
//       deferredRenderingPass.setPipeline(deferredRenderPipeline);
//       deferredRenderingPass.setBindGroup(0, gBufferTexturesBindGroup);
//       deferredRenderingPass.setBindGroup(1, lightsBufferBindGroup);
//       deferredRenderingPass.draw(6);
//       deferredRenderingPass.end();
//     }
//   }
//   device.queue.submit([commandEncoder.finish()]);

//   requestAnimationFrame(frame);
// }
// requestAnimationFrame(frame);
