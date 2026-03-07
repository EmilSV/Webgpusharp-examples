using WebGpuSharp;

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
            GLTFDataComponentType.Byte => sizeof(sbyte),
            GLTFDataComponentType.UnsignedByte => sizeof(byte),
            GLTFDataComponentType.Short => sizeof(short),
            GLTFDataComponentType.UnsignedShort => sizeof(ushort),
            GLTFDataComponentType.Int => sizeof(int),
            GLTFDataComponentType.UnsignedInt => sizeof(uint),
            GLTFDataComponentType.Float => sizeof(float),
            GLTFDataComponentType.Double => sizeof(double),
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
