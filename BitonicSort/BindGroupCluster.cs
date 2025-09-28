using WebGpuSharp;

using GPUBuffer = WebGpuSharp.Buffer;

class BindGroupCluster
{
    public required BindGroup[] BindGroups;
    public required BindGroupLayout BindGroupLayout;

    public static BindGroupCluster CreateBindGroupCluster(
        int[] bindings,
        ShaderStage[] visibilities,
        object[] resourceLayouts,
        object[][] resources,
        string label,
        Device device)
    {
        var layoutEntries = new BindGroupLayoutEntry[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            layoutEntries[i] = new BindGroupLayoutEntry()
            {
                Binding = (uint)bindings[i],
                Visibility = visibilities[i % visibilities.Length],
            };
            switch (resourceLayouts[i])
            {
                case BufferBindingLayout bufferBindingLayout:
                    layoutEntries[i].Buffer = bufferBindingLayout;
                    break;
                case SamplerBindingLayout samplerBindingLayout:
                    layoutEntries[i].Sampler = samplerBindingLayout;
                    break;
                case TextureBindingLayout textureBindingLayout:
                    layoutEntries[i].Texture = textureBindingLayout;
                    break;
                case StorageTextureBindingLayout storageTextureBindingLayout:
                    layoutEntries[i].StorageTexture = storageTextureBindingLayout;
                    break;
                default:
                    throw new Exception($"Unknown resource type: {resourceLayouts[i].GetType().Name}");
            }
        }

        var bindGroupLayout = device.CreateBindGroupLayout(new()
        {
            Label = $"{label}.bindGroupLayout",
            Entries = layoutEntries
        });

        List<BindGroup> bindGroups = [];
        //i represent the bindGroup index, j represents the binding index of the resource within the bindgroup
        //i=0, j=0  bindGroup: 0, binding: 0
        //i=1, j=1, bindGroup: 0, binding: 1
        //NOTE: not the same as @group(0) @binding(1) group index within the fragment shader is set within a pipeline
        for (int i = 0; i < resources.Length; i++)
        {
            List<BindGroupEntry> groupEntries = [];
            for (int j = 0; j < resources[0].Length; j++)
            {
                BindGroupEntry entry = new()
                {
                    Binding = (uint)j,
                };

                switch (resources[i][j])
                {
                    case GPUBuffer buffer:
                        entry.Buffer = buffer;
                        break;
                    case Sampler sampler:
                        entry.Sampler = sampler;
                        break;
                    case TextureView textureView:
                        entry.TextureView = textureView;
                        break;
                    default:
                        throw new Exception($"Unknown resource type: {resources[i][j].GetType().Name}");
                }

                groupEntries.Add(entry);
            }
            var newBindGroup = device.CreateBindGroup(new()
            {
                Label = $"{label}.bindGroup${i}",
                Layout = bindGroupLayout,
                Entries = groupEntries.ToArray()
            });
            bindGroups.Add(newBindGroup);
        }


        return new BindGroupCluster()
        {
            BindGroups = [.. bindGroups],
            BindGroupLayout = bindGroupLayout
        };
    }
}


// export const createBindGroupCluster = (
//   bindings: number[],
//   visibilities: number[],
//   resourceTypes: ResourceTypeName[],
//   resourceLayouts: BindGroupBindingLayout[],
//   resources: GPUBindingResource[][],
//   label: string,
//   device: GPUDevice
// ): BindGroupCluster => {
//   const layoutEntries: GPUBindGroupLayoutEntry[] = [];
//   for (let i = 0; i < bindings.length; i++) {
//     layoutEntries.push({
//       binding: bindings[i],
//       visibility: visibilities[i % visibilities.length],
//       [resourceTypes[i]]: resourceLayouts[i],
//     });
//   }

//   const bindGroupLayout = device.createBindGroupLayout({
//     label: `${label}.bindGroupLayout`,
//     entries: layoutEntries,
//   });

//   const bindGroups: GPUBindGroup[] = [];
//   //i represent the bindGroup index, j represents the binding index of the resource within the bindgroup
//   //i=0, j=0  bindGroup: 0, binding: 0
//   //i=1, j=1, bindGroup: 0, binding: 1
//   //NOTE: not the same as @group(0) @binding(1) group index within the fragment shader is set within a pipeline
//   for (let i = 0; i < resources.length; i++) {
//     const groupEntries: GPUBindGroupEntry[] = [];
//     for (let j = 0; j < resources[0].length; j++) {
//       groupEntries.push({
//         binding: j,
//         resource: resources[i][j],
//       });
//     }
//     const newBindGroup = device.createBindGroup({
//       label: `${label}.bindGroup${i}`,
//       layout: bindGroupLayout,
//       entries: groupEntries,
//     });
//     bindGroups.push(newBindGroup);
//   }

//   return {
//     bindGroups,
//     bindGroupLayout,
//   };
// };