using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Setup;
using WebGpuSharp;
using static Setup.SetupWebGPU;

// Blending example translated from ref.ts: renders a destination texture first (no blending),
// then a source texture on top using a configurable blend state. Provides ImGui controls
// to switch alpha mode, texture set (premultiplied/unpremultiplied), presets, factors, and constants.

const int WIDTH = 900;
const int HEIGHT = 600;
const int TEX_SIZE = 300;

return Run("Blending", WIDTH, HEIGHT, async (instance, surface, guiContext, onFrame) =>
{
    var adapter = await instance.RequestAdapterAsync(new() { CompatibleSurface = surface });
    var device = await adapter.RequestDeviceAsync(new()
    {
        UncapturedErrorCallback = (type, message) =>
        {
            var messageString = Encoding.UTF8.GetString(message);
            Console.Error.WriteLine($"Uncaptured error: {type} {messageString}");
        },
        DeviceLostCallback = (reason, message) =>
        {
            var messageString = Encoding.UTF8.GetString(message);
            Console.Error.WriteLine($"Device lost: {reason} {messageString}");
        },
    });
    var queue = device.GetQueue();

    var surfaceCaps = surface.GetCapabilities(adapter)!;
    var surfaceFormat = surfaceCaps.Formats[0];

    guiContext.SetupIMGUI(device, surfaceFormat);

    surface.Configure(new()
    {
        Width = WIDTH,
        Height = HEIGHT,
        Usage = TextureUsage.RenderAttachment,
        Format = surfaceFormat,
        Device = device,
        PresentMode = PresentMode.Fifo,
        AlphaMode = CompositeAlphaMode.Premultiplied,
    });

    // Load shader (embedded resource via csproj)
    static byte[] ToBytes(Stream s)
    {
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }
    var asm = Assembly.GetExecutingAssembly();
    var texturedQuadWGSL = ToBytes(asm.GetManifestResourceStream("Blending.shaders.texturedQuad.wgsl")!);
    var shaderModule = device.CreateShaderModuleWGSL(new() { Code = texturedQuadWGSL });

    // Build textures from procedurally generated images (matching ref.ts idea)
    static (byte[] rgba, int w, int h) CreateSourceImage(int size)
    {
        // 3 soft circles with blurred edges and screen compositing
        int w = size, h = size;
        byte[] data = new byte[w * h * 4];
        float cx = w / 2f, cy = h / 2f;
        float circleDist = size / 6f;
        float radius = size / 3f;
        Vector2[] offs = new Vector2[3];
        for (int i = 0; i < 3; i++)
        {
            float ang = i * (MathF.PI * 2f / 3f);
            offs[i] = new(cx + MathF.Cos(ang) * circleDist, cy + MathF.Sin(ang) * circleDist);
        }
        static Vector3 HslToRgb(float h, float s, float l)
        {
            float Hue2Rgb(float p, float q, float t)
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1f / 6f) return p + (q - p) * 6 * t;
                if (t < 1f / 2f) return q;
                if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6;
                return p;
            }
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
            float p = 2 * l - q;
            float r = Hue2Rgb(p, q, h + 1f / 3f);
            float g = Hue2Rgb(p, q, h);
            float b = Hue2Rgb(p, q, h - 1f / 3f);
            return new(r, g, b);
        }
        Vector3[] colors = new Vector3[3];
        for (int i = 0; i < 3; i++)
            colors[i] = HslToRgb(i / 3f, 1f, 0.5f);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                Vector3 c = Vector3.Zero;
                // Screen composite circles
                float cR = 0, cG = 0, cB = 0, a = 0;
                for (int i = 0; i < 3; i++)
                {
                    float dx = x - offs[i].X;
                    float dy = y - offs[i].Y;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float t = d / radius;
                    float aI = t <= 0.5f ? 1f : (t >= 1f ? 0f : (2f - 2f * t));
                    // color with alpha
                    float r = colors[i].X * aI;
                    float g = colors[i].Y * aI;
                    float b = colors[i].Z * aI;
                    // screen: out = 1 - (1 - dst) * (1 - src)
                    cR = 1f - (1f - cR) * (1f - r);
                    cG = 1f - (1f - cG) * (1f - g);
                    cB = 1f - (1f - cB) * (1f - b);
                    a = 1f - (1f - a) * (1f - aI);
                }
                data[idx + 0] = (byte)Math.Clamp((int)(cR * 255 + 0.5f), 0, 255);
                data[idx + 1] = (byte)Math.Clamp((int)(cG * 255 + 0.5f), 0, 255);
                data[idx + 2] = (byte)Math.Clamp((int)(cB * 255 + 0.5f), 0, 255);
                data[idx + 3] = (byte)Math.Clamp((int)(a * 255 + 0.5f), 0, 255);
            }
        }
        return (data, w, h);
    }

    static (byte[] rgba, int w, int h) CreateDestinationImage(int size)
    {
        int w = size, h = size;
        byte[] data = new byte[w * h * 4];
        // diagonal HSL gradient background
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float t = (x + y) / (float)(w + h);
                // 6-stop hue ramp similar to ref
                float hue = (1f - t) % 1f; // reverse
                var rgb = HslToRgb(hue, 1f, 0.5f);
                int idx = (y * w + x) * 4;
                data[idx + 0] = (byte)(rgb.X * 255);
                data[idx + 1] = (byte)(rgb.Y * 255);
                data[idx + 2] = (byte)(rgb.Z * 255);
                data[idx + 3] = 255; // opaque initially
            }
        }
        // apply rotated transparent stripes: rotate coords -45deg, make every 32px band half transparent removed
        float ang = -MathF.PI / 4f;
        float cos = MathF.Cos(ang), sin = MathF.Sin(ang);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float xr = (x - w / 2f) * cos - (y - h / 2f) * sin + w / 2f;
                float yr = (x - w / 2f) * sin + (y - h / 2f) * cos + h / 2f;
                int idx = (y * w + x) * 4;
                if (((int)MathF.Floor(yr) % 32) < 16)
                {
                    // destination-out: make transparent
                    data[idx + 3] = 0;
                }
            }
        }
        return (data, w, h);

        static Vector3 HslToRgb(float h, float s, float l)
        {
            float Hue2Rgb(float p, float q, float t)
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1f / 6f) return p + (q - p) * 6 * t;
                if (t < 1f / 2f) return q;
                if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6;
                return p;
            }
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
            float p = 2 * l - q;
            float r = Hue2Rgb(p, q, h + 1f / 3f);
            float g = Hue2Rgb(p, q, h);
            float b = Hue2Rgb(p, q, h - 1f / 3f);
            return new(r, g, b);
        }
    }

    static byte[] Premultiply(byte[] data)
    {
        var outData = new byte[data.Length];
        for (int i = 0; i < data.Length; i += 4)
        {
            float a = data[i + 3] / 255f;
            outData[i + 0] = (byte)Math.Clamp((int)(data[i + 0] * a), 0, 255);
            outData[i + 1] = (byte)Math.Clamp((int)(data[i + 1] * a), 0, 255);
            outData[i + 2] = (byte)Math.Clamp((int)(data[i + 2] * a), 0, 255);
            outData[i + 3] = data[i + 3];
        }
        return outData;
    }

    static Texture MakeTexture(Device device, Queue queue, byte[] rgba, int w, int h)
    {
        var tex = device.CreateTexture(new()
        {
            Size = new((uint)w, (uint)h),
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.RenderAttachment,
        });
        queue.WriteTexture(
            destination: new() { Texture = tex },
            data: rgba,
            dataLayout: new() { BytesPerRow = (uint)(w * 4), RowsPerImage = (uint)h },
            writeSize: new((uint)w, (uint)h)
        );
        return tex;
    }

    var (srcRGBA, sw, sh) = CreateSourceImage(TEX_SIZE);
    var (dstRGBA, dw, dh) = CreateDestinationImage(TEX_SIZE);

    // two texture sets: premultiplied and unpremultiplied
    var srcTexPremul = MakeTexture(device, queue, Premultiply(srcRGBA), sw, sh);
    var dstTexPremul = MakeTexture(device, queue, Premultiply(dstRGBA), dw, dh);
    var srcTexUnpremul = MakeTexture(device, queue, srcRGBA, sw, sh);
    var dstTexUnpremul = MakeTexture(device, queue, dstRGBA, dw, dh);

    var sampler = device.CreateSampler(new()
    {
        MagFilter = FilterMode.Linear,
        MinFilter = FilterMode.Linear,
        MipmapFilter = MipmapFilterMode.Linear,
    });

    // Uniform buffers (mat4x4 each)
    const ulong uniformSize = 4 * 16;
    var srcUBO = device.CreateBuffer(new() { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = uniformSize });
    var dstUBO = device.CreateBuffer(new() { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = uniformSize });

    // Create bind groups using auto layout from pipeline
    // We'll create the pipeline(s) now to fetch BGL, but will recreate src pipeline when blending changes.
    var pipelineBaseVertex = new VertexState() { Module = shaderModule };
    var dstPipeline = device.CreateRenderPipeline(new()
    {
        Layout = null,
        Vertex = ref pipelineBaseVertex,
        Fragment = new FragmentState()
        {
            Module = shaderModule,
            Targets = [new() { Format = surfaceFormat }]
        },
        Primitive = new() { Topology = PrimitiveTopology.TriangleList }
    });
    var bgl = dstPipeline.GetBindGroupLayout(0);

    BindGroup MakeBG(Texture tex, WebGpuSharp.Buffer ubo) => device.CreateBindGroup(new()
    {
        Layout = bgl,
        Entries = [
            new() { Binding = 0, Sampler = sampler },
            new() { Binding = 1, TextureView = tex.CreateView() },
            new() { Binding = 2, Buffer = ubo },
        ],
    });

    BindGroup MakeBGWithLayout(BindGroupLayout layout, Texture tex, WebGpuSharp.Buffer ubo) => device.CreateBindGroup(new()
    {
        Layout = layout,
        Entries = [
            new() { Binding = 0, Sampler = sampler },
            new() { Binding = 1, TextureView = tex.CreateView() },
            new() { Binding = 2, Buffer = ubo },
        ],
    });

    // Destination bind groups (made from dstPipeline's layout)
    var dstBG_Premul = MakeBG(dstTexPremul, dstUBO);
    var dstBG_Unpremul = MakeBG(dstTexUnpremul, dstUBO);

    // Settings and presets
    CompositeAlphaMode alphaMode = CompositeAlphaMode.Premultiplied;
    bool usePremultipliedSet = true; // true: premultiplied alpha textures; false: un-premultiplied

    BlendOperation colorOp = BlendOperation.Add;
    BlendFactor colorSrc = BlendFactor.One;
    BlendFactor colorDst = BlendFactor.OneMinusSrc;

    BlendOperation alphaOp = BlendOperation.Add;
    BlendFactor alphaSrc = BlendFactor.One;
    BlendFactor alphaDst = BlendFactor.OneMinusSrc;

    Vector3 constantColor = new(1f, 0.5f, 0.25f);
    float constantAlpha = 1f;

    Vector3 clearColor = Vector3.Zero;
    float clearAlpha = 0f;
    bool clearPremultiply = true;

    string[] presets = new[]
    {
        "default (copy)",
        "premultiplied blend (source-over)",
        "un-premultiplied blend",
        "destination-over",
        "source-in",
        "destination-in",
        "source-out",
        "destination-out",
        "source-atop",
        "destination-atop",
        "additive (lighten)",
    };
    int presetIndex = 1;

    void ApplyPreset(int idx)
    {
        switch (idx)
        {
            default:
            case 0: // default (copy)
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.One; colorDst = BlendFactor.Zero;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 1: // premultiplied source-over
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.One; colorDst = BlendFactor.OneMinusSrcAlpha;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 2: // unpremultiplied blend
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.SrcAlpha; colorDst = BlendFactor.OneMinusSrcAlpha;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 3: // destination-over
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.OneMinusDstAlpha; colorDst = BlendFactor.One;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 4: // source-in
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.DstAlpha; colorDst = BlendFactor.Zero;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 5: // destination-in
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.Zero; colorDst = BlendFactor.SrcAlpha;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 6: // source-out
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.OneMinusDstAlpha; colorDst = BlendFactor.Zero;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 7: // destination-out
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.Zero; colorDst = BlendFactor.OneMinusSrcAlpha;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 8: // source-atop
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.DstAlpha; colorDst = BlendFactor.OneMinusSrcAlpha;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 9: // destination-atop
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.OneMinusDstAlpha; colorDst = BlendFactor.SrcAlpha;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
            case 10: // additive
                colorOp = BlendOperation.Add; colorSrc = BlendFactor.One; colorDst = BlendFactor.One;
                alphaOp = colorOp; alphaSrc = colorSrc; alphaDst = colorDst;
                break;
        }
    }
    ApplyPreset(presetIndex);

    CompositeAlphaMode currentConfiguredAlpha = CompositeAlphaMode.Premultiplied;

    // Helpers
    static Matrix4x4 OrthoTopLeft(float w, float h)
    {
        // Map (0,0) top-left to NDC ( -1, 1 ), and (w,h) to (1,-1)
        // Column-major compatible for WGSL mat4 * vec4
        var m = Matrix4x4.Identity;
        m.M11 = 2f / w;
        m.M22 = -2f / h;
        m.M33 = 1f;
        m.M41 = -1f;
        m.M42 = 1f;
        return m;
    }

    void UpdateUniform(WebGpuSharp.Buffer ubo, int texW, int texH)
    {
        var proj = OrthoTopLeft(WIDTH, HEIGHT);
        // scale by texture size
        proj.M11 *= texW; // scale x
        proj.M22 *= texH; // scale y (note sign already set)
        queue.WriteBuffer(ubo, 0, proj);
    }

    UpdateUniform(srcUBO, sw, sh);
    UpdateUniform(dstUBO, dw, dh);

    // UI and render loop
    onFrame(() =>
    {
        guiContext.NewFrame();

        ImGui.SetNextWindowPos(new(600, 0));
        ImGui.SetNextWindowSize(new(300, 600));
        ImGui.Begin("Blending Controls", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);

        // Alpha mode
        if (ImGui.BeginCombo("Canvas alpha mode", alphaMode.ToString()))
        {
            foreach (var mode in new[] { CompositeAlphaMode.Opaque, CompositeAlphaMode.Premultiplied, CompositeAlphaMode.Unpremultiplied })
            {
                bool selected = alphaMode == mode;
                if (ImGui.Selectable(mode.ToString(), selected)) alphaMode = mode;
            }
            ImGui.EndCombo();
        }

        // Texture set
        int texSet = usePremultipliedSet ? 0 : 1;
        if (ImGui.Combo("Texture data", ref texSet, new[] { "premultiplied alpha", "un-premultiplied alpha" }, 2))
        {
            usePremultipliedSet = texSet == 0;
        }

        if (ImGui.Combo("Preset", ref presetIndex, presets, presets.Length))
        {
            ApplyPreset(presetIndex);
        }

        if (ImGui.CollapsingHeader("Color"))
        {
            ImGuiUtils.EnumDropdown("operation", ref colorOp);
            ImGuiUtils.EnumDropdown("srcFactor", ref colorSrc);
            ImGuiUtils.EnumDropdown("dstFactor", ref colorDst);
        }
        if (ImGui.CollapsingHeader("Alpha"))
        {
            ImGuiUtils.EnumDropdown("operation", ref alphaOp);
            ImGuiUtils.EnumDropdown("srcFactor", ref alphaSrc);
            ImGuiUtils.EnumDropdown("dstFactor", ref alphaDst);
        }

        if (ImGui.CollapsingHeader("Constant"))
        {
            var cc = new System.Numerics.Vector3(constantColor.X, constantColor.Y, constantColor.Z);
            if (ImGui.ColorEdit3("color", ref cc))
            {
                constantColor = new(cc.X, cc.Y, cc.Z);
            }
            ImGui.SliderFloat("alpha", ref constantAlpha, 0f, 1f);
        }
        if (ImGui.CollapsingHeader("Clear color"))
        {
            ImGui.Checkbox("premultiply", ref clearPremultiply);
            var clr = new System.Numerics.Vector3(clearColor.X, clearColor.Y, clearColor.Z);
            if (ImGui.ColorEdit3("color", ref clr)) clearColor = new(clr.X, clr.Y, clr.Z);
            ImGui.SliderFloat("alpha##clear", ref clearAlpha, 0f, 1f);
        }

        ImGui.End();

        guiContext.EndFrame();

        // Reconfigure surface if alphaMode changed
        if (alphaMode != currentConfiguredAlpha)
        {
            surface.Configure(new()
            {
                Width = WIDTH,
                Height = HEIGHT,
                Usage = TextureUsage.RenderAttachment,
                Format = surfaceFormat,
                Device = device,
                PresentMode = PresentMode.Fifo,
                AlphaMode = alphaMode,
            });
            currentConfiguredAlpha = alphaMode;
        }

        // Create src pipeline with current blend state; enforce valid factors for Min/Max
        BlendComponent MakeComp(BlendOperation op, BlendFactor src, BlendFactor dst)
        {
            if (op == BlendOperation.Min || op == BlendOperation.Max)
            {
                src = BlendFactor.One; dst = BlendFactor.One;
            }
            return new() { Operation = op, SrcFactor = src, DstFactor = dst };
        }

        var srcPipeline = device.CreateRenderPipeline(new()
        {
            Layout = null,
            Vertex = ref pipelineBaseVertex,
            Fragment = new FragmentState()
            {
                Module = shaderModule,
                Targets = [
                    new()
                    {
                        Format = surfaceFormat,
                        Blend = new BlendState
                        {
                            Color = MakeComp(colorOp, colorSrc, colorDst),
                            Alpha = MakeComp(alphaOp, alphaSrc, alphaDst)
                        }
                    }
                ]
            },
            Primitive = new() { Topology = PrimitiveTopology.TriangleList }
        });

    // Clear color (optionally premultiplied)
        var mult = clearPremultiply ? clearAlpha : 1f;
        var clear = new Color(clearColor.X * mult, clearColor.Y * mult, clearColor.Z * mult, clearAlpha);

    // Select texture set and bind groups
    var dstBG = usePremultipliedSet ? dstBG_Premul : dstBG_Unpremul;
    var srcTex = usePremultipliedSet ? srcTexPremul : srcTexUnpremul;
    // IMPORTANT: create source bind group using the source pipeline's auto layout
    var srcLayout = srcPipeline.GetBindGroupLayout(0);
    var srcBGFrame = MakeBGWithLayout(srcLayout, srcTex, srcUBO);

        var swapView = surface.GetCurrentTexture().Texture!.CreateView();
        var encoder = device.CreateCommandEncoder();
        var pass = encoder.BeginRenderPass(new()
        {
            ColorAttachments = [new() { View = swapView, LoadOp = LoadOp.Clear, StoreOp = StoreOp.Store, ClearValue = clear }]
        });

    // Draw destination (no blending)
        pass.SetPipeline(dstPipeline);
        pass.SetBindGroup(0, dstBG);
        pass.Draw(6);

        // Draw source (with blending)
        pass.SetPipeline(srcPipeline);
    pass.SetBindGroup(0, srcBGFrame);
        pass.SetBlendConstant(new Color(constantColor.X, constantColor.Y, constantColor.Z, constantAlpha));
        pass.Draw(6);

        pass.End();

        var guiCmd = guiContext.Render(surface);
        if (guiCmd.HasValue)
            queue.Submit([encoder.Finish(), guiCmd.Value]);
        else
            queue.Submit([encoder.Finish()]);
        surface.Present();
    });
});

