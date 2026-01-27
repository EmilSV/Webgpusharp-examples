using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;
using static Setup.SetupWebGPU;

const int WIDTH = 600;
const int HEIGHT = 600;
const int MAT4X4_BYTES = 64;

var assembly = Assembly.GetExecutingAssembly();
var gltfWGSL = ResourceUtils.GetEmbeddedResource("SkinnedMesh.shaders.gltf.wgsl", assembly);
var gridWGSL = ResourceUtils.GetEmbeddedResource("SkinnedMesh.shaders.grid.wgsl", assembly);
var whaleGlbData = ResourceUtils.GetEmbeddedResource("SkinnedMesh.assets.gltf.whale.glb", assembly);
var gltfWGSLStr = Encoding.UTF8.GetString(gltfWGSL);



var startTimeStamp = Stopwatch.GetTimestamp();
var settings = new Settings();

CommandBuffer DrawGui(
    GuiContext guiContext,
    Queue queue,
    GPUBuffer generalUniformsBuffer,
    Surface surface, GlbUtils.ConvertGLBResult whaleScene)
{
    guiContext.NewFrame();
    ImGui.SetNextWindowBgAlpha(0.75f);
    ImGui.SetNextWindowPos(new(0, 0));
    ImGui.SetNextWindowSize(new(350, 240));
    ImGui.Begin("Skinned Mesh",
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoResize
    );

    // Determine whether we want to render our whale or our skinned grid
    if (ImGuiUtils.EnumDropdown("Object", ref settings.ObjectType))
    {
        if (settings.ObjectType == ObjectType.SkinnedGrid)
        {
            settings.CameraX = -10;
            settings.CameraY = 0;
            settings.ObjectScale = 1.27f;
        }
        else
        {
            if (settings.SkinMode == SkinMode.Off)
            {
                settings.CameraX = 0;
                settings.CameraY = 0;
                settings.CameraZ = -11;
            }
            else
            {
                settings.CameraX = 0;
                settings.CameraY = -5.1f;
                settings.CameraZ = -14.6f;
            }
        }
    }

    // Output the mesh normals, its joints, or the weights that influence the movement of the joints
    if (ImGuiUtils.EnumDropdown("Render Mode", ref settings.RenderMode))
    {
        queue.WriteBuffer(generalUniformsBuffer, 0, (uint)settings.RenderMode);
    }

    // Determine whether the mesh is static or whether skinning is activated
    if (ImGuiUtils.EnumDropdown("Skin Mode", ref settings.SkinMode))
    {
        if (settings.ObjectType == ObjectType.Whale)
        {
            if (settings.SkinMode == SkinMode.Off)
            {
                settings.CameraX = 0;
                settings.CameraY = 0;
                settings.CameraZ = -11;
            }
            else
            {
                settings.CameraX = 0;
                settings.CameraY = -5.1f;
                settings.CameraZ = -14.6f;
            }
        }
        queue.WriteBuffer(generalUniformsBuffer, 4, (uint)settings.SkinMode);
    }

    ImGui.SliderFloat("Angle", ref settings.Angle, 0.05f, 0.5f);
    ImGui.SliderFloat("Speed", ref settings.Speed, 10, 100);

    ImGui.End();
    guiContext.EndFrame();

    return guiContext.Render(surface)!.Value!;
}

return Run("Skinned Mesh", WIDTH, HEIGHT, async runContext =>
{
    var instance = runContext.GetInstance();
    var surface = runContext.GetSurface();
    var guiContext = runContext.GetGuiContext();

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
        }
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

    var depthTexture = device.CreateTexture(new()
    {
        Size = new(WIDTH, HEIGHT, 1),
        Format = TextureFormat.Depth24Plus,
        Usage = TextureUsage.RenderAttachment,
    });

    var cameraBuffer = device.CreateBuffer(new()
    {
        Size = MAT4X4_BYTES * 3,
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var cameraBGLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new BindGroupLayoutEntry
            {
                Binding = 0,
                Buffer = new() { Type = BufferBindingType.Uniform },
                Visibility = ShaderStage.Vertex,
            }
        ],
    });

    var cameraBG = device.CreateBindGroup(new()
    {
        Layout = cameraBGLayout,
        Entries =
        [
            new BindGroupEntry
            {
                Binding = 0,
                Buffer = cameraBuffer,
            }
        ],
    });

    var generalUniformsBuffer = device.CreateBuffer(new()
    {
        Size = (ulong)(sizeof(uint) * 2),
        Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
    });

    var generalUniformsBGLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new BindGroupLayoutEntry
            {
                Binding = 0,
                Buffer = new() { Type = BufferBindingType.Uniform },
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            }
        ],
    });

    var generalUniformsBG = device.CreateBindGroup(new()
    {
        Layout = generalUniformsBGLayout,
        Entries =
        [
            new BindGroupEntry
            {
                Binding = 0,
                Buffer = generalUniformsBuffer,
            }
        ],
    });

    // Fetch whale resources from the glb file
    var whaleScene = GlbUtils.ConvertGLBToJSONAndBinary(whaleGlbData, device);

    // Builds a render pipeline for our whale mesh
    whaleScene.Meshes[0].BuildRenderPipeline(
        device,
        gltfWGSLStr,
        gltfWGSLStr,
        surfaceFormat,
        depthTexture.GetFormat(),
        [
            cameraBGLayout,
            generalUniformsBGLayout,
            whaleScene.Nodes[0].NodeUniformsBGL,
            GLTFSkin.SkinBindGroupLayout!,
        ]
    );

    // Create skinned grid resources
    var skinnedGridVertexBuffers = GridUtils.CreateSkinnedGridBuffers(device);

    // Buffer for our uniforms, joints, and inverse bind matrices
    var skinnedGridJointUniformBuffer = device.CreateBuffer(new()
    {
        Size = MAT4X4_BYTES * 5, // 5 4x4 matrices, one for each bone
        Usage = BufferUsage.Storage | BufferUsage.CopyDst,
    });

    var skinnedGridInverseBindUniformBuffer = device.CreateBuffer(new()
    {
        Size = MAT4X4_BYTES * 5,
        Usage = BufferUsage.Storage | BufferUsage.CopyDst,
    });

    var skinnedGridBoneBGLayout = device.CreateBindGroupLayout(new()
    {
        Entries =
        [
            new BindGroupLayoutEntry
            {
                Binding = 0,
                Buffer = new() { Type = BufferBindingType.ReadOnlyStorage },
                Visibility = ShaderStage.Vertex,
            },
            new BindGroupLayoutEntry
            {
                Binding = 1,
                Buffer = new() { Type = BufferBindingType.ReadOnlyStorage },
                Visibility = ShaderStage.Vertex,
            }
        ],
    });

    var skinnedGridBoneBG = device.CreateBindGroup(new()
    {
        Layout = skinnedGridBoneBGLayout,
        Entries =
        [
            new BindGroupEntry
            {
                Binding = 0,
                Buffer = skinnedGridJointUniformBuffer,
            },
            new BindGroupEntry
            {
                Binding = 1,
                Buffer = skinnedGridInverseBindUniformBuffer,
            }
        ],
    });

    var skinnedGridPipeline = GridUtils.CreateSkinnedGridRenderPipeline(
        device,
        surfaceFormat,
        gridWGSL,
        gridWGSL,
        [cameraBGLayout, generalUniformsBGLayout, skinnedGridBoneBGLayout]
    );

    // Global Calc
    var aspect = (float)WIDTH / HEIGHT;
    var perspectiveProjection = Matrix4x4.CreatePerspectiveFieldOfView(
        (2 * MathF.PI) / 5,
        aspect,
        0.1f,
        100.0f
    );

    var orthographicProjection = Matrix4x4.CreateOrthographicOffCenter(-20, 20, -10, 10, -100, 100);

    Matrix4x4 GetProjectionMatrix()
    {
        if (settings.ObjectType != ObjectType.SkinnedGrid)
        {
            return perspectiveProjection;
        }
        return orthographicProjection;
    }

    Matrix4x4 GetViewMatrix()
    {
        var viewMatrix = Matrix4x4.Identity;
        if (settings.ObjectType == ObjectType.SkinnedGrid)
        {
            viewMatrix = Matrix4x4.CreateTranslation(
                settings.CameraX * settings.ObjectScale,
                settings.CameraY * settings.ObjectScale,
                settings.CameraZ
            );
        }
        else
        {
            viewMatrix = Matrix4x4.CreateTranslation(
                settings.CameraX,
                settings.CameraY,
                settings.CameraZ
            );
        }
        return viewMatrix;
    }

    Matrix4x4 GetModelMatrix()
    {
        var modelMatrix = Matrix4x4.CreateScale(
            settings.ObjectScale,
            settings.ObjectScale,
            settings.ObjectScale
        );
        if (settings.ObjectType == ObjectType.Whale)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimeStamp).TotalSeconds;
            modelMatrix.RotateY((float)elapsed * 0.5f);
        }
        return modelMatrix;
    }

    void AnimSkinnedGrid(Matrix4x4[] boneTransforms, float angle)
    {
        var m = Matrix4x4.Identity;
        m.RotateZ(angle);
        boneTransforms[0] = m;
        
        m.Translate(new(4, 0, 0));
        m.RotateZ(angle);
        boneTransforms[1] = m;

        m.Translate(new(4, 0, 0));
        m.RotateZ(angle);
        boneTransforms[2] = m;
    }

    // Create a group of bones
    BoneObject CreateBoneCollection(int numBones)
    {
        var transforms = new Matrix4x4[numBones];
        var bindPoses = new Matrix4x4[numBones];

        for (int i = 0; i < numBones; i++)
        {
            transforms[i] = Matrix4x4.Identity;
            bindPoses[i] = Matrix4x4.Identity;
        }

        // Get initial bind pose positions
        AnimSkinnedGrid(bindPoses, 0);

        var bindPosesInv = new Matrix4x4[numBones];
        for (int i = 0; i < numBones; i++)
        {
            Matrix4x4.Invert(bindPoses[i], out bindPosesInv[i]);
        }

        return new BoneObject
        {
            Transforms = transforms,
            BindPoses = bindPoses,
            BindPosesInv = bindPosesInv,
        };
    }

    // Create bones of the skinned grid and write the inverse bind positions
    var gridBoneCollection = CreateBoneCollection(5);
    for (int i = 0; i < gridBoneCollection.BindPosesInv.Length; i++)
    {
        queue.WriteBuffer(
            skinnedGridInverseBindUniformBuffer,
            (ulong)(i * 64),
            gridBoneCollection.BindPosesInv[i]
        );
    }

    // A map that maps a joint index to the original matrix transformation of a bone
    var origMatrices = new Dictionary<int, Matrix4x4>();

    void AnimWhaleSkin(GLTFSkin skin, float angle)
    {
        for (int i = 0; i < skin.Joints.Length; i++)
        {
            var joint = skin.Joints[i];
            if (!origMatrices.ContainsKey(joint))
            {
                origMatrices[joint] = whaleScene.Nodes[joint].Source.GetMatrix();
            }

            var origMatrix = origMatrices[joint];

            if (joint == 1 || joint == 0)
            {
                origMatrix.RotateY(-angle);
            }
            else if (joint == 3 || joint == 4)
            {
                origMatrix.RotateX(joint == 3 ? angle : -angle);
            }
            else
            {
                origMatrix.RotateZ(angle);
            }

            Matrix4x4.Decompose(
                matrix: origMatrix, 
                scale: out Vector3 scale, 
                rotation: out Quaternion rotation,
                translation: out Vector3 translation
            );


            whaleScene.Nodes[joint].Source.Scale = scale;
            whaleScene.Nodes[joint].Source.Rotation = rotation;
            whaleScene.Nodes[joint].Source.Position = translation;
        }
    }

    void Frame()
    {
        var projectionMatrix = GetProjectionMatrix();
        var viewMatrix = GetViewMatrix();
        var modelMatrix = GetModelMatrix();

        // Calculate bone transformation
        var t = Stopwatch.GetElapsedTime(startTimeStamp).TotalMilliseconds / 20000.0 * settings.Speed;
        var angle = Math.Sin(t) * settings.Angle;

        // Compute Transforms when angle is applied
        AnimSkinnedGrid(gridBoneCollection.Transforms, (float)angle);

        // Write to camera buffer
        queue.WriteBuffer(cameraBuffer, 0, projectionMatrix);
        queue.WriteBuffer(cameraBuffer, 64, viewMatrix);
        queue.WriteBuffer(cameraBuffer, 128, modelMatrix);

        // Write to skinned grid bone uniform buffer
        for (int i = 0; i < gridBoneCollection.Transforms.Length; i++)
        {
            queue.WriteBuffer(
                skinnedGridJointUniformBuffer,
                (ulong)(i * 64),
                gridBoneCollection.Transforms[i]
            );
        }

       // Pass Descriptor for GLTFs
        var gltfRenderPassDescriptor = new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
            {
                View = surface.GetCurrentTexture()!.Texture!.CreateView(),
                ClearValue = new(0.3, 0.3, 0.3, 1.0),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            }
            ],
            DepthStencilAttachment = new()
            {
                View = depthTexture.CreateView(),
                DepthLoadOp = LoadOp.Clear,
                DepthClearValue = 1.0f,
                DepthStoreOp = StoreOp.Store,
            },
        };

        // Pass descriptor for grid with no depth testing
        var skinnedGridRenderPassDescriptor = new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
            {
                View = surface.GetCurrentTexture()!.Texture!.CreateView(),
                ClearValue = new(0.3, 0.3, 0.3, 1.0),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
            }
            ],
        };

        // Update node matrices
        foreach (var scene in whaleScene.Scenes)
        {
            scene.Root.UpdateWorldMatrix(device);
        }

        // Updates skins
        AnimWhaleSkin(whaleScene.Skins[0], (float)(Math.Sin(t) * settings.Angle));
        whaleScene.Skins[0].Update(device, 6, whaleScene.Nodes);

        var commandEncoder = device.CreateCommandEncoder();

        if (settings.ObjectType == ObjectType.Whale)
        {
            var passEncoder = commandEncoder.BeginRenderPass(gltfRenderPassDescriptor);
            foreach (var scene in whaleScene.Scenes)
            {
                scene.Root.RenderDrawables(passEncoder, [cameraBG, generalUniformsBG]);
            }
            passEncoder.End();
        }
        else
        {
            var passEncoder = commandEncoder.BeginRenderPass(skinnedGridRenderPassDescriptor);
            passEncoder.SetPipeline(skinnedGridPipeline);
            passEncoder.SetBindGroup(0, cameraBG);
            passEncoder.SetBindGroup(1, generalUniformsBG);
            passEncoder.SetBindGroup(2, skinnedGridBoneBG);
            passEncoder.SetVertexBuffer(0, skinnedGridVertexBuffers.Positions);
            passEncoder.SetVertexBuffer(1, skinnedGridVertexBuffers.Joints);
            passEncoder.SetVertexBuffer(2, skinnedGridVertexBuffers.Weights);
            passEncoder.SetIndexBuffer(skinnedGridVertexBuffers.Indices, IndexFormat.Uint16);
            passEncoder.DrawIndexed((uint)GridData.GridIndices.Length);
            passEncoder.End();
        }

        var guiCommandBuffer = DrawGui(guiContext, queue, generalUniformsBuffer, surface, whaleScene);
        queue.Submit([commandEncoder.Finish(), guiCommandBuffer]);
        surface.Present();
    }

    runContext.OnFrame += Frame;
});

enum RenderMode
{
    Normal = 0,
    Joints = 1,
    Weights = 2,
}

enum SkinMode
{
    On = 0,
    Off = 1,
}

enum ObjectType
{
    Whale,
    SkinnedGrid,
}

class Settings
{
    public float CameraX = 0;
    public float CameraY = -5.1f;
    public float CameraZ = -14.6f;
    public float ObjectScale = 1.0f;
    public float Angle = 0.2f;
    public float Speed = 50;
    public ObjectType ObjectType = ObjectType.Whale;
    public RenderMode RenderMode = RenderMode.Normal;
    public SkinMode SkinMode = SkinMode.On;
}

class BoneObject
{
    public required Matrix4x4[] Transforms { get; init; }
    public required Matrix4x4[] BindPoses { get; init; }
    public required Matrix4x4[] BindPosesInv { get; init; }
}
