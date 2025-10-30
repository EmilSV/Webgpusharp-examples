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
using static WebGpuSharp.WebGpuUtil;

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
var distanceSizedPointsVertWGSL = ToBytes(asm.GetManifestResourceStream("Points.shaders.distance-sized-points.vert.wgsl")!);
var fixedSizePointsVertWGSL = ToBytes(asm.GetManifestResourceStream("Points.shaders.fixed-size-points.vert.wgsl")!);
var orangeFragWGSL = ToBytes(asm.GetManifestResourceStream("Points.shaders.orange.frag.wgsl")!);
var texturedFragWGSL = ToBytes(asm.GetManifestResourceStream("Points.shaders.textured.frag.wgsl")!);

var butterflyImage = ResourceUtils.LoadImage(asm.GetManifestResourceStream("Points.assets.img.Butterfly.png")!);

var settings = new Settings()
{
    FixedSize = false,
    Textured = false,
    Size = 10.0f
};


static float[] CreateFibonacciSphereVertices(int numSamples, float radius)
{
    float[] vertices = new float[numSamples * 3];
    float increment = MathF.PI * (3 - MathF.Sqrt(5));
    for (int i = 0; i < numSamples; ++i)
    {
        float offset = 2.0f / numSamples;
        float y = i * offset - 1 + offset / 2;
        float r = MathF.Sqrt(1.0f - y * y);
        float phi = i % numSamples * increment;
        float x = MathF.Cos(phi) * r;
        float z = MathF.Sin(phi) * r;
        vertices[i * 3 + 0] = x * radius;
        vertices[i * 3 + 1] = y * radius;
        vertices[i * 3 + 2] = z * radius;
    }
    return vertices;
}


CommandBuffer DrawGui(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(340, 0));
    ImGui.SetNextWindowSize(new(300, 100));
    ImGui.Begin("Points",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize
    );

    ImGui.Checkbox("fixedSize", ref settings.FixedSize);
    ImGui.Checkbox("textured", ref settings.Textured);
    ImGui.SliderFloat("size", ref settings.Size, 0.0f, 80.0f);

    ImGui.End();
    guiContext.EndFrame();
    return guiContext.Render(surface)!.Value!;
}


return Run("Points", WIDTH, HEIGHT, async runContext =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();

    Adapter adapter = await instance.RequestAdapterAsync(new()
    {
        PowerPreference = PowerPreference.HighPerformance,
        CompatibleSurface = surface
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

    var query = device.GetQueue();

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

    // Create a bind group layout so we can share the bind groups
    // with multiple pipelines.
    var bindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries = [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex,
                Buffer = new(),
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Fragment,
                Sampler = new()
            },
            new()
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Texture = new()
            },
        ],
    });

    var pipelineLayout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = [bindGroupLayout],
    });


    // Compile all 4 shaders
    var distanceSizedPointsVertShader = device.CreateShaderModuleWGSL(new()
    {
        Code = distanceSizedPointsVertWGSL
    });

    var fixedSizePointsVertShader = device.CreateShaderModuleWGSL(new()
    {
        Code = fixedSizePointsVertWGSL
    });

    var orangeFragShader = device.CreateShaderModuleWGSL(new()
    {
        Code = orangeFragWGSL
    });

    var texturedFragShader = device.CreateShaderModuleWGSL(new()
    {
        Code = texturedFragWGSL
    });

    const TextureFormat DEPTH_FORMAT = TextureFormat.Depth24Plus;

    // make pipelines for each combination
    ShaderModule[] fragModules = [orangeFragShader, texturedFragShader];
    ShaderModule[] vertModules = [distanceSizedPointsVertShader, fixedSizePointsVertShader];


    var pipelines = vertModules.Select(vertModule =>
        fragModules.Select(fragModule =>
            device.CreateRenderPipeline(new()
            {
                Layout = pipelineLayout,
                Vertex = ref InlineInit(new VertexState()
                {
                    Module = vertModule,
                    Buffers = [
                        new()
                        {
                            ArrayStride = (ulong)Unsafe.SizeOf<Vector3>(),
                            StepMode = VertexStepMode.Instance,
                            Attributes = [
                                new()
                                {
                                    ShaderLocation = 0,
                                    Offset = 0,
                                    Format = VertexFormat.Float32x3,
                                },
                            ],
                        },
                    ],
                }),
                Fragment = new()
                {
                    Module = fragModule,
                    Targets = [
                        new()
                        {
                            Format = surfaceFormat,
                            Blend = new()
                            {
                                Color = new()
                                {
                                    SrcFactor = BlendFactor.One,
                                    DstFactor = BlendFactor.OneMinusSrcAlpha,
                                },
                                Alpha = new()
                                {
                                    SrcFactor = BlendFactor.One,
                                    DstFactor = BlendFactor.OneMinusSrcAlpha,
                                },
                            },
                        },
                    ],
                },
                DepthStencil = new()
                {
                    DepthWriteEnabled = OptionalBool.True,
                    DepthCompare = CompareFunction.Less,
                    Format = DEPTH_FORMAT,
                },
            })
        ).ToArray()
    ).ToArray();

    var vertexData = CreateFibonacciSphereVertices(
        numSamples: 1000,
        radius: 1.0f
    );

    var numberOfPoints = vertexData.Length / 3;

    var vertexBuffer = device.CreateBuffer(new()
    {
        Label = "vertex buffer vertices",
        Size = vertexData.GetSizeInBytes(),
        Usage = BufferUsage.Vertex | BufferUsage.CopyDst
    });
    query.WriteBuffer(vertexBuffer, 0, vertexData);


    var uniformValues = new Uniforms();
    var uniformBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)Unsafe.SizeOf<Uniforms>(),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst
    });


    var sampler = device.CreateSampler();
    var texture = device.CreateTexture(new()
    {
        Size = new(butterflyImage.Width, butterflyImage.Height),
        Format = TextureFormat.RGBA8Unorm,
        Usage =
            TextureUsage.CopyDst |
            TextureUsage.TextureBinding |
            TextureUsage.RenderAttachment,
    });

    ResourceUtils.CopyExternalImageToTexture(
        queue: query,
        source: butterflyImage,
        texture: texture
    );

    var bindGroup = device.CreateBindGroup(new()
    {
        Layout = bindGroupLayout,
        Entries = [
            new()
            {
                Binding = 0,
                Buffer = uniformBuffer,
            },
            new()
            {
                Binding = 1,
                Sampler = sampler,
            },
            new()
            {
                Binding = 2,
                TextureView = texture.CreateView(),
            },
        ],
    });

    Texture? depthTexture = null;

    runContext.OnFrame += () =>
    {
        var time = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;

        // If we don't have a depth texture OR if its size is different
        // from the canvasTexture when make a new depth texture
        if (
            depthTexture == null ||
            depthTexture.GetWidth() != WIDTH ||
            depthTexture.GetHeight() != HEIGHT
        )
        {
            depthTexture?.Destroy();
            depthTexture = device.CreateTexture(new()
            {
                Size = new(WIDTH, HEIGHT),
                Format = DEPTH_FORMAT,
                Usage = TextureUsage.RenderAttachment,
            });
        }

        var renderPassDescriptor = new RenderPassDescriptor()
        {
            Label = "our basic canvas renderPass",
            ColorAttachments = [
                new()
                {
                    View = surface.GetCurrentTexture().Texture!.CreateView(),
                    ClearValue = new(0.3f, 0.3f, 0.3f, 1.0f),
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

        var fixedSize = settings.FixedSize;
        var textured = settings.Textured;

        var pipeline = pipelines[fixedSize ? 1 : 0][textured ? 1 : 0];

        // Set the size in the uniform values
        uniformValues.Size = settings.Size;

        var fov = 90 * MathF.PI / 180;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView: fov,
            aspectRatio: ASPECT,
            nearPlaneDistance: 0.1f,
            farPlaneDistance: 50.0f
        );

        var view = Matrix4x4.CreateLookAt(
            cameraPosition: new(0, 0, 1.5f),
            cameraTarget: Vector3.Zero,
            cameraUpVector: Vector3.UnitY
        );

        var viewProjection = Matrix4x4.Multiply(view, projection);


        viewProjection.RotateY(time);
        viewProjection.RotateX(time * 0.1f);

        uniformValues.Resolution = new Vector2(WIDTH, HEIGHT);
        uniformValues.Matrix = viewProjection;


        // Copy the uniform values to the GPU
        query.WriteBuffer(uniformBuffer, 0, uniformValues);

        var encoder = device.CreateCommandEncoder();
        var pass = encoder.BeginRenderPass(renderPassDescriptor);
        pass.SetPipeline(pipeline);
        pass.SetVertexBuffer(0, vertexBuffer);
        pass.SetBindGroup(0, bindGroup);
        pass.Draw(6, (uint)numberOfPoints);
        pass.End();

        var commandBuffer = encoder.Finish();

        var guiCommandBuffer = DrawGui(guiContext, surface);

        query.Submit([commandBuffer, guiCommandBuffer]);

        surface.Present();

    };

});

struct Uniforms
{
    public Matrix4x4 Matrix;
    public Vector2 Resolution;
    public float Size;
    private float _pad0;
}

class Settings
{
    public bool FixedSize = false;
    public bool Textured = false;
    public float Size = 10.0f;
}


// import { mat4 } from 'wgpu-matrix';
// import { GUI } from 'dat.gui';

// import distanceSizedPointsVertWGSL from './distance-sized-points.vert.wgsl';
// import fixedSizePointsVertWGSL from './fixed-size-points.vert.wgsl';
// import orangeFragWGSL from './orange.frag.wgsl';
// import texturedFragWGSL from './textured.frag.wgsl';
// import { quitIfWebGPUNotAvailable } from '../util';

// // See: https://www.google.com/search?q=fibonacci+sphere
// function createFibonacciSphereVertices({
//   numSamples,
//   radius,
// }: {
//   numSamples: number;
//   radius: number;
// }) {
//   const vertices = [];
//   const increment = Math.PI * (3 - Math.sqrt(5));
//   for (let i = 0; i < numSamples; ++i) {
//     const offset = 2 / numSamples;
//     const y = i * offset - 1 + offset / 2;
//     const r = Math.sqrt(1 - Math.pow(y, 2));
//     const phi = (i % numSamples) * increment;
//     const x = Math.cos(phi) * r;
//     const z = Math.sin(phi) * r;
//     vertices.push(x * radius, y * radius, z * radius);
//   }
//   return new Float32Array(vertices);
// }

// const adapter = await navigator.gpu?.requestAdapter({
//   featureLevel: 'compatibility',
// });
// const device = await adapter?.requestDevice();
// quitIfWebGPUNotAvailable(adapter, device);

// // Get a WebGPU context from the canvas and configure it
// const canvas = document.querySelector('canvas') as HTMLCanvasElement;
// const context = canvas.getContext('webgpu');
// const devicePixelRatio = window.devicePixelRatio;
// canvas.width = canvas.clientWidth * devicePixelRatio;
// canvas.height = canvas.clientHeight * devicePixelRatio;
// const presentationFormat = navigator.gpu.getPreferredCanvasFormat();
// context.configure({
//   device,
//   format: presentationFormat,
// });

// // Create a bind group layout so we can share the bind groups
// // with multiple pipelines.
// const bindGroupLayout = device.createBindGroupLayout({
//   entries: [
//     {
//       binding: 0,
//       visibility: GPUShaderStage.VERTEX,
//       buffer: {},
//     },
//     {
//       binding: 1,
//       visibility: GPUShaderStage.FRAGMENT,
//       sampler: {},
//     },
//     {
//       binding: 2,
//       visibility: GPUShaderStage.FRAGMENT,
//       texture: {},
//     },
//   ],
// });

// const pipelineLayout = device.createPipelineLayout({
//   bindGroupLayouts: [bindGroupLayout],
// });

// // Compile all 4 shaders
// const shaderModules = Object.fromEntries(
//   Object.entries({
//     orangeFragWGSL,
//     texturedFragWGSL,
//     distanceSizedPointsVertWGSL,
//     fixedSizePointsVertWGSL,
//   }).map(([key, code]) => [key, device.createShaderModule({ code })])
// );

// const fragModules = [
//   shaderModules.orangeFragWGSL,
//   shaderModules.texturedFragWGSL,
// ];

// const vertModules = [
//   shaderModules.distanceSizedPointsVertWGSL,
//   shaderModules.fixedSizePointsVertWGSL,
// ];

// const depthFormat = 'depth24plus';

// // make pipelines for each combination
// const pipelines = vertModules.map((vertModule) =>
//   fragModules.map((fragModule) =>
//     device.createRenderPipeline({
//       layout: pipelineLayout,
//       vertex: {
//         module: vertModule,
//         buffers: [
//           {
//             arrayStride: 3 * 4, // 3 floats, 4 bytes each
//             stepMode: 'instance',
//             attributes: [
//               { shaderLocation: 0, offset: 0, format: 'float32x3' }, // position
//             ],
//           },
//         ],
//       },
//       fragment: {
//         module: fragModule,
//         targets: [
//           {
//             format: presentationFormat,
//             blend: {
//               color: {
//                 srcFactor: 'one',
//                 dstFactor: 'one-minus-src-alpha',
//               },
//               alpha: {
//                 srcFactor: 'one',
//                 dstFactor: 'one-minus-src-alpha',
//               },
//             },
//           },
//         ],
//       },
//       depthStencil: {
//         depthWriteEnabled: true,
//         depthCompare: 'less',
//         format: depthFormat,
//       },
//     })
//   )
// );

// const vertexData = createFibonacciSphereVertices({
//   radius: 1,
//   numSamples: 1000,
// });
// const kNumPoints = vertexData.length / 3;

// const vertexBuffer = device.createBuffer({
//   label: 'vertex buffer vertices',
//   size: vertexData.byteLength,
//   usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST,
// });
// device.queue.writeBuffer(vertexBuffer, 0, vertexData);

// const uniformValues = new Float32Array(16 + 2 + 1 + 1);
// const uniformBuffer = device.createBuffer({
//   size: uniformValues.byteLength,
//   usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
// });
// const kMatrixOffset = 0;
// const kResolutionOffset = 16;
// const kSizeOffset = 18;
// const matrixValue = uniformValues.subarray(kMatrixOffset, kMatrixOffset + 16);
// const resolutionValue = uniformValues.subarray(
//   kResolutionOffset,
//   kResolutionOffset + 2
// );
// const sizeValue = uniformValues.subarray(kSizeOffset, kSizeOffset + 1);

// // Use canvas 2d to make texture data
// const ctx = new OffscreenCanvas(64, 64).getContext('2d');
// ctx.font = '60px sans-serif';
// ctx.textAlign = 'center';
// ctx.textBaseline = 'middle';
// ctx.fillText('🦋', 32, 32);

// const sampler = device.createSampler();
// const texture = device.createTexture({
//   size: [ctx.canvas.width, ctx.canvas.height],
//   format: 'rgba8unorm',
//   usage:
//     GPUTextureUsage.COPY_DST |
//     GPUTextureUsage.TEXTURE_BINDING |
//     GPUTextureUsage.RENDER_ATTACHMENT,
// });
// device.queue.copyExternalImageToTexture(
//   { source: ctx.canvas, flipY: true },
//   { texture },
//   [ctx.canvas.width, ctx.canvas.height]
// );

// const bindGroup = device.createBindGroup({
//   layout: bindGroupLayout,
//   entries: [
//     { binding: 0, resource: { buffer: uniformBuffer } },
//     { binding: 1, resource: sampler },
//     { binding: 2, resource: texture.createView() },
//   ],
// });

// const renderPassDescriptor: GPURenderPassDescriptor = {
//   label: 'our basic canvas renderPass',
//   colorAttachments: [
//     {
//       view: undefined, // assigned later
//       clearValue: [0.3, 0.3, 0.3, 1],
//       loadOp: 'clear',
//       storeOp: 'store',
//     },
//   ],
//   depthStencilAttachment: {
//     view: undefined, // to be filled out when we render
//     depthClearValue: 1.0,
//     depthLoadOp: 'clear',
//     depthStoreOp: 'store',
//   },
// };

// const settings = {
//   fixedSize: false,
//   textured: false,
//   size: 10,
// };

// const gui = new GUI();
// gui.add(settings, 'fixedSize');
// gui.add(settings, 'textured');
// gui.add(settings, 'size', 0, 80);

// let depthTexture;

// function render(time: number) {
//   // Convert to seconds.
//   time *= 0.001;

//   // Get the current texture from the canvas context and
//   // set it as the texture to render to.
//   const canvasTexture = context.getCurrentTexture();
//   renderPassDescriptor.colorAttachments[0].view = canvasTexture.createView();

//   // If we don't have a depth texture OR if its size is different
//   // from the canvasTexture when make a new depth texture
//   if (
//     !depthTexture ||
//     depthTexture.width !== canvasTexture.width ||
//     depthTexture.height !== canvasTexture.height
//   ) {
//     if (depthTexture) {
//       depthTexture.destroy();
//     }
//     depthTexture = device.createTexture({
//       size: [canvasTexture.width, canvasTexture.height],
//       format: depthFormat,
//       usage: GPUTextureUsage.RENDER_ATTACHMENT,
//     });
//   }
//   renderPassDescriptor.depthStencilAttachment.view = depthTexture.createView();

//   const { size, fixedSize, textured } = settings;

//   const pipeline = pipelines[fixedSize ? 1 : 0][textured ? 1 : 0];

//   // Set the size in the uniform values
//   sizeValue[0] = size;

//   const fov = (90 * Math.PI) / 180;
//   const aspect = canvas.clientWidth / canvas.clientHeight;
//   const projection = mat4.perspective(fov, aspect, 0.1, 50);
//   const view = mat4.lookAt(
//     [0, 0, 1.5], // position
//     [0, 0, 0], // target
//     [0, 1, 0] // up
//   );
//   const viewProjection = mat4.multiply(projection, view);
//   mat4.rotateY(viewProjection, time, matrixValue);
//   mat4.rotateX(matrixValue, time * 0.1, matrixValue);

//   // Update the resolution in the uniform values
//   resolutionValue.set([canvasTexture.width, canvasTexture.height]);

//   // Copy the uniform values to the GPU
//   device.queue.writeBuffer(uniformBuffer, 0, uniformValues);

//   const encoder = device.createCommandEncoder();
//   const pass = encoder.beginRenderPass(renderPassDescriptor);
//   pass.setPipeline(pipeline);
//   pass.setVertexBuffer(0, vertexBuffer);
//   pass.setBindGroup(0, bindGroup);
//   pass.draw(6, kNumPoints);
//   pass.end();

//   const commandBuffer = encoder.finish();
//   device.queue.submit([commandBuffer]);

//   requestAnimationFrame(render);
// }

// requestAnimationFrame(render);
