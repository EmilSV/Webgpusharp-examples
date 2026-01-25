using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

// NOTE: GLTF code is not generally extensible to all gltf models
// Modified from Will Usher code found at this link https://www.willusher.io/graphics/2023/05/16/0-to-gltf-first-mesh

// Associates the mode parameter of a gltf primitive object with the primitive's intended render mode
enum GLTFRenderMode
{
    Points = 0,
    Line = 1,
    LineLoop = 2,
    LineStrip = 3,
    Triangles = 4,
    TriangleStrip = 5,
    TriangleFan = 6,
}

// Determines how to interpret each element of the structure that is accessed from our accessor
enum GLTFDataComponentType
{
    Byte = 5120,
    UnsignedByte = 5121,
    Short = 5122,
    UnsignedShort = 5123,
    Int = 5124,
    UnsignedInt = 5125,
    Float = 5126,
    Double = 5130,
}

// Determines how to interpret the structure of the values accessed by an accessor
enum GLTFDataStructureType
{
    Scalar = 0,
    Vec2 = 1,
    Vec3 = 2,
    Vec4 = 3,
    Mat2 = 4,
    Mat3 = 5,
    Mat4 = 6,
}

static class GlbUtils
{
    public static uint AlignTo(uint val, uint align)
    {
        return (val + align - 1) / align * align;
    }

    public static void ValidateGLBHeader(ReadOnlySpan<byte> header)
    {
        uint magic = MemoryMarshal.Read<uint>(header);
        uint version = MemoryMarshal.Read<uint>(header[4..]);

        if (magic != 0x46546C67) // "glTF"
            throw new Exception("Provided file is not a glB file");
        if (version != 2)
            throw new Exception("Provided file is not glTF 2.0 file");
    }

    public static void ValidateBinaryHeader(ReadOnlySpan<uint> header)
    {
        if (header[1] != 0x004E4942) // "BIN"
        {
            throw new Exception("Invalid glB: The second chunk of the glB file is not a binary chunk!");
        }
    }

    public record ConvertGLBResult(
        GLTFMesh[] Meshes,
        GLTFNode[] Nodes,
        GLTFScene[] Scenes,
        GLTFSkin[] Skins
    );

    // Upload a GLB model, parse its JSON and Binary components, and create the requisite GPU resources
    // to render them. NOTE: Not extensible to all GLTF contexts at this point in time
    public static ConvertGLBResult ConvertGLBToJSONAndBinary(byte[] buffer, Device device)
    {
        // Binary GLTF layout: https://cdn.willusher.io/webgpu-0-to-gltf/glb-layout.svg
        var jsonHeaderSpan = buffer.AsSpan(0, 20);
        ValidateGLBHeader(jsonHeaderSpan);

        // Length of the jsonChunk found at jsonHeader[12 - 15]
        uint jsonChunkLength = MemoryMarshal.Read<uint>(buffer.AsSpan(12, 4));

        // Parse the JSON chunk of the glB file to a JSON object
        var jsonChunkBytes = buffer.AsSpan(20, (int)jsonChunkLength);
        var jsonChunk = JsonSerializer.Deserialize(jsonChunkBytes, GltfJsonContext.Default.GlTf)
            ?? throw new Exception("Failed to deserialize GLTF JSON");

        // Binary data located after jsonChunk
        var binaryHeaderSpan = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(20 + (int)jsonChunkLength, 8));
        ValidateBinaryHeader(binaryHeaderSpan);

        var binaryChunk = new GLTFBuffer(
            buffer: buffer,
            offset: 28 + (int)jsonChunkLength,
            size: (int)binaryHeaderSpan[0]
        );

        // Populate missing properties of jsonChunk
        foreach (var accessor in jsonChunk.Accessors)
        {
            accessor.ByteOffset ??= 0;
            accessor.Normalized ??= false;
        }

        foreach (var bufferView in jsonChunk.BufferViews)
        {
            bufferView.ByteOffset ??= 0;
        }

        if (jsonChunk.Samplers != null)
        {
            foreach (var sampler in jsonChunk.Samplers)
            {
                sampler.WrapS ??= 10497; //GL.REPEAT
                sampler.WrapT ??= 10497; //GL.REPEAT
            }
        }

        // Mark each accessor with its intended usage within the vertexShader.
        // Often necessary due to infrequency with which the BufferView target field is populated.
        foreach (var mesh in jsonChunk.Meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                if (primitive.Indices != null)
                {
                    var accessor = jsonChunk.Accessors[primitive.Indices.Value];
                    jsonChunk.Accessors[primitive.Indices.Value].BufferViewUsage |= (int)BufferUsage.Index;
                    jsonChunk.BufferViews[accessor.BufferView!.Value].Usage |= (int)BufferUsage.Index;
                }
                foreach (var attribute in primitive.Attributes.Values)
                {
                    var accessor = jsonChunk.Accessors[attribute];
                    jsonChunk.Accessors[attribute].BufferViewUsage |= (int)BufferUsage.Vertex;
                    jsonChunk.BufferViews[accessor.BufferView!.Value].Usage |= (int)BufferUsage.Vertex;
                }
            }
        }

        // Create GLTFBufferView objects for all the buffer views in the glTF file
        var bufferViews = new List<GLTFBufferView>();
        for (int i = 0; i < jsonChunk.BufferViews.Length; i++)
        {
            bufferViews.Add(new GLTFBufferView(binaryChunk, jsonChunk.BufferViews[i]));
        }

        var accessors = new List<GLTFAccessor>();
        for (int i = 0; i < jsonChunk.Accessors.Length; i++)
        {
            var accessorInfo = jsonChunk.Accessors[i];
            var viewID = accessorInfo.BufferView!.Value;
            accessors.Add(new GLTFAccessor(bufferViews[viewID], accessorInfo));
        }

        // Load meshes
        var meshes = new List<GLTFMesh>();
        foreach (var mesh in jsonChunk.Meshes)
        {
            var meshPrimitives = new List<GLTFPrimitive>();
            foreach (var prim in mesh.Primitives)
            {
                var topology = (GLTFRenderMode)(prim.Mode ?? (int)GLTFRenderMode.Triangles);

                if (topology != GLTFRenderMode.Triangles && topology != GLTFRenderMode.TriangleStrip)
                {
                    throw new Exception($"Unsupported primitive mode {prim.Mode}");
                }

                var primitiveAttributeMap = new Dictionary<string, GLTFAccessor>();
                var attributes = new List<string>();

                if (prim.Indices != null && jsonChunk.Accessors[prim.Indices.Value] != null)
                {
                    var indices = accessors[prim.Indices.Value];
                    primitiveAttributeMap["INDICES"] = indices;
                }

                // Loop through all the attributes and store within our attributeMap
                foreach (var attr in prim.Attributes)
                {
                    var accessor = accessors[attr.Value];
                    primitiveAttributeMap[attr.Key] = accessor;
                    if ((int)accessor.StructureType > 3)
                    {
                        throw new Exception("Vertex attribute accessor accessed an unsupported data type for vertex attribute");
                    }
                    attributes.Add(attr.Key);
                }
                meshPrimitives.Add(new GLTFPrimitive(topology, primitiveAttributeMap, attributes));
            }
            meshes.Add(new GLTFMesh(mesh.Name ?? "UnnamedMesh", [.. meshPrimitives]));
        }

        var skins = new List<GLTFSkin>();
        if (jsonChunk.Skins != null)
        {
            foreach (var skin in jsonChunk.Skins)
            {
                if (skin.InverseBindMatrices.HasValue)
                {
                    var inverseBindMatrixAccessor = accessors[skin.InverseBindMatrices.Value];
                    inverseBindMatrixAccessor.View.AddUsage(BufferUsage.Uniform | BufferUsage.CopyDst);
                    inverseBindMatrixAccessor.View.NeedsUpload = true;
                }
            }
        }

        // Upload the buffer views used by mesh
        foreach (var bufferView in bufferViews)
        {
            if (bufferView.NeedsUpload)
            {
                bufferView.Upload(device);
            }
        }

        GLTFSkin.CreateSharedBindGroupLayout(device);

        if (jsonChunk.Skins != null)
        {
            foreach (var skin in jsonChunk.Skins)
            {
                if (skin.InverseBindMatrices.HasValue)
                {
                    var inverseBindMatrixAccessor = accessors[skin.InverseBindMatrices.Value];
                    var joints = skin.Joints;
                    skins.Add(new GLTFSkin(device, inverseBindMatrixAccessor, joints));
                }
            }
        }

        var nodes = new List<GLTFNode>();

        // Access each node. If node references a mesh, add mesh to that node
        var nodeUniformsBindGroupLayout = device.CreateBindGroupLayout(new()
        {
            Label = "NodeUniforms.bindGroupLayout",
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

        foreach (var currNode in jsonChunk.Nodes)
        {
            var baseTransformation = new BaseTransformation(
                currNode.Translation != null ? new Vector3(currNode.Translation[0], currNode.Translation[1], currNode.Translation[2]) : Vector3.Zero,
                currNode.Rotation != null ? new Quaternion(currNode.Rotation[0], currNode.Rotation[1], currNode.Rotation[2], currNode.Rotation[3]) : Quaternion.Identity,
                currNode.Scale != null ? new Vector3(currNode.Scale[0], currNode.Scale[1], currNode.Scale[2]) : Vector3.One
            );

            GLTFSkin? nodeSkin = null;
            if (currNode.Skin.HasValue && currNode.Skin.Value < skins.Count)
            {
                nodeSkin = skins[currNode.Skin.Value];
            }

            var nodeToCreate = new GLTFNode(device, nodeUniformsBindGroupLayout, baseTransformation, currNode.Name, nodeSkin);

            if (currNode.Mesh.HasValue && currNode.Mesh.Value < meshes.Count)
            {
                var meshToAdd = meshes[currNode.Mesh.Value];
                nodeToCreate.Drawables.Add(meshToAdd);
            }
            nodes.Add(nodeToCreate);
        }

        // Assign each node its children
        for (int idx = 0; idx < nodes.Count; idx++)
        {
            var children = jsonChunk.Nodes[idx].Children;
            if (children != null)
            {
                foreach (var childIdx in children)
                {
                    var child = nodes[childIdx];
                    child.SetParent(nodes[idx]);
                }
            }
        }

        var scenes = new List<GLTFScene>();
        foreach (var jsonScene in jsonChunk.Scenes)
        {
            var scene = new GLTFScene(device, nodeUniformsBindGroupLayout, jsonScene);
            var sceneChildren = scene.Nodes;
            if (sceneChildren != null)
            {
                foreach (var childIdx in sceneChildren)
                {
                    var child = nodes[childIdx];
                    child.SetParent(scene.Root);
                }
            }
            scenes.Add(scene);
        }

        return new ConvertGLBResult([.. meshes], [.. nodes], [.. scenes], [.. skins]);
    }
}

public class BaseTransformation
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }

    public BaseTransformation(Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null)
    {
        Position = position ?? Vector3.Zero;
        Rotation = rotation ?? Quaternion.Identity;
        Scale = scale ?? Vector3.One;
    }

    public Matrix4x4 GetMatrix()
    {
        // Analagous to let transformationMatrix: mat4x4f = translation * rotation * scale;
        var dst = Matrix4x4.Identity;
        // Scale the transformation Matrix
        dst = Matrix4x4.CreateScale(Scale);
        // Calculate the rotationMatrix from the quaternion
        var rotationMatrix = Matrix4x4.CreateFromQuaternion(Rotation);
        // Apply the rotation Matrix to the scaleMatrix (rotMat * scaleMat)
        dst = rotationMatrix * dst;
        // Translate the transformationMatrix
        dst = Matrix4x4.CreateTranslation(Position) * dst;
        return dst;
    }
}

public class GLTFBuffer
{
    public byte[] Buffer { get; }

    public GLTFBuffer(byte[] buffer, int offset, int size)
    {
        Buffer = new byte[size];
        Array.Copy(buffer, offset, Buffer, 0, size);
    }
}

class GLTFBufferView
{
    public int ByteLength { get; }
    public int ByteStride { get; }
    public byte[] View { get; }
    public bool NeedsUpload { get; set; }
    public GPUBuffer? GpuBuffer { get; private set; }
    public BufferUsage Usage { get; private set; }

    public GLTFBufferView(GLTFBuffer buffer, BufferView view)
    {
        ByteLength = view.ByteLength;
        ByteStride = view.ByteStride ?? 0;

        int viewOffset = view.ByteOffset ?? 0;
        View = new byte[ByteLength];
        Array.Copy(buffer.Buffer, viewOffset, View, 0, ByteLength);

        NeedsUpload = false;
        Usage = 0;
    }

    public void AddUsage(BufferUsage usage)
    {
        Usage |= usage;
    }

    public void Upload(Device device)
    {
        // Note: must align to 4 byte size when mapped at creation is true
        var buf = device.CreateBuffer(new()
        {
            Size = GlbUtils.AlignTo((uint)View.Length, 4),
            Usage = Usage,
            MappedAtCreation = true,
        });
        buf.GetMappedRange(i => View.CopyTo(i));
        buf.Unmap();
        GpuBuffer = buf;
        NeedsUpload = false;
    }
}

class GLTFAccessor
{
    public int Count { get; }
    public GLTFDataComponentType ComponentType { get; }
    public GLTFDataStructureType StructureType { get; }
    public GLTFBufferView View { get; }
    public int ByteOffset { get; }

    public GLTFAccessor(GLTFBufferView view, Accessor accessor)
    {
        Count = accessor.Count;
        ComponentType = (GLTFDataComponentType)accessor.ComponentType;
        StructureType = ParseGltfDataStructureType(accessor.Type);
        View = view;
        ByteOffset = accessor.ByteOffset ?? 0;
    }

    private static GLTFDataStructureType ParseGltfDataStructureType(string type)
    {
        return type switch
        {
            "SCALAR" => GLTFDataStructureType.Scalar,
            "VEC2" => GLTFDataStructureType.Vec2,
            "VEC3" => GLTFDataStructureType.Vec3,
            "VEC4" => GLTFDataStructureType.Vec4,
            "MAT2" => GLTFDataStructureType.Mat2,
            "MAT3" => GLTFDataStructureType.Mat3,
            "MAT4" => GLTFDataStructureType.Mat4,
            _ => throw new Exception($"Unhandled glTF Type {type}")
        };
    }

    public int ByteStride
    {
        get
        {
            var elementSize = GltfElementSize(ComponentType, StructureType);
            return Math.Max(elementSize, View.ByteStride);
        }
    }

    private static int GltfElementSize(
        GLTFDataComponentType componentType,
        GLTFDataStructureType type)
    {
        int componentSize = componentType switch
        {
            GLTFDataComponentType.Byte => 1,
            GLTFDataComponentType.UnsignedByte => 1,
            GLTFDataComponentType.Short => 2,
            GLTFDataComponentType.UnsignedShort => 2,
            GLTFDataComponentType.Int => 4,
            GLTFDataComponentType.UnsignedInt => 4,
            GLTFDataComponentType.Float => 4,
            GLTFDataComponentType.Double => 8,
            _ => throw new Exception("Unrecognized GLTF Component Type?")
        };
        return GltfDataStructureTypeNumComponents(type) * componentSize;
    }

    private static int GltfDataStructureTypeNumComponents(GLTFDataStructureType type)
    {
        return type switch
        {
            GLTFDataStructureType.Scalar => 1,
            GLTFDataStructureType.Vec2 => 2,
            GLTFDataStructureType.Vec3 => 3,
            GLTFDataStructureType.Vec4 or GLTFDataStructureType.Mat2 => 4,
            GLTFDataStructureType.Mat3 => 9,
            GLTFDataStructureType.Mat4 => 16,
            _ => throw new Exception($"Invalid glTF Type {type}")
        };
    }

    public int ByteLength => Count * ByteStride;

    // Get the vertex attribute type for accessors that are used as vertex attributes
    public VertexFormat VertexType => GltfVertexType(ComponentType, StructureType);

    private static VertexFormat GltfVertexType(
        GLTFDataComponentType componentType,
        GLTFDataStructureType type)
    {
        string typeStr = componentType switch
        {
            GLTFDataComponentType.Byte => "sint8",
            GLTFDataComponentType.UnsignedByte => "uint8",
            GLTFDataComponentType.Short => "sint16",
            GLTFDataComponentType.UnsignedShort => "uint16",
            GLTFDataComponentType.Int => "int32",
            GLTFDataComponentType.UnsignedInt => "uint32",
            GLTFDataComponentType.Float => "float32",
            _ => throw new Exception($"Unrecognized or unsupported glTF type {componentType}")
        };

        string suffix = GltfDataStructureTypeNumComponents(type) switch
        {
            1 => "",
            2 => "x2",
            3 => "x3",
            4 => "x4",
            _ => throw new Exception($"Invalid number of components for gltfType: {type}")
        };

        var formatStr = typeStr + suffix;
        return Enum.Parse<VertexFormat>(formatStr, ignoreCase: true);
    }
}

class GLTFPrimitive
{
    public GLTFRenderMode Topology { get; }
    public RenderPipeline? RenderPipeline { get; private set; }
    private readonly Dictionary<string, GLTFAccessor> _attributeMap;
    private readonly List<string> _attributes;

    public GLTFPrimitive(
        GLTFRenderMode topology,
        Dictionary<string, GLTFAccessor> attributeMap,
        List<string> attributes)
    {
        Topology = topology;
        _attributeMap = attributeMap;
        _attributes = attributes;

        foreach (var kvp in _attributeMap)
        {
            kvp.Value.View.NeedsUpload = true;
            if (kvp.Key == "INDICES")
            {
                kvp.Value.View.AddUsage(BufferUsage.Index);
                continue;
            }
            kvp.Value.View.AddUsage(BufferUsage.Vertex);
        }
    }

    public void BuildRenderPipeline(
        Device device,
        string vertexShader,
        string fragmentShader,
        TextureFormat colorFormat,
        TextureFormat depthFormat,
        BindGroupLayout[] bgLayouts,
        string label)
    {
        // Build vertex input shader string
        var vertexInputShaderString = new StringBuilder("struct VertexInput {\n");
        var vertexBuffers = new List<VertexBufferLayout>();

        for (int idx = 0; idx < _attributes.Count; idx++)
        {
            var attr = _attributes[idx];
            var vertexFormat = _attributeMap[attr].VertexType;
            var attrString = attr.ToLower().Replace("_0", "");
            vertexInputShaderString.Append($"\t@location({idx}) {attrString}: {ConvertGPUVertexFormatToWGSLFormat(vertexFormat)},\n");

            vertexBuffers.Add(new VertexBufferLayout
            {
                ArrayStride = (ulong)_attributeMap[attr].ByteStride,
                Attributes =
                [
                    new VertexAttribute
                    {
                        Format = _attributeMap[attr].VertexType,
                        Offset = (ulong)_attributeMap[attr].ByteOffset,
                        ShaderLocation = (uint)idx,
                    }
                ],
            });
        }
        vertexInputShaderString.Append('}');

        var vertexState = new VertexState
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexInputShaderString.ToString() + vertexShader
            }),
            Buffers = [.. vertexBuffers],
        };

        var fragmentState = new FragmentState
        {
            Module = device.CreateShaderModuleWGSL(new()
            {
                Code = vertexInputShaderString.ToString() + fragmentShader
            }),
            Targets = [new ColorTargetState { Format = colorFormat }],
        };

        var primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList };
        if (Topology == GLTFRenderMode.TriangleStrip)
        {
            primitive.Topology = PrimitiveTopology.TriangleStrip;
            primitive.StripIndexFormat = _attributeMap["INDICES"].VertexType switch
            {
                VertexFormat.Uint16 => IndexFormat.Uint16,
                VertexFormat.Uint32 => IndexFormat.Uint32,
                _ => throw new Exception("Unsupported index format for triangle strip"),
            };
        }

        var layout = device.CreatePipelineLayout(new()
        {
            BindGroupLayouts = bgLayouts,
            Label = $"{label}.pipelineLayout",
        });

        var rpDescript = new RenderPipelineDescriptor
        {
            Layout = layout,
            Label = $"{label}.pipeline",
            Vertex = vertexState,
            Fragment = fragmentState,
            Primitive = primitive,
            DepthStencil = new()
            {
                Format = depthFormat,
                DepthWriteEnabled = OptionalBool.True,
                DepthCompare = CompareFunction.Less,
            },
        };

        RenderPipeline = device.CreateRenderPipelineSync(rpDescript);
    }

    private static string ConvertGPUVertexFormatToWGSLFormat(VertexFormat vertexFormat)
    {
        return vertexFormat switch
        {
            VertexFormat.Float32 => "f32",
            VertexFormat.Float32x2 => "vec2f",
            VertexFormat.Float32x3 => "vec3f",
            VertexFormat.Float32x4 => "vec4f",
            VertexFormat.Uint32 => "u32",
            VertexFormat.Uint32x2 => "vec2u",
            VertexFormat.Uint32x3 => "vec3u",
            VertexFormat.Uint32x4 => "vec4u",
            VertexFormat.Uint8x2 => "vec2u",
            VertexFormat.Uint8x4 => "vec4u",
            VertexFormat.Uint16x4 => "vec4u",
            VertexFormat.Uint16x2 => "vec2u",
            _ => "f32"
        };
    }

    public void Render(RenderPassEncoder renderPassEncoder, BindGroup[] bindGroups)
    {
        if (RenderPipeline == null) return;

        renderPassEncoder.SetPipeline(RenderPipeline);

        for (int idx = 0; idx < bindGroups.Length; idx++)
        {
            renderPassEncoder.SetBindGroup((uint)idx, bindGroups[idx]);
        }

        for (int idx = 0; idx < _attributes.Count; idx++)
        {
            var attr = _attributes[idx];
            renderPassEncoder.SetVertexBuffer(
                (uint)idx,
                _attributeMap[attr].View.GpuBuffer!,
                (ulong)_attributeMap[attr].ByteOffset,
                (ulong)_attributeMap[attr].ByteLength
            );
        }

        if (_attributeMap.ContainsKey("INDICES"))
        {
            renderPassEncoder.SetIndexBuffer(
                _attributeMap["INDICES"].View.GpuBuffer!,
                _attributeMap["INDICES"].VertexType == VertexFormat.Uint16 ? IndexFormat.Uint16 : IndexFormat.Uint32,
                (ulong)_attributeMap["INDICES"].ByteOffset,
                (ulong)_attributeMap["INDICES"].ByteLength
            );
            renderPassEncoder.DrawIndexed((uint)_attributeMap["INDICES"].Count);
        }
        else
        {
            renderPassEncoder.Draw((uint)_attributeMap["POSITION"].Count);
        }
    }
}

class GLTFMesh
{
    public string Name { get; }
    public GLTFPrimitive[] Primitives { get; }

    public GLTFMesh(string name, GLTFPrimitive[] primitives)
    {
        Name = name;
        Primitives = primitives;
    }

    public void BuildRenderPipeline(
        Device device,
        string vertexShader,
        string fragmentShader,
        TextureFormat colorFormat,
        TextureFormat depthFormat,
        BindGroupLayout[] bgLayouts)
    {
        for (int i = 0; i < Primitives.Length; i++)
        {
            Primitives[i].BuildRenderPipeline(
                device,
                vertexShader,
                fragmentShader,
                colorFormat,
                depthFormat,
                bgLayouts,
                $"PrimitivePipeline{i}"
            );
        }
    }

    public void Render(RenderPassEncoder renderPassEncoder, BindGroup[] bindGroups)
    {
        foreach (var primitive in Primitives)
        {
            primitive.Render(renderPassEncoder, bindGroups);
        }
    }
}

class GLTFNode
{
    public string Name { get; }
    public BaseTransformation Source { get; }
    public GLTFNode? Parent { get; private set; }
    public List<GLTFNode> Children { get; } = new();
    public Matrix4x4 LocalMatrix { get; private set; }
    public Matrix4x4 WorldMatrix { get; private set; }
    public List<GLTFMesh> Drawables { get; } = new();
    public GLTFSkin? Skin { get; }
    public BindGroupLayout NodeUniformsBGL { get; }
    private readonly GPUBuffer _nodeTransformGPUBuffer;
    private readonly BindGroup _nodeTransformBindGroup;

    public GLTFNode(
        Device device,
        BindGroupLayout bgLayout,
        BaseTransformation source,
        string? name = null,
        GLTFSkin? skin = null)
    {
        Name = name ?? $"node_{source.Position}_{source.Rotation}_{source.Scale}";
        Source = source;
        LocalMatrix = Matrix4x4.Identity;
        WorldMatrix = Matrix4x4.Identity;
        Skin = skin;
        NodeUniformsBGL = bgLayout;

        _nodeTransformGPUBuffer = device.CreateBuffer(new()
        {
            Size = 64, // sizeof(Matrix4x4)
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
        });

        _nodeTransformBindGroup = device.CreateBindGroup(new()
        {
            Layout = bgLayout,
            Entries =
            [
                new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = _nodeTransformGPUBuffer,
                }
            ],
        });
    }

    public void SetParent(GLTFNode parent)
    {
        if (Parent != null)
        {
            Parent.RemoveChild(this);
            Parent = null;
        }
        parent.AddChild(this);
        Parent = parent;
    }

    public void UpdateWorldMatrix(Device device, Matrix4x4? parentWorldMatrix = null)
    {
        LocalMatrix = Source.GetMatrix();
        if (parentWorldMatrix.HasValue)
        {
            WorldMatrix = parentWorldMatrix.Value * LocalMatrix;
        }
        else
        {
            WorldMatrix = LocalMatrix;
        }

        device.GetQueue().WriteBuffer(_nodeTransformGPUBuffer, 0, WorldMatrix);

        foreach (var child in Children)
        {
            child.UpdateWorldMatrix(device, WorldMatrix);
        }
    }

    public void Traverse(Action<GLTFNode> fn)
    {
        fn(this);
        foreach (var child in Children)
        {
            child.Traverse(fn);
        }
    }

    public void RenderDrawables(RenderPassEncoder passEncoder, BindGroup[] bindGroups)
    {
        if (Drawables.Count > 0)
        {
            foreach (var drawable in Drawables)
            {
                if (Skin != null)
                {
                    var allBindGroups = new List<BindGroup>(bindGroups);
                    allBindGroups.Add(_nodeTransformBindGroup);
                    allBindGroups.Add(Skin.SkinBindGroup);
                    drawable.Render(passEncoder, [.. allBindGroups]);
                }
                else
                {
                    var allBindGroups = new List<BindGroup>(bindGroups);
                    allBindGroups.Add(_nodeTransformBindGroup);
                    drawable.Render(passEncoder, [.. allBindGroups]);
                }
            }
        }

        foreach (var child in Children)
        {
            child.RenderDrawables(passEncoder, bindGroups);
        }
    }

    private void AddChild(GLTFNode child)
    {
        Children.Add(child);
    }

    private void RemoveChild(GLTFNode child)
    {
        Children.Remove(child);
    }
}

class GLTFScene
{
    public int[]? Nodes { get; }
    public GLTFNode Root { get; }
    public string? Name { get; }

    public GLTFScene(Device device, BindGroupLayout nodeTransformBGL, Scene baseScene)
    {
        Nodes = baseScene.Nodes;
        Name = baseScene.Name;
        Root = new GLTFNode(device, nodeTransformBGL, new BaseTransformation(), baseScene.Name);
    }
}

class GLTFSkin
{
    public int[] Joints { get; }
    public BindGroup SkinBindGroup { get; }
    private readonly float[] _inverseBindMatrices;
    private readonly GPUBuffer _jointMatricesUniformBuffer;
    private readonly GPUBuffer _inverseBindMatricesUniformBuffer;
    public static BindGroupLayout? SkinBindGroupLayout { get; private set; }

    public static void CreateSharedBindGroupLayout(Device device)
    {
        SkinBindGroupLayout = device.CreateBindGroupLayout(new()
        {
            Label = "StaticGLTFSkin.bindGroupLayout",
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
    }

    public GLTFSkin(Device device, GLTFAccessor inverseBindMatricesAccessor, int[] joints)
    {
        if (inverseBindMatricesAccessor.ComponentType != GLTFDataComponentType.Float ||
            inverseBindMatricesAccessor.ByteStride != 64)
        {
            throw new Exception("This skin's provided accessor does not access a mat4x4f matrix, or does not access the provided mat4x4f data correctly");
        }

        _inverseBindMatrices = MemoryMarshal.Cast<byte, float>(inverseBindMatricesAccessor.View.View).ToArray();
        Joints = joints;

        var skinGPUBufferSize = (ulong)(sizeof(float) * 16 * joints.Length);
        _jointMatricesUniformBuffer = device.CreateBuffer(new()
        {
            Size = skinGPUBufferSize,
            Usage = BufferUsage.Storage | BufferUsage.CopyDst,
        });

        _inverseBindMatricesUniformBuffer = device.CreateBuffer(new()
        {
            Size = skinGPUBufferSize,
            Usage = BufferUsage.Storage | BufferUsage.CopyDst,
        });

        device.GetQueue().WriteBuffer(_inverseBindMatricesUniformBuffer, 0, _inverseBindMatrices);

        SkinBindGroup = device.CreateBindGroup(new()
        {
            Layout = SkinBindGroupLayout!,
            Label = "StaticGLTFSkin.bindGroup",
            Entries =
            [
                new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = _jointMatricesUniformBuffer,
                },
                new BindGroupEntry
                {
                    Binding = 1,
                    Buffer = _inverseBindMatricesUniformBuffer,
                }
            ],
        });
    }

    public void Update(Device device, int currentNodeIndex, GLTFNode[] nodes)
    {
        Matrix4x4.Invert(nodes[currentNodeIndex].WorldMatrix, out var globalWorldInverse);

        for (int j = 0; j < Joints.Length; j++)
        {
            var joint = Joints[j];
            var dstMatrix = globalWorldInverse * nodes[joint].WorldMatrix;
            device.GetQueue().WriteBuffer(_jointMatricesUniformBuffer, (ulong)(j * 64), dstMatrix);
        }
    }
}
