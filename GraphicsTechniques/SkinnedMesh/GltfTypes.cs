using System.Numerics;
using System.Text.Json.Serialization;

// GLTF Type definitions based on https://github.com/bwasty/gltf-loader-ts
// Modified for C# and WebGpuSharp

class AccessorSparseIndices
{
    [JsonPropertyName("bufferView")]
    public int BufferView { get; set; }

    [JsonPropertyName("byteOffset")]
    public int? ByteOffset { get; set; }

    [JsonPropertyName("componentType")]
    public int ComponentType { get; set; }
}

class AccessorSparseValues
{
    [JsonPropertyName("bufferView")]
    public int BufferView { get; set; }

    [JsonPropertyName("byteOffset")]
    public int? ByteOffset { get; set; }
}

class AccessorSparse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("indices")]
    public AccessorSparseIndices? Indices { get; set; }

    [JsonPropertyName("values")]
    public AccessorSparseValues? Values { get; set; }
}

class Accessor
{
    [JsonPropertyName("bufferView")]
    public int? BufferView { get; set; }

    [JsonIgnore]
    public int BufferViewUsage { get; set; }

    [JsonPropertyName("byteOffset")]
    public int? ByteOffset { get; set; }

    [JsonPropertyName("componentType")]
    public int ComponentType { get; set; }

    [JsonPropertyName("normalized")]
    public bool? Normalized { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("max")]
    public float[]? Max { get; set; }

    [JsonPropertyName("min")]
    public float[]? Min { get; set; }

    [JsonPropertyName("sparse")]
    public AccessorSparse? Sparse { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class AnimationChannelTarget
{
    [JsonPropertyName("node")]
    public int? Node { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}

class AnimationChannel
{
    [JsonPropertyName("sampler")]
    public int Sampler { get; set; }

    [JsonPropertyName("target")]
    public AnimationChannelTarget? Target { get; set; }
}

class AnimationSampler
{
    [JsonPropertyName("input")]
    public int Input { get; set; }

    [JsonPropertyName("interpolation")]
    public string? Interpolation { get; set; }

    [JsonPropertyName("output")]
    public int Output { get; set; }
}

class Animation
{
    [JsonPropertyName("channels")]
    public AnimationChannel[] Channels { get; set; } = [];

    [JsonPropertyName("samplers")]
    public AnimationSampler[] Samplers { get; set; } = [];

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class Asset
{
    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("generator")]
    public string? Generator { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("minVersion")]
    public string? MinVersion { get; set; }
}

class GltfBuffer
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("byteLength")]
    public int ByteLength { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class BufferView
{
    [JsonPropertyName("buffer")]
    public int Buffer { get; set; }

    [JsonPropertyName("byteOffset")]
    public int? ByteOffset { get; set; }

    [JsonPropertyName("byteLength")]
    public int ByteLength { get; set; }

    [JsonPropertyName("byteStride")]
    public int? ByteStride { get; set; }

    [JsonPropertyName("target")]
    public int? Target { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonIgnore]
    public int Usage { get; set; }
}

class CameraOrthographic
{
    [JsonPropertyName("xmag")]
    public float Xmag { get; set; }

    [JsonPropertyName("ymag")]
    public float Ymag { get; set; }

    [JsonPropertyName("zfar")]
    public float Zfar { get; set; }

    [JsonPropertyName("znear")]
    public float Znear { get; set; }
}

class CameraPerspective
{
    [JsonPropertyName("aspectRatio")]
    public float? AspectRatio { get; set; }

    [JsonPropertyName("yfov")]
    public float Yfov { get; set; }

    [JsonPropertyName("zfar")]
    public float? Zfar { get; set; }

    [JsonPropertyName("znear")]
    public float Znear { get; set; }
}

class Camera
{
    [JsonPropertyName("orthographic")]
    public CameraOrthographic? Orthographic { get; set; }

    [JsonPropertyName("perspective")]
    public CameraPerspective? Perspective { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class Image
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("bufferView")]
    public int? BufferView { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class TextureInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("texCoord")]
    public int? TexCoord { get; set; }
}

class MaterialPbrMetallicRoughness
{
    [JsonPropertyName("baseColorFactor")]
    public float[]? BaseColorFactor { get; set; }

    [JsonPropertyName("baseColorTexture")]
    public TextureInfo? BaseColorTexture { get; set; }

    [JsonPropertyName("metallicFactor")]
    public float? MetallicFactor { get; set; }

    [JsonPropertyName("roughnessFactor")]
    public float? RoughnessFactor { get; set; }

    [JsonPropertyName("metallicRoughnessTexture")]
    public TextureInfo? MetallicRoughnessTexture { get; set; }
}

class MaterialNormalTextureInfo
{
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("texCoord")]
    public int? TexCoord { get; set; }

    [JsonPropertyName("scale")]
    public float? Scale { get; set; }
}

class MaterialOcclusionTextureInfo
{
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("texCoord")]
    public int? TexCoord { get; set; }

    [JsonPropertyName("strength")]
    public float? Strength { get; set; }
}

class Material
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("pbrMetallicRoughness")]
    public MaterialPbrMetallicRoughness? PbrMetallicRoughness { get; set; }

    [JsonPropertyName("normalTexture")]
    public MaterialNormalTextureInfo? NormalTexture { get; set; }

    [JsonPropertyName("occlusionTexture")]
    public MaterialOcclusionTextureInfo? OcclusionTexture { get; set; }

    [JsonPropertyName("emissiveTexture")]
    public TextureInfo? EmissiveTexture { get; set; }

    [JsonPropertyName("emissiveFactor")]
    public float[]? EmissiveFactor { get; set; }

    [JsonPropertyName("alphaMode")]
    public string? AlphaMode { get; set; }

    [JsonPropertyName("alphaCutoff")]
    public float? AlphaCutoff { get; set; }

    [JsonPropertyName("doubleSided")]
    public bool? DoubleSided { get; set; }
}

class MeshPrimitive
{
    [JsonPropertyName("attributes")]
    public Dictionary<string, int> Attributes { get; set; } = new();

    [JsonPropertyName("indices")]
    public int? Indices { get; set; }

    [JsonPropertyName("material")]
    public int? Material { get; set; }

    [JsonPropertyName("mode")]
    public int? Mode { get; set; }

    [JsonPropertyName("targets")]
    public Dictionary<string, int>[]? Targets { get; set; }
}

class Mesh
{
    [JsonPropertyName("primitives")]
    public MeshPrimitive[] Primitives { get; set; } = [];

    [JsonPropertyName("weights")]
    public float[]? Weights { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class Node
{
    [JsonPropertyName("camera")]
    public int? Camera { get; set; }

    [JsonPropertyName("children")]
    public int[]? Children { get; set; }

    [JsonPropertyName("skin")]
    public int? Skin { get; set; }

    [JsonPropertyName("matrix")]
    public float[]? Matrix { get; set; }

    [JsonIgnore]
    public Matrix4x4? WorldTransformationMatrix { get; set; }

    [JsonPropertyName("mesh")]
    public int? Mesh { get; set; }

    [JsonPropertyName("rotation")]
    public float[]? Rotation { get; set; }

    [JsonPropertyName("scale")]
    public float[]? Scale { get; set; }

    [JsonPropertyName("translation")]
    public float[]? Translation { get; set; }

    [JsonPropertyName("weights")]
    public float[]? Weights { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class Sampler
{
    [JsonPropertyName("magFilter")]
    public int? MagFilter { get; set; }

    [JsonPropertyName("minFilter")]
    public int? MinFilter { get; set; }

    [JsonPropertyName("wrapS")]
    public int? WrapS { get; set; }

    [JsonPropertyName("wrapT")]
    public int? WrapT { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class Scene
{
    [JsonPropertyName("nodes")]
    public int[]? Nodes { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class Skin
{
    [JsonPropertyName("inverseBindMatrices")]
    public int? InverseBindMatrices { get; set; }

    [JsonPropertyName("skeleton")]
    public int? Skeleton { get; set; }

    [JsonPropertyName("joints")]
    public int[] Joints { get; set; } = [];

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class Texture
{
    [JsonPropertyName("sampler")]
    public int? Sampler { get; set; }

    [JsonPropertyName("source")]
    public int? Source { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

class GlTf
{
    [JsonPropertyName("extensionsUsed")]
    public string[]? ExtensionsUsed { get; set; }

    [JsonPropertyName("extensionsRequired")]
    public string[]? ExtensionsRequired { get; set; }

    [JsonPropertyName("accessors")]
    public Accessor[] Accessors { get; set; } = [];

    [JsonPropertyName("animations")]
    public Animation[]? Animations { get; set; }

    [JsonPropertyName("asset")]
    public Asset Asset { get; set; } = new();

    [JsonPropertyName("buffers")]
    public GltfBuffer[]? Buffers { get; set; }

    [JsonPropertyName("bufferViews")]
    public BufferView[] BufferViews { get; set; } = [];

    [JsonPropertyName("cameras")]
    public Camera[]? Cameras { get; set; }

    [JsonPropertyName("images")]
    public Image[]? Images { get; set; }

    [JsonPropertyName("materials")]
    public Material[]? Materials { get; set; }

    [JsonPropertyName("meshes")]
    public Mesh[] Meshes { get; set; } = [];

    [JsonPropertyName("nodes")]
    public Node[] Nodes { get; set; } = [];

    [JsonPropertyName("samplers")]
    public Sampler[]? Samplers { get; set; }

    [JsonPropertyName("scene")]
    public int? Scene { get; set; }

    [JsonPropertyName("scenes")]
    public Scene[] Scenes { get; set; } = [];

    [JsonPropertyName("skins")]
    public Skin[]? Skins { get; set; }

    [JsonPropertyName("textures")]
    public Texture[]? Textures { get; set; }
}
