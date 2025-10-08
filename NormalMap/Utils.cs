using WebGpuSharp;

using GPUBuffer = WebGpuSharp.Buffer;

sealed class BindGroupsObjectsAndLayout
{
    public required BindGroup[] BindGroups { get; init; }
    public required BindGroupLayout BindGroupLayout { get; init; }
}

readonly struct BindGroupBindingLayout
{
    public object Value { get; private init; }

    public static implicit operator BindGroupBindingLayout(BufferBindingLayout value) => new() { Value = value };
    public static implicit operator BindGroupBindingLayout(SamplerBindingLayout value) => new() { Value = value };
    public static implicit operator BindGroupBindingLayout(TextureBindingLayout value) => new() { Value = value };
    public static implicit operator BindGroupBindingLayout(StorageTextureBindingLayout value) => new() { Value = value };
}

readonly struct BindingResource
{
    public object Value { get; private init; }

    public static implicit operator BindingResource(Sampler value) => new() { Value = value };
    public static implicit operator BindingResource(TextureView value) => new() { Value = value };
    public static implicit operator BindingResource(GPUBuffer value) => new() { Value = value };
}


static class Utils
{
    public static BindGroupsObjectsAndLayout CreateBindGroupDescriptor(
        int[] bindings,
        ShaderStage[] visibilities,
        BindGroupBindingLayout[] resourceLayouts,
        BindingResource[][] resources,
        string label,
        Device device
    )
    {
        var layoutEntries = new BindGroupLayoutEntry[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            layoutEntries[i] = new BindGroupLayoutEntry()
            {
                Binding = (uint)bindings[i],
                Visibility = visibilities[i % visibilities.Length],
            };
            switch (resourceLayouts[i].Value)
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
                    throw new Exception($"Unknown resource type: {resourceLayouts[i].Value.GetType().Name}");
            }
        }
        var bindGroupLayout = device.CreateBindGroupLayout(new()
        {
            Label = $"{label}.bindGroupLayout",
            Entries = layoutEntries
        });

        List<BindGroup> bindGroups = [];
        for (int i = 0; i < resources.Length; i++)
        {
            List<BindGroupEntry> groupEntries = [];
            for (int j = 0; j < resources[0].Length; j++)
            {
                BindGroupEntry entry = new()
                {
                    Binding = (uint)j,
                };

                switch (resources[i][j].Value)
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
                Label = $"{label}.bindGroup{i}",
                Layout = bindGroupLayout,
                Entries = groupEntries.ToArray()
            });
            bindGroups.Add(newBindGroup);
        }


        return new BindGroupsObjectsAndLayout()
        {
            BindGroups = [.. bindGroups],
            BindGroupLayout = bindGroupLayout
        };

    }

    public static RenderPipeline Create3DRenderPipeline(
        Device device,
        string label,
        BindGroupLayout[] bindGroupLayouts,
        string vertexShader,
        VertexFormat[] vertexBufferFormats,
        string fragmentShader,
        TextureFormat presentationFormat,
        bool depthTest = false,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        CullMode cullMode = CullMode.Back)
    {
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDescriptor
        {
            Label = $"{label}.pipelineLayout",
            BindGroupLayouts = bindGroupLayouts
        });

        var pipelineDescriptor = new RenderPipelineDescriptor
        {
            Label = $"{label}.pipeline",
            Layout = pipelineLayout,
            Vertex = ref WebGpuUtil.InlineInit(new VertexState
            {
                Module = device.CreateShaderModuleWGSL($"{label}.vertexShader", new()
                {
                    Code = vertexShader
                }),
                Buffers = vertexBufferFormats.Length != 0
                    ? [CreateVBuffer(vertexBufferFormats)]
                    : []
            }),
            Fragment = new FragmentState
            {
                Module = device.CreateShaderModuleWGSL(new()
                {
                    Code = fragmentShader
                }),
                Targets = new[]
                {
                    new ColorTargetState
                    {
                        Format = presentationFormat
                    }
                }
            },
            Primitive = new PrimitiveState
            {
                Topology = topology,
                CullMode = cullMode
            }
        };

        if (depthTest)
        {
            pipelineDescriptor.DepthStencil = new DepthStencilState
            {
                DepthCompare = CompareFunction.Less,
                DepthWriteEnabled = OptionalBool.True,
                Format = TextureFormat.Depth24Plus
            };
        }

        return device.CreateRenderPipeline(pipelineDescriptor);
    }

    public static VertexBufferLayout CreateVBuffer(VertexFormat[] vertexFormats)
    {
        var attributes = new VertexAttribute[vertexFormats.Length];
        uint arrayStride = 0;

        for (int i = 0; i < vertexFormats.Length; i++)
        {
            attributes[i] = new VertexAttribute
            {
                ShaderLocation = (uint)i,
                Offset = arrayStride,
                Format = vertexFormats[i]
            };

            arrayStride += (uint)ConvertVertexFormatToBytes(vertexFormats[i]);
        }

        return new VertexBufferLayout
        {
            ArrayStride = arrayStride,
            Attributes = attributes
        };
    }

    static int ConvertVertexFormatToBytes(VertexFormat format)
    {
        var parts = format.ToString().Split('x');
        var digits = new string(parts[0].Where(char.IsDigit).ToArray());
        var bytesPerElement = int.Parse(digits) / 8;

        var components = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 1;
        return bytesPerElement * components;
    }
}








// type BindGroupBindingLayout =
//   | GPUBufferBindingLayout
//   | GPUTextureBindingLayout
//   | GPUSamplerBindingLayout
//   | GPUStorageTextureBindingLayout
//   | GPUExternalTextureBindingLayout;

// export type BindGroupsObjectsAndLayout = {
//   bindGroups: GPUBindGroup[];
//   bindGroupLayout: GPUBindGroupLayout;
// };

// type ResourceTypeName =
//   | 'buffer'
//   | 'texture'
//   | 'sampler'
//   | 'externalTexture'
//   | 'storageTexture';

// /**
//  * @param {number[]} bindings - The binding value of each resource in the bind group.
//  * @param {number[]} visibilities - The GPUShaderStage visibility of the resource at the corresponding index.
//  * @param {ResourceTypeName[]} resourceTypes - The resourceType at the corresponding index.
//  * @returns {BindGroupsObjectsAndLayout} An object containing an array of bindGroups and the bindGroupLayout they implement.
//  */
// export const createBindGroupDescriptor = (
//   bindings: number[],
//   visibilities: number[],
//   resourceTypes: ResourceTypeName[],
//   resourceLayouts: BindGroupBindingLayout[],
//   resources: GPUBindingResource[][],
//   label: string,
//   device: GPUDevice
// ): BindGroupsObjectsAndLayout => {
//   // Create layout of each entry within a bindGroup
//   const layoutEntries: GPUBindGroupLayoutEntry[] = [];
//   for (let i = 0; i < bindings.length; i++) {
//     layoutEntries.push({
//       binding: bindings[i],
//       visibility: visibilities[i % visibilities.length],
//       [resourceTypes[i]]: resourceLayouts[i],
//     });
//   }

//   // Apply entry layouts to bindGroupLayout
//   const bindGroupLayout = device.createBindGroupLayout({
//     label: `${label}.bindGroupLayout`,
//     entries: layoutEntries,
//   });

//   // Create bindGroups that conform to the layout
//   const bindGroups: GPUBindGroup[] = [];
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

// export type ShaderKeyInterface<T extends string[]> = {
//   [K in T[number]]: number;
// };

// interface AttribAcc {
//   attributes: GPUVertexAttribute[];
//   arrayStride: number;
// }

// /**
//  * @param {GPUVertexFormat} vf - A valid GPUVertexFormat, representing a per-vertex value that can be passed to the vertex shader.
//  * @returns {number} The number of bytes present in the value to be passed.
//  */
// export const convertVertexFormatToBytes = (vf: GPUVertexFormat): number => {
//   const splitFormat = vf.split('x');
//   const bytesPerElement = parseInt(splitFormat[0].replace(/[^0-9]/g, '')) / 8;

//   const bytesPerVec =
//     bytesPerElement *
//     (splitFormat[1] !== undefined ? parseInt(splitFormat[1]) : 1);

//   return bytesPerVec;
// };

// /** Creates a GPUVertexBuffer Layout that maps to an interleaved vertex buffer.
//  * @param {GPUVertexFormat[]} vertexFormats - An array of valid GPUVertexFormats.
//  * @returns {GPUVertexBufferLayout} A GPUVertexBufferLayout representing an interleaved vertex buffer.
//  */
// export const createVBuffer = (
//   vertexFormats: GPUVertexFormat[]
// ): GPUVertexBufferLayout => {
//   const initialValue: AttribAcc = { attributes: [], arrayStride: 0 };

//   const vertexBuffer = vertexFormats.reduce(
//     (acc: AttribAcc, curr: GPUVertexFormat, idx: number) => {
//       const newAttribute: GPUVertexAttribute = {
//         shaderLocation: idx,
//         offset: acc.arrayStride,
//         format: curr,
//       };
//       const nextOffset: number =
//         acc.arrayStride + convertVertexFormatToBytes(curr);

//       const retVal: AttribAcc = {
//         attributes: [...acc.attributes, newAttribute],
//         arrayStride: nextOffset,
//       };
//       return retVal;
//     },
//     initialValue
//   );

//   const layout: GPUVertexBufferLayout = {
//     arrayStride: vertexBuffer.arrayStride,
//     attributes: vertexBuffer.attributes,
//   };

//   return layout;
// };

// export const create3DRenderPipeline = (
//   device: GPUDevice,
//   label: string,
//   bgLayouts: GPUBindGroupLayout[],
//   vertexShader: string,
//   vBufferFormats: GPUVertexFormat[],
//   fragmentShader: string,
//   presentationFormat: GPUTextureFormat,
//   depthTest = false,
//   topology: GPUPrimitiveTopology = 'triangle-list',
//   cullMode: GPUCullMode = 'back'
// ) => {
//   const pipelineDescriptor: GPURenderPipelineDescriptor = {
//     label: `${label}.pipeline`,
//     layout: device.createPipelineLayout({
//       label: `${label}.pipelineLayout`,
//       bindGroupLayouts: bgLayouts,
//     }),
//     vertex: {
//       module: device.createShaderModule({
//         label: `${label}.vertexShader`,
//         code: vertexShader,
//       }),
//       buffers:
//         vBufferFormats.length !== 0 ? [createVBuffer(vBufferFormats)] : [],
//     },
//     fragment: {
//       module: device.createShaderModule({
//         label: `${label}.fragmentShader`,
//         code: fragmentShader,
//       }),
//       targets: [
//         {
//           format: presentationFormat,
//         },
//       ],
//     },
//     primitive: {
//       topology: topology,
//       cullMode: cullMode,
//     },
//   };
//   if (depthTest) {
//     pipelineDescriptor.depthStencil = {
//       depthCompare: 'less',
//       depthWriteEnabled: true,
//       format: 'depth24plus',
//     };
//   }
//   return device.createRenderPipeline(pipelineDescriptor);
// };

// export const createTextureFromImage = (
//   device: GPUDevice,
//   bitmap: ImageBitmap
// ) => {
//   const texture: GPUTexture = device.createTexture({
//     size: [bitmap.width, bitmap.height, 1],
//     format: 'rgba8unorm',
//     usage:
//       GPUTextureUsage.TEXTURE_BINDING |
//       GPUTextureUsage.COPY_DST |
//       GPUTextureUsage.RENDER_ATTACHMENT,
//   });
//   device.queue.copyExternalImageToTexture(
//     { source: bitmap },
//     { texture: texture },
//     [bitmap.width, bitmap.height]
//   );
//   return texture;
// };
