using System.Buffers.Binary;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WebGpuSharp;


// NOTE: GLTF code is not generally extensible to all gltf models
// Modified from Will Usher code found at this link https://www.willusher.io/graphics/2023/05/16/0-to-gltf-first-mesh

static class GlbParser
{
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
        static void ValidateGLBHeader(ReadOnlySpan<byte> header)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(header) != 0x46546C67)
            {
                throw new Exception("Provided file is not a glB file");
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) != 2)
            {
                throw new Exception("Provided file is not glTF 2.0 file");
            }
        }

        static void ValidateBinaryHeader(ReadOnlySpan<uint> header)
        {
            if (header[1] != 0x004E4942)
            {
                throw new Exception("Invalid glB: The second chunk of the glB file is not a binary chunk!");
            }
        }


        // Binary GLTF layout: https://cdn.willusher.io/webgpu-0-to-gltf/glb-layout.svg
        var jsonHeaderSpan = buffer.AsSpan(0, 20);
        ValidateGLBHeader(jsonHeaderSpan);

        // Length of the jsonChunk found at jsonHeader[12 - 15]
        uint jsonChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12, 4));

        // Parse the JSON chunk of the glB file to a JSON object
        var jsonChunk = JsonSerializer.Deserialize(buffer.AsSpan(20, (int)jsonChunkLength), GltfJsonContext.Default.GlTf)
            ?? throw new Exception("Failed to deserialize GLTF JSON");

        // Binary data located after jsonChunk
        var binaryHeaderSpan = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(20 + (int)jsonChunkLength, 2 * sizeof(uint)));
        ValidateBinaryHeader(binaryHeaderSpan);

        var binaryChunk = new ReadOnlySpan<byte>(
            array: buffer,
            start: 28 + (int)jsonChunkLength,
            length: (int)binaryHeaderSpan[0]
        );

        // Populate missing properties of jsonChunk
        foreach (var accessor in jsonChunk.Accessors!)
        {
            accessor.ByteOffset ??= 0;
            accessor.Normalized ??= false;
        }

        foreach (var bufferView in jsonChunk.BufferViews!)
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
        foreach (var mesh in jsonChunk.Meshes!)
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

        foreach (var currNode in jsonChunk.Nodes!)
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
        foreach (var jsonScene in jsonChunk.Scenes!)
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
