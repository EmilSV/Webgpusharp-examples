using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;
using static Setup.SetupWebGPU;
using static WebGpuSharp.WebGpuUtil;
using System.Runtime.InteropServices;

static byte[] ToByteArray(Stream input)
{
    using MemoryStream ms = new();
    input.CopyTo(ms);
    return ms.ToArray();
}

const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = WIDTH / (float)HEIGHT;

bool useRenderBundles = true;
int asteroidCount = 100;

CommandBuffer DrawGUI(GuiContext guiContext, Surface surface)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.3f);
    ImGui.Begin("Settings",
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoCollapse
    );
    ImGui.Checkbox(nameof(useRenderBundles), ref useRenderBundles);
    ImGui.SliderInt(nameof(asteroidCount), ref asteroidCount, 1000, 10000);
    ImGui.End();
    guiContext.EndFrame();

    return guiContext.Render(surface)!.Value!;
}

return Run(
    name: "Render Bundles",
    width: WIDTH,
    height: HEIGHT,
    callback: async (instance, surface, guiContext, onFrame) =>
    {
        var startTimeStamp = Stopwatch.GetTimestamp();

        var meshWGSL = ResourceUtils.GetEmbeddedResource("RenderBundles.shaders.mesh.wgsl");

        var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });
        var device = (
            await adapter.RequestDeviceAsync(
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
            )
        )!;

        var queue = device.GetQueue();
        var surfaceCapabilities = surface.GetCapabilities(adapter)!;
        var surfaceFormat = surfaceCapabilities.Formats[0];

        surface.Configure(new()
        {
            Width = WIDTH,
            Height = HEIGHT,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            Format = surfaceFormat,
            Device = device,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto,
        });

        var shaderModule = device.CreateShaderModuleWGSL(new()
        {
            Code = meshWGSL
        });


        var pipeline = device.CreateRenderPipeline(new()
        {
            Layout = null,
            Vertex = ref InlineInit(new VertexState()
            {
                Module = shaderModule,
                Buffers = [
                    new()
                    {
                        ArrayStride =  SphereMesh.Vertex.VertexStride,
                        Attributes= [
                            new()
                            {
                                // position
                                ShaderLocation = 0,
                                Offset = SphereMesh.Vertex.PositionsOffset,
                                Format = VertexFormat.Float32x3,
                            },
                            new()
                            {
                                // normal
                                ShaderLocation = 1,
                                Offset = SphereMesh.Vertex.NormalOffset,
                                Format = VertexFormat.Float32x3,
                            },
                            new()
                            {
                                // uv
                                ShaderLocation = 2,
                                Offset = SphereMesh.Vertex.UvOffset,
                                Format = VertexFormat.Float32x2,
                            },
                        ]
                    }
                ]
            }),
            Fragment = new FragmentState()
            {
                Module = shaderModule,
                Targets = [
                    new(){
                        Format = surfaceFormat
                    }
                ]
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                // Backface culling since the sphere is solid piece of geometry.
                // Faces pointing away from the camera will be occluded by faces
                // pointing toward the camera.
                CullMode = CullMode.Back,
            },

            // Enable depth testing so that the fragment closest to the camera
            // is rendered in front.
            DepthStencil = new DepthStencilState()
            {
                DepthWriteEnabled = OptionalBool.True,
                DepthCompare = CompareFunction.Less,
                Format = TextureFormat.Depth24Plus,
            },
        });

        var depthTexture = device.CreateTexture(new()
        {
            Size = new(WIDTH, HEIGHT),
            Format = TextureFormat.Depth24Plus,
            Usage = TextureUsage.RenderAttachment,
        });

        const int uniformBufferSize = 4 * 16; // 4x4 matrix
        var uniformBuffer = device.CreateBuffer(new()
        {
            Size = uniformBufferSize,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        });


        Texture planetTexture;
        {
            var imageStream = ResourceUtils.GetEmbeddedResourceStream("RenderBundles.assets.img.saturn.jpg");
            var imageData = ResourceUtils.LoadImage(imageStream!);

            planetTexture = device.CreateTexture(new()
            {
                Size = new Extent3D(imageData.Width, imageData.Height, 1),
                Format = TextureFormat.RGBA8Unorm,
                Usage =
                    TextureUsage.TextureBinding |
                    TextureUsage.CopyDst |
                    TextureUsage.RenderAttachment,
            });

            ResourceUtils.CopyExternalImageToTexture(
                queue: queue,
                source: imageData,
                texture: planetTexture,
                width: imageData.Width,
                height: imageData.Height
            );
        }

        Texture moonTexture;
        {
            var imageStream = ResourceUtils.GetEmbeddedResourceStream("RenderBundles.assets.img.moon.jpg");
            var imageData = ResourceUtils.LoadImage(imageStream!);

            moonTexture = device.CreateTexture(new()
            {
                Size = new Extent3D(imageData.Width, imageData.Height, 1),
                Format = TextureFormat.RGBA8Unorm,
                Usage =
                    TextureUsage.TextureBinding |
                    TextureUsage.CopyDst |
                    TextureUsage.RenderAttachment,
            });

            ResourceUtils.CopyExternalImageToTexture(
                queue: queue,
                source: imageData,
                texture: moonTexture,
                width: imageData.Width,
                height: imageData.Height
            );
        }

        var sampler = device.CreateSampler(new()
        {
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
        });

        //helper functions to create the required meshes and bind groups for each sphere.
        Renderable CreateSphereRenderable(
            float radius,
            int widthSegments = 32,
            int heightSegments = 16,
            float randomness = 0)
        {
            var sphereMesh = SphereMesh.Create(
                radius,
                widthSegments,
                heightSegments,
                randomness
            );

            var vertices = device!.CreateBuffer(new()
            {
                Size = (ulong)System.Buffer.ByteLength(sphereMesh.Vertices),
                Usage = BufferUsage.Vertex,
                MappedAtCreation = true,
            });

            vertices.GetMappedRange<SphereMesh.Vertex>(data =>
            {
                sphereMesh.Vertices.AsSpan().CopyTo(data);
            });
            vertices.Unmap();


            var indices = device!.CreateBuffer(new()
            {
                Size = (ulong)System.Buffer.ByteLength(sphereMesh.Indices),
                Usage = BufferUsage.Index,
                MappedAtCreation = true,
            });
            indices.GetMappedRange<ushort>(data =>
            {
                sphereMesh.Indices.AsSpan().CopyTo(data);
            });
            indices.Unmap();

            return new()
            {
                Vertices = vertices,
                Indices = indices,
                IndexCount = sphereMesh.Indices.Length
            };
        }


        BindGroup CreateSphereBindGroup(Texture texture, Matrix4x4 transform)
        {
            var uniformBufferSize = Marshal.SizeOf<Matrix4x4>();
            var uniformBuffer = device!.CreateBuffer(new()
            {
                Size = (uint)uniformBufferSize,
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
                MappedAtCreation = true,
            });
            uniformBuffer.GetMappedRange<Matrix4x4>(data =>
            {
                data[0] = transform;
            });
            uniformBuffer.Unmap();

            var bindGroup = device!.CreateBindGroup(new()
            {
                Layout = pipeline!.GetBindGroupLayout(1),
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

            return bindGroup;
        }

        var transform = Matrix4x4.Identity;

        var planet = CreateSphereRenderable(1.0f);
        planet.BindGroup = CreateSphereBindGroup(planetTexture, transform);

        List<Renderable> asteroids = [
           CreateSphereRenderable(0.01f, 8, 6, 0.15f),
            CreateSphereRenderable(0.013f, 8, 6, 0.15f),
            CreateSphereRenderable(0.017f, 8, 6, 0.15f),
            CreateSphereRenderable(0.02f, 8, 6, 0.15f),
            CreateSphereRenderable(0.03f, 16, 8, 0.15f),
        ];

        List<Renderable> renderables = [planet];

        void EnsureEnoughAsteroids()
        {
            for (int i = renderables.Count; i <= asteroidCount; ++i)
            {
                // Place copies of the asteroid in a ring.
                var radius = Random.Shared.NextSingle() * 1.7f + 1.25f;
                var angle = Random.Shared.NextSingle() * MathF.PI * 2.0f;
                var x = MathF.Sin(angle) * radius;
                var y = (Random.Shared.NextSingle() - 0.5f) * 0.015f;
                var z = MathF.Cos(angle) * radius;

                var transform = Matrix4x4.Identity;
                transform.Translate(new(x, y, z));
                transform.RotateX(MathF.PI * Random.Shared.NextSingle());
                transform.RotateY(MathF.PI * Random.Shared.NextSingle());
                var asteroid = asteroids[i % asteroids.Count];
                renderables.Add(new()
                {
                    Vertices = asteroid.Vertices,
                    Indices = asteroid.Indices,
                    IndexCount = asteroid.IndexCount,
                    BindGroup = CreateSphereBindGroup(moonTexture, transform)
                });
            }
        }
        EnsureEnoughAsteroids();

        
    }
);


class Renderable
{
    public required GPUBuffer Vertices { get; set; }
    public required GPUBuffer Indices { get; set; }
    public BindGroup? BindGroup { get; set; }
    public required int IndexCount { get; set; }
}