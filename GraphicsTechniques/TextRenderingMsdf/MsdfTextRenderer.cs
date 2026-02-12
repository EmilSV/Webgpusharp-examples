using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Setup;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

sealed class MsdfTextRenderer
{
    private static readonly Lazy<byte[]> _shaderCode = new(() => ResourceUtils.GetEmbeddedResource(
        resourceName: "TextRenderingMsdf.shaders.msdfText.wgsl",
        assembly: typeof(MsdfTextRenderer).Assembly
    ));

    private readonly TextureFormat _colorFormat;
    private readonly TextureFormat _depthFormat;
    private CameraUniform _cameraData = new();
    private readonly Queue _queue;

    public Device Device { get; }
    public BindGroupLayout FontBindGroupLayout { get; }
    public BindGroupLayout TextBindGroupLayout { get; }
    public RenderPipeline Pipeline { get; }
    public Sampler Sampler { get; }
    public GPUBuffer CameraUniformBuffer { get; }

    public MsdfTextRenderer(
        Device device,
        TextureFormat colorFormat,
        TextureFormat depthFormat)
    {
        Device = device;
        _queue = device.GetQueue();

        _colorFormat = colorFormat;
        _depthFormat = depthFormat;

        Sampler = device.CreateSampler(new()
        {
            Label = "MSDF text sampler",
            MinFilter = FilterMode.Linear,
            MagFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Linear,
            MaxAnisotropy = 16,
        })!;

        CameraUniformBuffer = device.CreateBuffer(new()
        {
            Label = "MSDF camera uniform buffer",
            Size = (ulong)Unsafe.SizeOf<CameraUniform>(),
            Usage = BufferUsage.CopyDst | BufferUsage.Uniform,
        });

        FontBindGroupLayout = device.CreateBindGroupLayout(new()
        {
            Label = "MSDF font group layout",
            Entries = [
                new()
                {
                    Binding = 0,
                    Visibility = ShaderStage.Fragment,
                    Texture = new()
                },
                new()
                {
                    Binding = 1,
                    Visibility = ShaderStage.Fragment,
                    Sampler = new()
                },
                new()
                {
                    Binding = 2,
                    Visibility = ShaderStage.Vertex,
                    Buffer = new()
                    {
                        Type = BufferBindingType.ReadOnlyStorage,
                    },
                },
            ],
        });

        TextBindGroupLayout = device.CreateBindGroupLayout(new()
        {
            Label = "MSDF text group layout",
            Entries = [
                new()
                {
                    Binding = 0,
                    Visibility = ShaderStage.Vertex,
                    Buffer = new()
                },
                new()
                {
                    Binding = 1,
                    Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                    Buffer = new()
                    {
                        Type = BufferBindingType.ReadOnlyStorage,
                    },
                },
            ],
        });

        var shaderModule = device.CreateShaderModuleWGSL(
            label: "MSDF text shader",
            descriptor: new() { Code = _shaderCode.Value }
        );

        Pipeline = device.CreateRenderPipelineSync(new()
        {
            Label = "MSDF text pipeline",
            Layout = device.CreatePipelineLayout(new()
            {
                BindGroupLayouts = [FontBindGroupLayout, TextBindGroupLayout],
            }),
            Vertex = new()
            {
                Module = shaderModule,
                EntryPoint = "vertexMain",
            },
            Fragment = new()
            {
                Module = shaderModule,
                EntryPoint = "fragmentMain",
                Targets = [
                    new()
                    {
                        Format = colorFormat,
                        Blend = new()
                        {
                            Color = new()
                            {
                                SrcFactor = BlendFactor.SrcAlpha,
                                DstFactor = BlendFactor.OneMinusSrcAlpha,
                            },
                            Alpha = new()
                            {
                                SrcFactor = BlendFactor.One,
                                DstFactor = BlendFactor.One,
                            },
                        },
                    },
                ],
            },
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleStrip,
                StripIndexFormat = IndexFormat.Uint32,
            },
            DepthStencil = new()
            {
                DepthWriteEnabled = OptionalBool.False,
                DepthCompare = CompareFunction.Less,
                Format = depthFormat,
            },
        });
    }

    private Texture LoadTexture(Stream stream, string label)
    {
        var imageData = ResourceUtils.LoadImage(stream);
        var texture = Device.CreateTexture(new()
        {
            Label = label,
            Size = new(imageData.Width, imageData.Height, 1),
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.RenderAttachment,
        });
        ResourceUtils.CopyExternalImageToTexture(_queue, imageData, texture);
        return texture;
    }

    public MsdfFont CreateFontFromResources(Assembly assembly, string fontJsonResourceName, string pageResourcePrefix)
    {
        using var jsonStream = ResourceUtils.GetEmbeddedResourceStream(fontJsonResourceName, assembly);
        var json = JsonSerializer.Deserialize(jsonStream!, MsdfTextJsonContext.Default.MsdfFontJson)
                   ?? throw new InvalidOperationException("Failed to parse MSDF font JSON.");

        var pageTextures = new List<Texture>(json.Pages.Length);
        for (int i = 0; i < json.Pages.Length; ++i)
        {
            var pageName = pageResourcePrefix + json.Pages[i];
            using var pageStream = ResourceUtils.GetEmbeddedResourceStream(pageName, assembly);
            pageTextures.Add(LoadTexture(pageStream!, pageName));
        }

        var charCount = json.Chars.Length;
        var charsBuffer = Device.CreateBuffer(new()
        {
            Label = "MSDF character layout buffer",
            Size = (ulong)(charCount * 8 * sizeof(float)),
            Usage = BufferUsage.Storage,
            MappedAtCreation = true,
        });

        var chars = new Dictionary<int, MsdfChar>(charCount);
        charsBuffer.GetMappedRange<float>(data =>
        {
            var u = 1f / json.Common.ScaleW;
            var v = 1f / json.Common.ScaleH;

            var offset = 0;
            for (int i = 0; i < json.Chars.Length; ++i)
            {
                var ch = json.Chars[i];
                ch.CharIndex = i;
                chars[ch.Id] = ch;

                data[offset] = ch.X * u;
                data[offset + 1] = ch.Y * v;
                data[offset + 2] = ch.Width * u;
                data[offset + 3] = ch.Height * v;
                data[offset + 4] = ch.Width;
                data[offset + 5] = ch.Height;
                data[offset + 6] = ch.XOffset;
                data[offset + 7] = -ch.YOffset;
                offset += 8;
            }
        });
        charsBuffer.Unmap();

        var bindGroup = Device.CreateBindGroup(new()
        {
            Label = "msdf font bind group",
            Layout = FontBindGroupLayout,
            Entries = [
                new()
                {
                    Binding = 0,
                    TextureView = pageTextures[0].CreateView(),
                },
                new()
                {
                    Binding = 1,
                    Sampler = Sampler,
                },
                new()
                {
                    Binding = 2,
                    Buffer = charsBuffer,
                },
            ],
        });

        var kernings = new Dictionary<int, Dictionary<int, int>>();
        if (json.Kernings is not null)
        {
            foreach (var kerning in json.Kernings)
            {
                if (!kernings.TryGetValue(kerning.First, out var charKerning))
                {
                    charKerning = new Dictionary<int, int>();
                    kernings[kerning.First] = charKerning;
                }
                charKerning[kerning.Second] = kerning.Amount;
            }
        }

        return new MsdfFont(Pipeline, bindGroup, json.Common.LineHeight, chars, kernings);
    }

    public MsdfText FormatText(MsdfFont font, string text, MsdfTextFormattingOptions? options = null)
    {
        options ??= new MsdfTextFormattingOptions();

        var textBuffer = Device.CreateBuffer(new()
        {
            Label = "msdf text buffer",
            Size = (ulong)((text.Length + 6) * 4 * sizeof(float)),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst,
            MappedAtCreation = true,
        });

        var measurements = MeasureText(font, text);

        textBuffer.GetMappedRange<float>(data =>
        {
            var offset = 24;

            if (options.Centered)
            {
                MeasureText(font, text, (textX, textY, line, ch, data) =>
                {
                    var lineOffset = measurements.Width * -0.5f -
                        (measurements.Width - measurements.LineWidths[line]) * -0.5f;

                    data[offset] = textX + lineOffset;
                    data[offset + 1] = textY + measurements.Height * 0.5f;
                    data[offset + 2] = ch.CharIndex;
                    offset += 4;
                }, data);
            }
            else
            {

                MeasureText(font, text, (textX, textY, line, ch, data) =>
                {
                    data[offset] = textX;
                    data[offset + 1] = textY;
                    data[offset + 2] = ch.CharIndex;
                    offset += 4;
                }, data);
            }
        });
        textBuffer.Unmap();

        var bindGroup = Device.CreateBindGroup(new()
        {
            Label = "msdf text bind group",
            Layout = TextBindGroupLayout,
            Entries = [
                new()
                {
                    Binding = 0,
                    Buffer = CameraUniformBuffer,
                },
                new()
                {
                    Binding = 1,
                    Buffer = textBuffer,
                },
            ],
        });

        var renderBundleDescriptor = new RenderBundleEncoderDescriptor
        {
            ColorFormats = [_colorFormat],
            DepthStencilFormat = _depthFormat,
        };

        var encoder = Device.CreateRenderBundleEncoder(renderBundleDescriptor);
        encoder.SetPipeline(font.Pipeline);
        encoder.SetBindGroup(0, font.BindGroup);
        encoder.SetBindGroup(1, bindGroup);
        encoder.Draw(4, (uint)measurements.PrintedCharCount);
        var renderBundle = encoder.Finish();

        var msdfText = new MsdfText(
            _queue,
            renderBundle,
            measurements,
            font,
            textBuffer
        );

        if (options.PixelScale.HasValue)
        {
            msdfText.SetPixelScale(options.PixelScale.Value);
        }

        if (options.Color.HasValue)
        {
            var color = options.Color.Value;
            msdfText.SetColor(color.X, color.Y, color.Z, color.W);
        }

        return msdfText;
    }

    public MsdfTextMeasurements MeasureText(MsdfFont font, string text)
    {
        return MeasureText(font, text, charCallback: null, context: 0);
    }

    public MsdfTextMeasurements MeasureText<T>(
        MsdfFont font,
        string text,
        Action<float, float, int, MsdfChar, T>? charCallback, T context)
        where T : allows ref struct
    {
        var maxWidth = 0f;
        var lineWidths = new List<float>();

        var textOffsetX = 0f;
        var textOffsetY = 0f;
        var line = 0;
        var printedCharCount = 0;

        var nextCharCode = text.Length > 0 ? text[0] : -1;
        for (int i = 0; i < text.Length; ++i)
        {
            var charCode = nextCharCode;
            nextCharCode = i < text.Length - 1 ? text[i + 1] : -1;

            switch (charCode)
            {
                case '\n':
                    lineWidths.Add(textOffsetX);
                    line++;
                    maxWidth = MathF.Max(maxWidth, textOffsetX);
                    textOffsetX = 0;
                    textOffsetY -= font.LineHeight;
                    break;
                case '\r':
                    break;
                case ' ':
                    textOffsetX += font.GetXAdvance(charCode);
                    break;
                default:
                    charCallback?.Invoke(textOffsetX, textOffsetY, line, font.GetChar(charCode), context);
                    textOffsetX += font.GetXAdvance(charCode, nextCharCode);
                    printedCharCount++;
                    break;
            }
        }

        lineWidths.Add(textOffsetX);
        maxWidth = MathF.Max(maxWidth, textOffsetX);

        return new MsdfTextMeasurements(
            maxWidth,
            lineWidths.Count * font.LineHeight,
            lineWidths.ToArray(),
            printedCharCount
        );
    }

    public void UpdateCamera(Matrix4x4 projection, Matrix4x4 view)
    {
        _cameraData.Projection = projection;
        _cameraData.View = view;
        _queue.WriteBuffer(CameraUniformBuffer, 0, _cameraData);
    }

    public void Render(RenderPassEncoder renderPass, params MsdfText[] text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var bundles = new RenderBundle[text.Length];
        for (int i = 0; i < text.Length; ++i)
        {
            bundles[i] = text[i].GetRenderBundle();
        }
        renderPass.ExecuteBundles(bundles);
    }


}