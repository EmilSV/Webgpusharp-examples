
// NOTE: GLTF code is not generally extensible to all gltf models
// Modified from Will Usher code found at this link https://www.willusher.io/graphics/2023/05/16/0-to-gltf-first-mesh

// Associates the mode parameter of a gltf primitive object with the primitive's intended render mode
enum GLTFRenderMode
{
    Points = 0,
    Line = 1,
    LineLoop = 2,
    LineStrip = 3,
    Triangles = 4,
    TriangleStrip = 5,
    TriangleFan = 6,
}
