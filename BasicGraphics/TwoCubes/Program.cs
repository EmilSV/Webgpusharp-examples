using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.VisualBasic;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

const int WIDTH = 640;
const int HEIGHT = 480;

var executingAssembly = Assembly.GetExecutingAssembly();
var basicVertWgsl = ResourceUtils.GetEmbeddedResource("TwoCubes.shaders.basic.vert.wgsl", executingAssembly);
var vertexPositionColorWgsl = ResourceUtils.GetEmbeddedResource("TwoCubes.shaders.vertexPositionColor.frag.wgsl", executingAssembly);


return Run("Two Cubes", WIDTH, HEIGHT, async runContext =>
{
    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();

    var adapter = await instance.RequestAdapterAsync(new()
    {
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
    }) ?? throw new Exception("Could not create device");

    var queue = device.GetQueue();
    var surfaceCapabilities = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCapabilities.Formats[0];

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

    var verticesBuffer = device.CreateBuffer(new()
    {
        Label = "Vertices Buffer",
        Size = (ulong)System.Buffer.ByteLength(Cube.CubeVertices),
        Usage = BufferUsage.Vertex,
        MappedAtCreation = true,
    })!;

    verticesBuffer.GetMappedRange<float>(data =>
    {
        Cube.CubeVertices.AsSpan().CopyTo(data);
    });
    verticesBuffer.Unmap();

    var pipeline = device.CreateRenderPipelineSync(new()
    {
        Layout = null, // Auto-layout
        Vertex = new()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = basicVertWgsl
            }),
            Buffers = [
                new()
                {
                    ArrayStride = Cube.CubeVertexSize,
                    Attributes = [
                        new()
                        {
                            // position
                            ShaderLocation = 0,
                            Offset = Cube.CubePositionOffset,
                            Format = VertexFormat.Float32x4,
                        },
                        new()
                        {
                            // uv
                            ShaderLocation = 1,
                            Offset = Cube.CubeUVOffset,
                            Format = VertexFormat.Float32x2,
                        }
                    ],
                }
            ]
        },
        Fragment = new FragmentState()
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexPositionColorWgsl
            }),
            Targets = [
                new()
                {
                    Format = surfaceFormat
                }
          ]
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,

            // Backface culling since the cube is solid piece of geometry.
            // Faces pointing away from the camera will be occluded by faces
            // pointing toward the camera.
            CullMode = CullMode.Back,
        },

        DepthStencil = new DepthStencilState()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            Format = TextureFormat.Depth24Plus,
        },
    })!;

    var depthTexture = device.CreateTexture(new()
    {
        Size = new(WIDTH, HEIGHT),
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment,
    });

    const int matrixSize = 4 * 16; // 4x4 matrix
    const int offset = 256; // uniformBindGroup offset must be 256-byte aligned
    const int uniformBufferSize = offset + matrixSize;

    var uniformBuffer = device.CreateBuffer(new()
    {
        Label = "Uniform Buffer",
        Size = uniformBufferSize,
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var uniformBindGroup1 = device.CreateBindGroup(new()
    {
        Layout = pipeline.GetBindGroupLayout(0),
        Entries = [
            new BindGroupEntry()
            {
                Binding = 0,
                Buffer = uniformBuffer!,
                Size = matrixSize,
            }
        ]
    });

    var uniformBindGroup2 = device.CreateBindGroup(new()
    {
        Layout = pipeline.GetBindGroupLayout(0),
        Entries = [
            new BindGroupEntry()
            {
                Binding = 0,
                Buffer = uniformBuffer,
                Offset = offset,
                Size = matrixSize,
            }
        ],
    });

    const float aspect = WIDTH / (float)HEIGHT;
    var projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView((float)(2.0 * Math.PI / 5.0), aspect, 1f, 100.0f);
    var viewMatrix = Matrix4x4.CreateTranslation(new(0, 0, -7));

    (Matrix4x4, Matrix4x4) getModelMatrixes()
    {
        float now = (float)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds;

        var modelMatrix1 = Matrix4x4.CreateFromAxisAngle(new(MathF.Sin(now), MathF.Cos(now), 0), 1) with
        {
            Translation = new(-2, 0, 0)
        };

        var modelMatrix2 = Matrix4x4.CreateFromAxisAngle(new(MathF.Cos(now), MathF.Sin(now), 0), 1) with
        {
            Translation = new(2, 0, 0)
        };

        modelMatrix1 *= viewMatrix * projectionMatrix;
        modelMatrix2 *= viewMatrix * projectionMatrix;

        return (modelMatrix1, modelMatrix2);
    }

    runContext.OnFrame += () =>
    {
        var (modelMatrix1, modelMatrix2) = getModelMatrixes();
        queue.WriteBuffer(uniformBuffer, 0, modelMatrix1);
        queue.WriteBuffer(uniformBuffer, offset, modelMatrix2);

        var texture = surface.GetCurrentTexture().Texture!;
        var textureView = texture.CreateView()!;

        var commandEncoder = device.CreateCommandEncoder(new());
        var passEncoder = commandEncoder.BeginRenderPass(new()
        {
            ColorAttachments = [
                new()
                {
                    View = textureView,
                    ClearValue = new Color(0.5, 0.5, 0.5, 1.0),
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                }
            ],
            DepthStencilAttachment = new RenderPassDepthStencilAttachment()
            {
                View = depthTexture.CreateView()!,
                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
            }
        });
        passEncoder.SetPipeline(pipeline);
        passEncoder.SetVertexBuffer(0, verticesBuffer);

        // Bind the bind group (with the transformation matrix) for
        // each cube, and draw.
        passEncoder.SetBindGroup(0, uniformBindGroup1);
        passEncoder.Draw(Cube.CubeVertexCount);

        passEncoder.SetBindGroup(0, uniformBindGroup2);
        passEncoder.Draw(Cube.CubeVertexCount);

        passEncoder.End();
        queue.Submit([commandEncoder.Finish()]);

        surface.Present();
    };
});