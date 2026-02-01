using System.Numerics;
using System.Runtime.InteropServices;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

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
            var dstMatrix = nodes[joint].WorldMatrix * globalWorldInverse;
            device.GetQueue().WriteBuffer(_jointMatricesUniformBuffer, (ulong)(j * 64), dstMatrix);
        }
    }
}
