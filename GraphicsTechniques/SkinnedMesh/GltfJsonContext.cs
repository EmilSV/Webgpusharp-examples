using System.Text.Json.Serialization;

// JSON source generator context for GLTF serialization
// This enables AOT-compatible JSON serialization without reflection

[JsonSerializable(typeof(GlTf))]
[JsonSerializable(typeof(Accessor))]
[JsonSerializable(typeof(AccessorSparse))]
[JsonSerializable(typeof(AccessorSparseIndices))]
[JsonSerializable(typeof(AccessorSparseValues))]
[JsonSerializable(typeof(Animation))]
[JsonSerializable(typeof(AnimationChannel))]
[JsonSerializable(typeof(AnimationChannelTarget))]
[JsonSerializable(typeof(AnimationSampler))]
[JsonSerializable(typeof(Asset))]
[JsonSerializable(typeof(GltfBuffer))]
[JsonSerializable(typeof(BufferView))]
[JsonSerializable(typeof(Camera))]
[JsonSerializable(typeof(CameraOrthographic))]
[JsonSerializable(typeof(CameraPerspective))]
[JsonSerializable(typeof(Image))]
[JsonSerializable(typeof(Material))]
[JsonSerializable(typeof(MaterialPbrMetallicRoughness))]
[JsonSerializable(typeof(MaterialNormalTextureInfo))]
[JsonSerializable(typeof(MaterialOcclusionTextureInfo))]
[JsonSerializable(typeof(Mesh))]
[JsonSerializable(typeof(MeshPrimitive))]
[JsonSerializable(typeof(Node))]
[JsonSerializable(typeof(Sampler))]
[JsonSerializable(typeof(Scene))]
[JsonSerializable(typeof(Skin))]
[JsonSerializable(typeof(Texture))]
[JsonSerializable(typeof(TextureInfo))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class GltfJsonContext : JsonSerializerContext
{
}
