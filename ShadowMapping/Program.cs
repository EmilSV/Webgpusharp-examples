using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;

const int SHADOW_DEPTH_TEXTURE_SIZE = 1024;
const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = (float)WIDTH / HEIGHT;

static byte[] ToBytes(Stream s)
{
    using MemoryStream ms = new();
    s.CopyTo(ms);
    return ms.ToArray();
}

static T ToUniformBufferSize<T>(T originalSize)
    where T : unmanaged, INumber<T>
{
    return originalSize + (originalSize % T.CreateTruncating(16));
}


var asm = Assembly.GetExecutingAssembly();
using var stream = asm.GetManifestResourceStream("ShadowMapping.assets.stanfordDragonData.bin")!;
var mesh = await SimpleMeshBinReader.LoadData(stream);
var fragmentWGSL = ToBytes(asm.GetManifestResourceStream("ShadowMapping.shaders.fragment.wgsl")!);
var vertexWGSL = ToBytes(asm.GetManifestResourceStream("ShadowMapping.shaders.vertex.wgsl")!);
var vertexShadowWGSL = ToBytes(asm.GetManifestResourceStream("ShadowMapping.shaders.vertexShadow.wgsl")!);

return Run("Shadow Mapping", WIDTH, HEIGHT, async runContext =>
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

    Debug.Assert(mesh.Positions.Length == mesh.Normals.Length);

    // Create the model vertex buffer.
    var vertexBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(mesh.Positions.Length * Unsafe.SizeOf<Vector3>() * 2),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    });

    vertexBuffer.GetMappedRange<Vector3>(data =>
    {
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            data[i * 2 + 0] = mesh.Positions[i];
            data[i * 2 + 1] = mesh.Normals[i];
        }
    });
    vertexBuffer.Unmap();

    // Create the model index buffer.
    var indexCount = mesh.Triangles.Length * 3;
    var indexBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(indexCount * sizeof(ushort)),
        Usage = BufferUsage.Index,
        MappedAtCreation = true,
    });
    indexBuffer.GetMappedRange<ushort>(data =>
    {
        for (int i = 0; i < mesh.Triangles.Length; i++)
        {
            data[i * 3 + 0] = (ushort)mesh.Triangles[i].X;
            data[i * 3 + 1] = (ushort)mesh.Triangles[i].Y;
            data[i * 3 + 2] = (ushort)mesh.Triangles[i].Z;
        }
    });
    indexBuffer.Unmap();

    // Create the depth texture for rendering/sampling the shadow map.
    var shadowDepthTexture = device.CreateTexture(new()
    {
        Size = new Extent3D
        {
            Width = SHADOW_DEPTH_TEXTURE_SIZE,
            Height = SHADOW_DEPTH_TEXTURE_SIZE,
            DepthOrArrayLayers = 1,
        },
        Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
        Format = TextureFormat.Depth32Float,
    });
    var shadowDepthTextureView = shadowDepthTexture.CreateView();


    // Create some common descriptors used for both the shadow pipeline
    // and the color rendering pipeline.
    var vertexBuffers = new VertexBufferLayout[]
    {
        new() {
            ArrayStride = (ulong)(Unsafe.SizeOf<Vector3>() * 2),
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
                    Offset = (ulong)Unsafe.SizeOf<Vector3>(),
                    Format = VertexFormat.Float32x3,
                },
            ],
        },
    };

    var primitive = new PrimitiveState
    {
        Topology = PrimitiveTopology.TriangleList,
        CullMode = CullMode.Back,
    };

    var uniformBufferBindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex,
                Buffer = new BufferBindingLayout
                {
                    Type = BufferBindingType.Uniform,
                },
            },
        ]
    });

    var shadowPipeline = device.CreateRenderPipeline(new()
    {
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts =
            [
                uniformBufferBindGroupLayout,
                uniformBufferBindGroupLayout,
            ],
        }),
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexShadowWGSL,
            }),
            Buffers = vertexBuffers,
        }),
        DepthStencil = new()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth32Float,
        },
        Primitive = primitive,
    });

    // Create a bind group layout which holds the scene uniforms and
    // the texture+sampler for depth. We create it manually because the WebPU
    // implementation doesn't infer this from the shader (yet).
    var bglForRender = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new()
                {
                    Type = BufferBindingType.Uniform,
                },
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Texture = new()
                {
                    SampleType = TextureSampleType.Depth,
                },
            },
            new()
            {
                Binding = 2,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Sampler = new()
                {
                    Type = SamplerBindingType.Comparison,
                },
            },
        ]
    });

    var pipeline = device.CreateRenderPipeline(new()
    {
        Layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts =
            [
                bglForRender,
                uniformBufferBindGroupLayout,
            ],
        }),
        Vertex = ref InlineInit(new VertexState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexWGSL,
            }),
            Buffers = vertexBuffers,
        }),
        Fragment = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = fragmentWGSL,
            }),
            Targets =
            [
                new()
                {
                    Format = surfaceFormat,
                },
            ],
            Constants =
            [
                new("shadowDepthTextureSize", SHADOW_DEPTH_TEXTURE_SIZE),
            ],
        },
        DepthStencil = new()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth24PlusStencil8,
        },
        Primitive = primitive,
    });

    var depthTexture = device.CreateTexture(new()
    {
        Size = new()
        {
            Width = WIDTH,
            Height = HEIGHT
        },
        Format = TextureFormat.Depth24PlusStencil8,
        Usage = TextureUsage.RenderAttachment,
    });
    var depthTextureView = depthTexture.CreateView();

    var modelUniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<Matrix4x4>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var sceneUniformBuffer = device.CreateBuffer(new()
    {
        // Two 4x4 viewProj matrices,
        // one for the camera and one for the light.
        // Then a vec3 for the light position.
        // Rounded to the nearest multiple of 16.
        Size = (uint)ToUniformBufferSize(2 * Unsafe.SizeOf<Matrix4x4>() + Unsafe.SizeOf<Vector3>()),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var sceneBindGroupForShadow = device.CreateBindGroup(new()
    {
        Layout = uniformBufferBindGroupLayout,
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = sceneUniformBuffer,
            }
        ],
    });

    var sceneBindGroupForRender = device.CreateBindGroup(new()
    {
        Layout = bglForRender,
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = sceneUniformBuffer,
            },
            new()
            {
                Binding = 1,
                TextureView = shadowDepthTextureView,
            },
            new()
            {
                Binding = 2,
                Sampler = device.CreateSampler(new()
                {
                    Compare = CompareFunction.Less,
                }),
            },
        ],
    });

    var modelBindGroup = device.CreateBindGroup(new()
    {
        Layout = uniformBufferBindGroupLayout,
        Entries =
        [
            new()
            {
                Binding = 0,
                Buffer = modelUniformBuffer,
            }
        ],
    });

    var eyePosition = new Vector3(0, 50, -100);
    var upVector = new Vector3(0, 1, 0);
    var origin = new Vector3(0, 0, 0);

    var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(2.0f * MathF.PI / 5f, ASPECT, 1, 2000.0f);

    var viewMatrix = Matrix4x4.CreateLookAt(eyePosition, origin, upVector);

    var lightPosition = new Vector3(50, 100, -100);
    var lightViewMatrix = Matrix4x4.CreateLookAt(lightPosition, origin, upVector);
    var lightProjectionMatrix = Matrix4x4.CreateOrthographicOffCenter(
        left: -80,
        right: 80,
        bottom: -80,
        top: 80,
        zNearPlane: -200,
        zFarPlane: 300
    );

    var lightViewProjMatrix = lightViewMatrix * lightProjectionMatrix;

    var viewProjMatrix = viewMatrix * projectionMatrix;

    // Move the model so it's centered.
    var modelMatrix = Matrix4x4.CreateTranslation(0, -45, 0);

    // The camera/light aren't moving, so write them into buffers now.
    {
        queue.WriteBuffer(sceneUniformBuffer, 0, lightViewProjMatrix);
        queue.WriteBuffer(sceneUniformBuffer, (ulong)Unsafe.SizeOf<Matrix4x4>(), lightViewProjMatrix);
        queue.WriteBuffer(sceneUniformBuffer, (ulong)(2 * Unsafe.SizeOf<Matrix4x4>()), lightPosition);
        queue.WriteBuffer(modelUniformBuffer, 0, modelMatrix);
    }

    Matrix4x4 GetCameraViewProjMatrix()
    {
        var eyePosition = new Vector3(0, 50, -100);

        var rad = MathF.PI * (float)(Stopwatch.GetElapsedTime(startTimeStamp).TotalMilliseconds / 2000f);
        var translation = Matrix4x4.CreateTranslation(origin);
        var rotation = translation.RotateY(rad);
        eyePosition = Vector3.Transform(eyePosition, rotation);

        var viewMatrix = Matrix4x4.CreateLookAt(eyePosition, origin, upVector);

        viewProjMatrix = viewMatrix * projectionMatrix;
        return viewProjMatrix;
    }

    runContext.OnFrame += () =>
    {
        var cameraViewProj = GetCameraViewProjMatrix();
        queue.WriteBuffer(
            sceneUniformBuffer,
            (ulong)Unsafe.SizeOf<Matrix4x4>(),
            cameraViewProj
        );

        var renderPassDescriptor = new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new()
                {
                    View = surface.GetCurrentTexture().Texture!.CreateView(),
                    ClearValue = new Color(0.5f, 0.5f, 0.5f, 1.0f),
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                }
            ],
            DepthStencilAttachment = new()
            {
                View = depthTextureView,

                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
                StencilClearValue = 0,
                StencilLoadOp = LoadOp.Clear,
                StencilStoreOp = StoreOp.Store,
            },
        };
        
        var commandEncoder = device.CreateCommandEncoder();
        {
            var shadowPass = commandEncoder.BeginRenderPass(new()
            {
                ColorAttachments = [],
                DepthStencilAttachment = new()
                {
                    View = shadowDepthTextureView,
                    DepthClearValue = 1.0f,
                    DepthLoadOp = LoadOp.Clear,
                    DepthStoreOp = StoreOp.Store,
                },
            });
            shadowPass.SetPipeline(shadowPipeline);
            shadowPass.SetBindGroup(0, sceneBindGroupForShadow);
            shadowPass.SetBindGroup(1, modelBindGroup);
            shadowPass.SetVertexBuffer(0, vertexBuffer);
            shadowPass.SetIndexBuffer(indexBuffer, IndexFormat.Uint16);
            shadowPass.DrawIndexed((uint)indexCount);

            shadowPass.End();
        }
        {
            var renderPass = commandEncoder.BeginRenderPass(renderPassDescriptor);
            renderPass.SetPipeline(pipeline);
            renderPass.SetBindGroup(0, sceneBindGroupForRender);
            renderPass.SetBindGroup(1, modelBindGroup);
            renderPass.SetVertexBuffer(0, vertexBuffer);
            renderPass.SetIndexBuffer(indexBuffer, IndexFormat.Uint16);
            renderPass.DrawIndexed((uint)indexCount);

            renderPass.End();
        }

        queue.Submit([commandEncoder.Finish()]);
        surface.Present();
    };
});

// import { mat4, vec3 } from 'wgpu-matrix';
// import { mesh } from '../../meshes/stanfordDragon';

// import vertexShadowWGSL from './vertexShadow.wgsl';
// import vertexWGSL from './vertex.wgsl';
// import fragmentWGSL from './fragment.wgsl';
// import { quitIfWebGPUNotAvailable } from '../util';

// const shadowDepthTextureSize = 1024;

// const canvas = document.querySelector('canvas') as HTMLCanvasElement;
// const adapter = await navigator.gpu?.requestAdapter({
//   featureLevel: 'compatibility',
// });
// const device = await adapter?.requestDevice();
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
// const vertexBuffer = device.createBuffer({
//   size: mesh.positions.length * 3 * 2 * Float32Array.BYTES_PER_ELEMENT,
//   usage: GPUBufferUsage.VERTEX,
//   mappedAtCreation: true,
// });
// {
//   const mapping = new Float32Array(vertexBuffer.getMappedRange());
//   for (let i = 0; i < mesh.positions.length; ++i) {
//     mapping.set(mesh.positions[i], 6 * i);
//     mapping.set(mesh.normals[i], 6 * i + 3);
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

// // Create the depth texture for rendering/sampling the shadow map.
// const shadowDepthTexture = device.createTexture({
//   size: [shadowDepthTextureSize, shadowDepthTextureSize, 1],
//   usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
//   format: 'depth32float',
// });
// const shadowDepthTextureView = shadowDepthTexture.createView();

// // Create some common descriptors used for both the shadow pipeline
// // and the color rendering pipeline.
// const vertexBuffers: Iterable<GPUVertexBufferLayout> = [
//   {
//     arrayStride: Float32Array.BYTES_PER_ELEMENT * 6,
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
//   cullMode: 'back',
// };

// const uniformBufferBindGroupLayout = device.createBindGroupLayout({
//   entries: [
//     {
//       binding: 0,
//       visibility: GPUShaderStage.VERTEX,
//       buffer: {
//         type: 'uniform',
//       },
//     },
//   ],
// });

// const shadowPipeline = device.createRenderPipeline({
//   layout: device.createPipelineLayout({
//     bindGroupLayouts: [
//       uniformBufferBindGroupLayout,
//       uniformBufferBindGroupLayout,
//     ],
//   }),
//   vertex: {
//     module: device.createShaderModule({
//       code: vertexShadowWGSL,
//     }),
//     buffers: vertexBuffers,
//   },
//   depthStencil: {
//     depthWriteEnabled: true,
//     depthCompare: 'less',
//     format: 'depth32float',
//   },
//   primitive,
// });

// // Create a bind group layout which holds the scene uniforms and
// // the texture+sampler for depth. We create it manually because the WebPU
// // implementation doesn't infer this from the shader (yet).
// const bglForRender = device.createBindGroupLayout({
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
//       visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
//       texture: {
//         sampleType: 'depth',
//       },
//     },
//     {
//       binding: 2,
//       visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
//       sampler: {
//         type: 'comparison',
//       },
//     },
//   ],
// });

// const pipeline = device.createRenderPipeline({
//   layout: device.createPipelineLayout({
//     bindGroupLayouts: [bglForRender, uniformBufferBindGroupLayout],
//   }),
//   vertex: {
//     module: device.createShaderModule({
//       code: vertexWGSL,
//     }),
//     buffers: vertexBuffers,
//   },
//   fragment: {
//     module: device.createShaderModule({
//       code: fragmentWGSL,
//     }),
//     targets: [
//       {
//         format: presentationFormat,
//       },
//     ],
//     constants: {
//       shadowDepthTextureSize,
//     },
//   },
//   depthStencil: {
//     depthWriteEnabled: true,
//     depthCompare: 'less',
//     format: 'depth24plus-stencil8',
//   },
//   primitive,
// });

// const depthTexture = device.createTexture({
//   size: [canvas.width, canvas.height],
//   format: 'depth24plus-stencil8',
//   usage: GPUTextureUsage.RENDER_ATTACHMENT,
// });

// const renderPassDescriptor: GPURenderPassDescriptor = {
//   colorAttachments: [
//     {
//       // view is acquired and set in render loop.
//       view: undefined,

//       clearValue: [0.5, 0.5, 0.5, 1.0],
//       loadOp: 'clear',
//       storeOp: 'store',
//     },
//   ],
//   depthStencilAttachment: {
//     view: depthTexture.createView(),

//     depthClearValue: 1.0,
//     depthLoadOp: 'clear',
//     depthStoreOp: 'store',
//     stencilClearValue: 0,
//     stencilLoadOp: 'clear',
//     stencilStoreOp: 'store',
//   },
// };

// const modelUniformBuffer = device.createBuffer({
//   size: 4 * 16, // 4x4 matrix
//   usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
// });

// const sceneUniformBuffer = device.createBuffer({
//   // Two 4x4 viewProj matrices,
//   // one for the camera and one for the light.
//   // Then a vec3 for the light position.
//   // Rounded to the nearest multiple of 16.
//   size: 2 * 4 * 16 + 4 * 4,
//   usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
// });

// const sceneBindGroupForShadow = device.createBindGroup({
//   layout: uniformBufferBindGroupLayout,
//   entries: [
//     {
//       binding: 0,
//       resource: {
//         buffer: sceneUniformBuffer,
//       },
//     },
//   ],
// });

// const sceneBindGroupForRender = device.createBindGroup({
//   layout: bglForRender,
//   entries: [
//     {
//       binding: 0,
//       resource: {
//         buffer: sceneUniformBuffer,
//       },
//     },
//     {
//       binding: 1,
//       resource: shadowDepthTextureView,
//     },
//     {
//       binding: 2,
//       resource: device.createSampler({
//         compare: 'less',
//       }),
//     },
//   ],
// });

// const modelBindGroup = device.createBindGroup({
//   layout: uniformBufferBindGroupLayout,
//   entries: [
//     {
//       binding: 0,
//       resource: {
//         buffer: modelUniformBuffer,
//       },
//     },
//   ],
// });

// const eyePosition = vec3.fromValues(0, 50, -100);
// const upVector = vec3.fromValues(0, 1, 0);
// const origin = vec3.fromValues(0, 0, 0);

// const projectionMatrix = mat4.perspective((2 * Math.PI) / 5, aspect, 1, 2000.0);

// const viewMatrix = mat4.lookAt(eyePosition, origin, upVector);

// const lightPosition = vec3.fromValues(50, 100, -100);
// const lightViewMatrix = mat4.lookAt(lightPosition, origin, upVector);
// const lightProjectionMatrix = mat4.create();
// {
//   const left = -80;
//   const right = 80;
//   const bottom = -80;
//   const top = 80;
//   const near = -200;
//   const far = 300;
//   mat4.ortho(left, right, bottom, top, near, far, lightProjectionMatrix);
// }

// const lightViewProjMatrix = mat4.multiply(
//   lightProjectionMatrix,
//   lightViewMatrix
// );

// const viewProjMatrix = mat4.multiply(projectionMatrix, viewMatrix);

// // Move the model so it's centered.
// const modelMatrix = mat4.translation([0, -45, 0]);

// // The camera/light aren't moving, so write them into buffers now.
// {
//   device.queue.writeBuffer(sceneUniformBuffer, 0, lightViewProjMatrix);
//   device.queue.writeBuffer(sceneUniformBuffer, 64, lightViewProjMatrix);
//   device.queue.writeBuffer(sceneUniformBuffer, 128, lightPosition);
//   device.queue.writeBuffer(modelUniformBuffer, 0, modelMatrix);
// }

// // Rotates the camera around the origin based on time.
// function getCameraViewProjMatrix() {
//   const eyePosition = vec3.fromValues(0, 50, -100);

//   const rad = Math.PI * (Date.now() / 2000);
//   const rotation = mat4.rotateY(mat4.translation(origin), rad);
//   vec3.transformMat4(eyePosition, rotation, eyePosition);

//   const viewMatrix = mat4.lookAt(eyePosition, origin, upVector);

//   mat4.multiply(projectionMatrix, viewMatrix, viewProjMatrix);
//   return viewProjMatrix;
// }

// const shadowPassDescriptor: GPURenderPassDescriptor = {
//   colorAttachments: [],
//   depthStencilAttachment: {
//     view: shadowDepthTextureView,
//     depthClearValue: 1.0,
//     depthLoadOp: 'clear',
//     depthStoreOp: 'store',
//   },
// };

// function frame() {
//   const cameraViewProj = getCameraViewProjMatrix();
//   device.queue.writeBuffer(
//     sceneUniformBuffer,
//     64,
//     cameraViewProj.buffer,
//     cameraViewProj.byteOffset,
//     cameraViewProj.byteLength
//   );

//   renderPassDescriptor.colorAttachments[0].view = context
//     .getCurrentTexture()
//     .createView();

//   const commandEncoder = device.createCommandEncoder();
//   {
//     const shadowPass = commandEncoder.beginRenderPass(shadowPassDescriptor);
//     shadowPass.setPipeline(shadowPipeline);
//     shadowPass.setBindGroup(0, sceneBindGroupForShadow);
//     shadowPass.setBindGroup(1, modelBindGroup);
//     shadowPass.setVertexBuffer(0, vertexBuffer);
//     shadowPass.setIndexBuffer(indexBuffer, 'uint16');
//     shadowPass.drawIndexed(indexCount);

//     shadowPass.end();
//   }
//   {
//     const renderPass = commandEncoder.beginRenderPass(renderPassDescriptor);
//     renderPass.setPipeline(pipeline);
//     renderPass.setBindGroup(0, sceneBindGroupForRender);
//     renderPass.setBindGroup(1, modelBindGroup);
//     renderPass.setVertexBuffer(0, vertexBuffer);
//     renderPass.setIndexBuffer(indexBuffer, 'uint16');
//     renderPass.drawIndexed(indexCount);

//     renderPass.end();
//   }
//   device.queue.submit([commandEncoder.finish()]);
//   requestAnimationFrame(frame);
// }
// requestAnimationFrame(frame);
