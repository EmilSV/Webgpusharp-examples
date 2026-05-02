import cubemapMeta from '../../BasicGraphics/Cubemap/meta.json';
import fractalCubeMeta from '../../BasicGraphics/FractalCube/meta.json';
import helloTriangleMeta from '../../BasicGraphics/HelloTriangle/meta.json';
import helloTriangleMSAAMeta from '../../BasicGraphics/HelloTriangleMSAA/meta.json';
import instancedCubeMeta from '../../BasicGraphics/InstancedCube/meta.json';
import rotatingCubeMeta from '../../BasicGraphics/RotatingCube/meta.json';
import texturedCubeMeta from '../../BasicGraphics/TexturedCube/meta.json';
import twoCubesMeta from '../../BasicGraphics/TwoCubes/meta.json';
import blendingMeta from '../../WebGPUFeatures/Blending/meta.json';
import occlusionQueryMeta from '../../WebGPUFeatures/OcclusionQuery/meta.json';
import renderBundlesMeta from '../../WebGPUFeatures/RenderBundles/meta.json';
import reversedZMeta from '../../WebGPUFeatures/ReversedZ/meta.json';
import samplerParametersMeta from '../../WebGPUFeatures/SamplerParameters/meta.json';
import timestampQueryMeta from '../../WebGPUFeatures/TimestampQuery/meta.json';
import bitonicSortMeta from '../../GPGPUDemos/BitonicSort/meta.json';
import computeBoidsMeta from '../../GPGPUDemos/ComputeBoids/meta.json';
import conwaysGameOfLifeMeta from '../../GPGPUDemos/ConwaysGameOfLife/meta.json';
import aBufferMeta from '../../GraphicsTechniques/ABuffer/meta.json';
import camerasMeta from '../../GraphicsTechniques/Cameras/meta.json';
import cornellMeta from '../../GraphicsTechniques/Cornell/meta.json';
import deferredRenderingMeta from '../../GraphicsTechniques/DeferredRendering/meta.json';
import imageBlurMeta from '../../GraphicsTechniques/ImageBlur/meta.json';
import normalMapMeta from '../../GraphicsTechniques/NormalMap/meta.json';
import particlesMeta from '../../GraphicsTechniques/Particles/meta.json';
import pointsMeta from '../../GraphicsTechniques/Points/meta.json';
import primitivePickingMeta from '../../GraphicsTechniques/PrimitivePicking/meta.json';
import shadowMappingMeta from '../../GraphicsTechniques/ShadowMapping/meta.json';
import skinnedMeshMeta from '../../GraphicsTechniques/SkinnedMesh/meta.json';
import stencilMaskMeta from '../../GraphicsTechniques/StencilMask/meta.json';
import textRenderingMsdfMeta from '../../GraphicsTechniques/TextRenderingMsdf/meta.json';
import volumeRenderingTexture3DMeta from '../../GraphicsTechniques/VolumeRenderingTexture3D/meta.json';
import wireframeMeta from '../../GraphicsTechniques/Wireframe/meta.json';

export type SourceInfo = {
  path: string;
  url?: string;
  repositoryUrl?: string;
};

export type SampleInfo = {
  name: string;
  tocName?: string;
  description: string;
  openInNewTab?: boolean;
  filename: string; // used if sample is local
  external?: { url: string; sourceURL: string; }; // used if sample is remote
  sources: SourceInfo[];
};


export type PageCategory = {
  title: string;
  description: string;
  samples: { [key: string]: SampleInfo; };
};

type MetaSourceInfo = {
  path: string;
};

type MetaInfo = {
  name: string;
  fileName: string;
  description: string | string[];
  sources: MetaSourceInfo[];
};

function normalizePathSegments(filePath: string)
{
  return filePath.replaceAll('\\', '/');
}

function toDescriptionText(description: string | string[])
{
  return Array.isArray(description) ? description.join('\n') : description;
}

function makeSampleInfo(
  meta: MetaInfo
): SampleInfo
{
  return {
    name: meta.name,
    description: toDescriptionText(meta.description),
    filename: meta.fileName,
    sources: meta.sources.map(({ path }) =>
    {
      const relativePath = normalizePathSegments(path);
      return {
        path: relativePath
      };
    }),
  };
}

const cubemap = makeSampleInfo(cubemapMeta);
const fractalCube = makeSampleInfo(fractalCubeMeta);
const helloTriangle = makeSampleInfo(helloTriangleMeta);
const helloTriangleMSAA = makeSampleInfo(helloTriangleMSAAMeta);
const instancedCube = makeSampleInfo(instancedCubeMeta);
const rotatingCube = makeSampleInfo(rotatingCubeMeta);
const texturedCube = makeSampleInfo(texturedCubeMeta);
const twoCubes = makeSampleInfo(twoCubesMeta);
const blending = makeSampleInfo(blendingMeta);
const occlusionQuery = makeSampleInfo(occlusionQueryMeta);
const renderBundles = makeSampleInfo(renderBundlesMeta);
const reversedZ = makeSampleInfo(reversedZMeta);
const samplerParameters = makeSampleInfo(samplerParametersMeta);
const timestampQuery = makeSampleInfo(timestampQueryMeta);
const bitonicSort = makeSampleInfo(bitonicSortMeta);
const computeBoids = makeSampleInfo(computeBoidsMeta);
const gameOfLife = makeSampleInfo(conwaysGameOfLifeMeta);
const aBuffer = makeSampleInfo(aBufferMeta);
const cameras = makeSampleInfo(camerasMeta);
const cornell = makeSampleInfo(cornellMeta);
const deferredRendering = makeSampleInfo(deferredRenderingMeta);
const imageBlur = makeSampleInfo(imageBlurMeta);
const normalMap = makeSampleInfo(normalMapMeta);
const particles = makeSampleInfo(particlesMeta);
const points = makeSampleInfo(pointsMeta);
const primitivePicking = makeSampleInfo(primitivePickingMeta);
const shadowMapping = makeSampleInfo(shadowMappingMeta);
const skinnedMesh = makeSampleInfo(skinnedMeshMeta);
const stencilMask = makeSampleInfo(stencilMaskMeta);
const textRenderingMsdf = makeSampleInfo(textRenderingMsdfMeta);
const volumeRenderingTexture3D = makeSampleInfo(volumeRenderingTexture3DMeta);
const wireframe = makeSampleInfo(wireframeMeta);

export const pageCategories: PageCategory[] = [
  {
    title: 'Basic Graphics',
    description: 'Basic rendering functionality implemented with the WebGPUSharp API.',
    samples: {
      helloTriangle,
      helloTriangleMSAA,
      rotatingCube,
      twoCubes,
      texturedCube,
      instancedCube,
      fractalCube,
      cubemap,
    },
  },
  {
    title: 'WebGPU Features',
    description: 'Highlights of important WebGPU features.',
    samples: {
      reversedZ,
      renderBundles,
      occlusionQuery,
      samplerParameters,
      timestampQuery,
      blending,
    },
  },
  {
    title: 'GPGPU Demos',
    description: 'Visualizations of parallel GPU compute operations.',
    samples: {
      computeBoids,
      gameOfLife,
      bitonicSort,
    },
  },
  {
    title: 'Graphics Techniques',
    description: 'A collection of graphics techniques implemented with WebGPUSharp.',
    samples: {
      cameras,
      normalMap,
      shadowMapping,
      deferredRendering,
      particles,
      points,
      primitivePicking,
      imageBlur,
      cornell,
      aBuffer,
      skinnedMesh,
      stencilMask,
      textRenderingMsdf,
      volumeRenderingTexture3D,
      wireframe,
    },
  },
];
