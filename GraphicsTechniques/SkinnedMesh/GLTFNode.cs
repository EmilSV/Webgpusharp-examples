using System.Numerics;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

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
            WorldMatrix = LocalMatrix * parentWorldMatrix.Value;
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

    public void RenderDrawables(RenderPassEncoder passEncoder, ReadOnlySpan<BindGroup> bindGroups)
    {
        if (Drawables.Count > 0)
        {
            foreach (var drawable in Drawables)
            {
                if (Skin != null)
                {
                    drawable.Render(passEncoder, [
                        ..bindGroups,
                        _nodeTransformBindGroup,
                        Skin.SkinBindGroup
                    ]);
                }
                else
                {
                    drawable.Render(passEncoder, [
                        ..bindGroups,
                        _nodeTransformBindGroup
                    ]);
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
