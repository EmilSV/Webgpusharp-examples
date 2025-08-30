struct VSOut {
    @builtin(position) position : vec4f,
    @location(0) uv : vec2f,
};

@vertex
fn vs(@builtin(vertex_index) vid : u32) -> VSOut {
    // Full-screen quad made of 2 triangles (6 verts via index map)
    let quad = array<vec2f,4>(
        vec2f(-1.0, -1.0),
        vec2f( 1.0, -1.0),
        vec2f(-1.0,  1.0),
        vec2f( 1.0,  1.0)
    );
    // Flip Y here if your offscreen appears upside-down
    let uvs = array<vec2f,4>(
        vec2f(0.0, 1.0),
        vec2f(1.0, 1.0),
        vec2f(0.0, 0.0),
        vec2f(1.0, 0.0)
    );
    let indexMap = array<u32,6>(0u, 1u, 2u, 2u, 1u, 3u);
    let i = indexMap[vid];
    var o : VSOut;
    o.position = vec4f(quad[i], 0.0, 1.0);
    o.uv = uvs[i];
    return o;
}

@group(0) @binding(0) var blitSampler : sampler;
@group(0) @binding(1) var blitTex : texture_2d<f32>;

@fragment
fn fs(in : VSOut) -> @location(0) vec4f {
    // Force base level (only one mip anyway) + nearest sampler
    return textureSampleLevel(blitTex, blitSampler, in.uv, 0.0);
}