using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;
using GPUBuffer = WebGpuSharp.Buffer;

const int WIDTH = 640;
const int HEIGHT = 480;
const float ASPECT = (float)WIDTH / HEIGHT;

var assembly = Assembly.GetExecutingAssembly();
var simpleLightingWGSL = ResourceUtils.GetEmbeddedResource("StencilMask.shaders.simple-lighting.wgsl", assembly);
var mousePos = new Vector2(0, 0);

return Run("Stencil Mask", WIDTH, HEIGHT, async runContext =>
{
    var startTimeStamp = Stopwatch.GetTimestamp();

    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();

    // Track mouse position
    runContext.Input.OnMouseMotion += (e) =>
    {
        // Convert to normalized coordinates (-1 to 1)
        mousePos.X = e.x / (float)WIDTH * 2 - 1;
        mousePos.Y = -(e.y / (float)HEIGHT * 2 - 1);
    };

    var adapter = await instance.RequestAdapterAsync(new()
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

    // Creates a buffer and puts data in it
    GPUBuffer CreateBufferWithData<T>(ReadOnlySpan<T> data, BufferUsage usage) where T : unmanaged
    {
        var buffer = device.CreateBuffer(new()
        {
            Size = (ulong)(data.Length * Unsafe.SizeOf<T>()),
            Usage = usage | BufferUsage.CopyDst,
        });
        queue.WriteBuffer(buffer, 0, data);
        return buffer;
    }

    // Creates vertex and index buffers for the given data
    Geometry CreateGeometry(VertexData vertexData)
    {
        var vertexBuffer = CreateBufferWithData(vertexData.Vertices, BufferUsage.Vertex);
        var indexBuffer = CreateBufferWithData(vertexData.Indices, BufferUsage.Index);
        return new Geometry
        {
            VertexBuffer = vertexBuffer,
            IndexBuffer = indexBuffer,
            IndexFormat = IndexFormat.Uint16,
            NumVertices = vertexData.Indices.Length * 3,
        };
    }

    // Create Geometry for our scenes
    var planeVerts = Primitives.ReorientInPlace(
        Primitives.CreatePlaneVertices(),
        Matrix4x4.CreateTranslation(0, 0.5f, 0)
    );
    var planeGeo = CreateGeometry(planeVerts);
    var sphereGeo = CreateGeometry(Primitives.CreateSphereVertices());
    var torusGeo = CreateGeometry(Primitives.CreateTorusVertices(thickness: 0.5f));
    var cubeGeo = CreateGeometry(Primitives.CreateCubeVertices());
    var coneGeo = CreateGeometry(Primitives.CreateTruncatedConeVertices());
    var cylinderGeo = CreateGeometry(Primitives.CreateCylinderVertices());
    var jemGeo = CreateGeometry(
        Primitives.Facet(Primitives.CreateSphereVertices(subdivisionsAxis: 6, subdivisionsHeight: 5))
    );
    var diceGeo = CreateGeometry(
        Primitives.Facet(Primitives.CreateTorusVertices(
            thickness: 0.5f,
            radialSubdivisions: 8,
            bodySubdivisions: 8
        ))
    );

    // Create a bind group layout and pipeline layout so we can
    // share the bind groups with multiple pipelines.
    var bindGroupLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new()
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new(),
            },
            new()
            {
                Binding = 1,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new(),
            },
        ],
    });

    var layout = device.CreatePipelineLayout(new()
    {
        BindGroupLayouts = [bindGroupLayout],
    });

    var module = device.CreateShaderModuleWGSL(new() { Code = simpleLightingWGSL });

    RenderPipelineDescriptor pipelineDesc = new()
    {
        Layout = layout,
        Vertex = new()
        {
            Module = module,
            Buffers =
            [
                new()
                {
                    ArrayStride = (ulong)Unsafe.SizeOf<Vertex>(),
                    Attributes =
                    [
                        new()
                        {
                            ShaderLocation = 0,
                            Offset = (ulong)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position)),
                            Format = VertexFormat.Float32x3 },
                        new()
                        {
                            ShaderLocation = 1,
                            Offset = (ulong)Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal)),
                            Format = VertexFormat.Float32x3
                        },
                        new()
                        {
                            ShaderLocation = 2,
                            Offset = (ulong)Marshal.OffsetOf<Vertex>(nameof(Vertex.Texcoord)),
                            Format = VertexFormat.Float32x2
                        },
                    ],
                },
            ],
        },
        Fragment = new()
        {
            Module = module,
            Targets = [new() { Format = surfaceFormat }],
        },
        Primitive = new()
        {
            Topology = PrimitiveTopology.TriangleList,
            CullMode = CullMode.Back,
        },
        DepthStencil = new()
        {
            DepthWriteEnabled = OptionalBool.True,
            DepthCompare = CompareFunction.Less,
            // The stencilFront setting specifies what happens when a front facing
            // triangle is rasterized. passOp: 'replace' means, when the texel "pass"es
            // the depth and stencil tests, replace the value in the stencil texture
            // with the reference value. The depth test is specified above. The stencil
            // test defaults to "always". The reference value is specified in the
            // command buffer with setStencilReference.
            // Effectively we'll draw the reference value into the stencil texture
            // with the reference value anywhere we draw the planes of the cube.
            StencilFront = new()
            {
                PassOp = StencilOperation.Replace,
            },
            Format = TextureFormat.Depth24PlusStencil8,
        },
    };

    // Make two render pipelines. One to set the stencil and one to draw
    // only where the stencil equals the stencil reference value.
    var stencilSetPipeline = device.CreateRenderPipelineSync(pipelineDesc);

    // passOp: 'keep' means, when the texel "pass"es the depth and stencil tests,
    // keep the value in the stencil texture as is. We set the stencil
    // test to 'equal' so the texel will only pass the stencil test when
    // the reference value, set in the command buffer with setStencilReference,
    // matches what's already in the stencil texture.
    pipelineDesc.DepthStencil = pipelineDesc.DepthStencil.Value with
    {
        StencilFront = pipelineDesc.DepthStencil!.Value.StencilFront with
        {
            PassOp = StencilOperation.Keep,
            Compare = CompareFunction.Equal,
        },
    };
    var stencilMaskPipeline = device.CreateRenderPipelineSync(pipelineDesc);

    // Helper functions for random and color
    static float Rand(float min, float max) => Random.Shared.NextSingle() * (max - min) + min;
    static float RandMax(float max) => Rand(0, max);

    Vector4 HslToRgba(float h, float s, float l)
    {
        static float hueToRgb(float p, float q, float t)
        {
            if (t < 0f)
                t += 1f;
            if (t > 1f)
                t -= 1f;
            if (t < 1f / 6f)
                return p + (q - p) * 6f * t;
            if (t < 1f / 2f)
                return q;
            if (t < 2f / 3f)
                return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }

        float r, g, b;

        if (s == 0f)
        {
            r = g = b = l; // achromatic
        }
        else
        {
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
            float p = 2 * l - q;
            r = hueToRgb(p, q, h + 1f / 3f);
            g = hueToRgb(p, q, h);
            b = hueToRgb(p, q, h - 1f / 3f);
        }
        return new Vector4(r, g, b, 1f);
    }

    static T RandElem<T>(T[] arr) => arr[(int)RandMax(arr.Length)];

    /// <summary>
    /// Make a scene with a bunch of semi-randomly colored objects.
    /// Each scene has a shared uniform buffer for viewProjection and lightDirection.
    /// Each object has its own uniform buffer for its color and its worldMatrix.
    /// </summary>
    Scene MakeScene(int numInstances, float hue, Geometry[] geometries)
    {
        var sharedUniformValues = new SharedUniforms();
        var sharedUniformBuffer = device.CreateBuffer(new()
        {
            Size = (ulong)Unsafe.SizeOf<SharedUniforms>(),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        });

        var objectInfos = new List<ObjectInfo>();
        for (int i = 0; i < numInstances; ++i)
        {
            var uniformValues = new Uniform();
            var uniformBuffer = device.CreateBuffer(new()
            {
                Size = (ulong)Unsafe.SizeOf<Uniform>(),
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            });

            // Set color
            var color = HslToRgba((hue + RandMax(0.2f)) % 1.0f, Rand(0.7f, 1f), Rand(0.5f, 0.8f));
            uniformValues.Color = color;

            var bindGroup = device.CreateBindGroup(new()
            {
                Layout = bindGroupLayout,
                Entries =
                [
                    new() { Binding = 0, Buffer = uniformBuffer },
                    new() { Binding = 1, Buffer = sharedUniformBuffer },
                ],
            });

            objectInfos.Add(new ObjectInfo
            {
                UniformValues = uniformValues,
                UniformBuffer = uniformBuffer,
                BindGroup = bindGroup,
                Geometry = RandElem(geometries),
            });
        }

        return new Scene
        {
            ObjectInfos = objectInfos,
            SharedUniformBuffer = sharedUniformBuffer,
            SharedUniformValues = sharedUniformValues,
        };
    }

    // Make our masking scenes, each with a single plane
    var maskScenes = new Scene[]
    {
        MakeScene(1, 0f / 6f + 0.5f, [planeGeo]),
        MakeScene(1, 1f / 6f + 0.5f, [planeGeo]),
        MakeScene(1, 2f / 6f + 0.5f, [planeGeo]),
        MakeScene(1, 3f / 6f + 0.5f, [planeGeo]),
        MakeScene(1, 4f / 6f + 0.5f, [planeGeo]),
        MakeScene(1, 5f / 6f + 0.5f, [planeGeo]),
    };

    // Make our object scenes, one for the background and one for each cube plane
    var scene0 = MakeScene(100, 0f / 7f, [sphereGeo]);
    var scene1 = MakeScene(100, 1f / 7f, [cubeGeo]);
    var scene2 = MakeScene(100, 2f / 7f, [torusGeo]);
    var scene3 = MakeScene(100, 3f / 7f, [coneGeo]);
    var scene4 = MakeScene(100, 4f / 7f, [cylinderGeo]);
    var scene5 = MakeScene(100, 5f / 7f, [jemGeo]);
    var scene6 = MakeScene(100, 6f / 7f, [diceGeo]);

    Texture? depthTexture = null;

    /// <summary>
    /// Update the viewProjection and light position of the scene
    /// and world matrix of the plane
    /// </summary>
    void UpdateMask(float time, Scene scene, Vector3 rotation)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            30f * MathF.PI / 180f,
            ASPECT,
            0.5f,
            100f
        );

        var eye = new Vector3(0, 0, 45);
        var target = new Vector3(0, 0, 0);
        var up = new Vector3(0, 1, 0);

        var view = Matrix4x4.CreateLookAt(eye, target, up);
        var viewProjection = view * projection;

        var lightDirection = Vector3.Normalize(new Vector3(1, 8, 10));

        // Copy viewProjection matrix to shared uniform values
        scene.SharedUniformValues.ViewProjection = viewProjection;
        scene.SharedUniformValues.LightDirection = lightDirection;
        queue.WriteBuffer(scene.SharedUniformBuffer, scene.SharedUniformValues);

        foreach (var info in scene.ObjectInfos)
        {
            var worldMatrix = Matrix4x4.Identity;
            var worldX = mousePos.X * 10;
            var worldY = mousePos.Y * 10;
            worldMatrix.Translate(new Vector3(worldX, worldY, 0));
            worldMatrix.RotateX(time * 0.25f);
            worldMatrix.RotateY(time * 0.15f);
            worldMatrix.RotateX(rotation.X * MathF.PI);
            worldMatrix.RotateZ(rotation.Z * MathF.PI);
            worldMatrix.Scale(new Vector3(10, 10, 10));

            info.UniformValues.world = worldMatrix;
            queue.WriteBuffer(info.UniformBuffer, info.UniformValues);
        }
    }

    /// <summary>
    /// Update the viewProjection and light position.
    /// and world matrix of every object in the scene.
    /// This update scene has a fixed position camera and
    /// has objects orbiting and spinning around the origin.
    /// </summary>
    void UpdateScene0(float time, Scene scene)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            30f * MathF.PI / 180f,
            ASPECT,
            0.5f,
            100f
        );

        var eye = new Vector3(0, 0, 35);
        var target = new Vector3(0, 0, 0);
        var up = new Vector3(0, 1, 0);

        var view = Matrix4x4.CreateLookAt(eye, target, up);
        var viewProjection = view * projection;

        var lightDirection = Vector3.Normalize(new Vector3(1, 8, 10));

        scene.SharedUniformValues.ViewProjection = viewProjection;
        scene.SharedUniformValues.LightDirection = lightDirection;
        queue.WriteBuffer(scene.SharedUniformBuffer, scene.SharedUniformValues);

        for (int i = 0; i < scene.ObjectInfos.Count; i++)
        {
            var info = scene.ObjectInfos[i];
            var worldMatrix = Matrix4x4.Identity;
            worldMatrix.Translate(new Vector3(0, 0, MathF.Sin(i * 3.721f + time * 0.1f) * 10));
            worldMatrix.RotateX(i * 4.567f);
            worldMatrix.RotateY(i * 2.967f);
            worldMatrix.Translate(new Vector3(0, 0, MathF.Sin(i * 9.721f + time * 0.1f) * 10));
            worldMatrix.RotateX(time * 0.53f + i);

            info.UniformValues.world = worldMatrix;
            queue.WriteBuffer(info.UniformBuffer, info.UniformValues);
        }
    }

    /// <summary>
    /// Update the viewProjection and light position of the scene
    /// and world matrix of every object in the scene.
    /// This update scene has a camera orbiting the origin and
    /// has objects orbiting and spinning around the origin.
    /// </summary>
    void UpdateScene1(float time, Scene scene)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            30f * MathF.PI / 180f,
            ASPECT,
            0.5f,
            100f
        );

        const float radius = 35f;
        float t = time * 0.1f;
        var eye = new Vector3(MathF.Cos(t) * radius, 4, MathF.Sin(t) * radius);
        var target = new Vector3(0, 0, 0);
        var up = new Vector3(0, 1, 0);

        var view = Matrix4x4.CreateLookAt(eye, target, up);
        var viewProjection = view * projection;

        var lightDirection = Vector3.Normalize(new Vector3(1, 8, 10));

        scene.SharedUniformValues.ViewProjection = viewProjection;
        scene.SharedUniformValues.LightDirection = lightDirection;

        queue.WriteBuffer(scene.SharedUniformBuffer, scene.SharedUniformValues);

        for (int i = 0; i < scene.ObjectInfos.Count; i++)
        {
            var info = scene.ObjectInfos[i];
            var worldMatrix = Matrix4x4.Identity;
            worldMatrix.Translate(new Vector3(0, 0, MathF.Sin(i * 3.721f + time * 0.1f) * 10));
            worldMatrix.RotateX(i * 4.567f);
            worldMatrix.RotateY(i * 2.967f);
            worldMatrix.Translate(new Vector3(0, 0, MathF.Sin(i * 9.721f + time * 0.1f) * 10));
            worldMatrix.RotateX(time * 1.53f + i);

            info.UniformValues.world = worldMatrix;
            queue.WriteBuffer(info.UniformBuffer, info.UniformValues);
        }
    }

    /// <summary>
    /// Draw a scene and every object in it with a specific stencilReference value
    /// </summary>
    void DrawScene(
        CommandEncoder encoder,
        in RenderPassDescriptor renderPassDescriptor,
        RenderPipeline pipeline,
        Scene scene,
        uint stencilRef)
    {

        var pass = encoder.BeginRenderPass(renderPassDescriptor);
        pass.SetPipeline(pipeline);
        pass.SetStencilReference(stencilRef);

        foreach (var info in scene.ObjectInfos)
        {
            pass.SetBindGroup(0, info.BindGroup);
            pass.SetVertexBuffer(0, info.Geometry.VertexBuffer);
            pass.SetIndexBuffer(info.Geometry.IndexBuffer, info.Geometry.IndexFormat);
            pass.DrawIndexed((uint)info.Geometry.NumVertices);
        }

        pass.End();
    }

    runContext.OnFrame += () =>
    {
        var now = (float)Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;

        var surfaceTexture = surface.GetCurrentTexture().Texture!;

        // If we don't have a depth texture OR if its size is different
        // from the surfaceTexture when make a new depth texture
        if (depthTexture == null ||
            depthTexture.GetWidth() != surfaceTexture.GetWidth() ||
            depthTexture.GetHeight() != surfaceTexture.GetHeight())
        {
            depthTexture?.Destroy();
            depthTexture = device.CreateTexture(new()
            {
                Size = new(surfaceTexture.GetWidth(), surfaceTexture.GetHeight()),
                Format = TextureFormat.Depth24PlusStencil8,
                Usage = TextureUsage.RenderAttachment,
            });
        }

        UpdateMask(now, maskScenes[0], new(0, 0, 0));
        UpdateMask(now, maskScenes[1], new(1, 0, 0));
        UpdateMask(now, maskScenes[2], new(0, 0, 0.5f));
        UpdateMask(now, maskScenes[3], new(0, 0, -0.5f));
        UpdateMask(now, maskScenes[4], new(-0.5f, 0, 0));
        UpdateMask(now, maskScenes[5], new(0.5f, 0, 0));

        UpdateScene0(now, scene0);
        UpdateScene1(now, scene1);
        UpdateScene0(now, scene2);
        UpdateScene1(now, scene3);
        UpdateScene0(now, scene4);
        UpdateScene1(now, scene5);
        UpdateScene0(now, scene6);

        RenderPassDescriptor clearPassDesc = new()
        {
            ColorAttachments =
            [
                new()
                {
                    View = surfaceTexture.CreateView(),
                    ClearValue = new(0.2, 0.2, 0.2, 1.0),
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                }
            ],
            DepthStencilAttachment = new()
            {
                View = depthTexture.CreateView(),
                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
                StencilLoadOp = LoadOp.Clear,
                StencilStoreOp = StoreOp.Store,
            },
        };

        RenderPassDescriptor loadPassDesc = new()
        {
            ColorAttachments =
            [
                new()
                {
                    View = surfaceTexture.CreateView(),
                    LoadOp = LoadOp.Load,
                    StoreOp = StoreOp.Store,
                }
            ],
            DepthStencilAttachment = new()
            {
                View = depthTexture.CreateView(),
                DepthClearValue = 1.0f,
                DepthLoadOp = LoadOp.Load,
                DepthStoreOp = StoreOp.Store,
                StencilLoadOp = LoadOp.Load,
                StencilStoreOp = StoreOp.Store,
            },
        };

        var encoder = device.CreateCommandEncoder();

        // Draw the 6 faces of a cube into the stencil buffer
        // each with a different stencil value.
        DrawScene(encoder, clearPassDesc, stencilSetPipeline, maskScenes[0], 1);
        DrawScene(encoder, loadPassDesc, stencilSetPipeline, maskScenes[1], 2);
        DrawScene(encoder, loadPassDesc, stencilSetPipeline, maskScenes[2], 3);
        DrawScene(encoder, loadPassDesc, stencilSetPipeline, maskScenes[3], 4);
        DrawScene(encoder, loadPassDesc, stencilSetPipeline, maskScenes[4], 5);
        DrawScene(encoder, loadPassDesc, stencilSetPipeline, maskScenes[5], 6);

        // Draw each scene of moving objects but only where the stencil value
        // matches the stencil reference.
        DrawScene(encoder, loadPassDesc, stencilMaskPipeline, scene0, 0);
        DrawScene(encoder, loadPassDesc, stencilMaskPipeline, scene1, 1);
        DrawScene(encoder, loadPassDesc, stencilMaskPipeline, scene2, 2);
        DrawScene(encoder, loadPassDesc, stencilMaskPipeline, scene3, 3);
        DrawScene(encoder, loadPassDesc, stencilMaskPipeline, scene4, 4);
        DrawScene(encoder, loadPassDesc, stencilMaskPipeline, scene5, 5);
        DrawScene(encoder, loadPassDesc, stencilMaskPipeline, scene6, 6);

        queue.Submit([encoder.Finish()]);
        surface.Present();
    };
});