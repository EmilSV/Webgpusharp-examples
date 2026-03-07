using WebGpuSharp;

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
