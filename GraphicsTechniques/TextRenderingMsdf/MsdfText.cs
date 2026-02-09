using System.Numerics;
using System.Runtime.InteropServices;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

sealed class MsdfText
{
    private readonly Queue _queue;
    private FormattedTextStorage _bufferData;
    private bool _bufferDataDirty = true;
    private RenderBundle _renderBundle;

    public MsdfTextMeasurements Measurements { get; }
    public MsdfFont Font { get; }
    public GPUBuffer TextBuffer { get; }

    public MsdfText(
        Queue queue,
        RenderBundle renderBundle,
        MsdfTextMeasurements measurements,
        MsdfFont font,
        GPUBuffer textBuffer)
    {
        _queue = queue;
        _renderBundle = renderBundle;
        Measurements = measurements;
        Font = font;
        TextBuffer = textBuffer;

        SetTransform(Matrix4x4.Identity);
        SetColor(1, 1, 1, 1);
        SetPixelScale(1f / 512f);
        _bufferDataDirty = true;
    }

    public RenderBundle GetRenderBundle()
    {
        if (_bufferDataDirty)
        {
            _bufferDataDirty = false;
            _queue.WriteBuffer(TextBuffer, 0, _bufferData);
        }
        return _renderBundle;
    }

    public void SetTransform(in Matrix4x4 matrix)
    {
        _bufferData.Transform = matrix;
        _bufferDataDirty = true;
    }

    public void SetColor(float r, float g, float b, float a = 1.0f)
    {
        _bufferData.Color = new Vector4(r, g, b, a);
        _bufferDataDirty = true;
    }

    public void SetPixelScale(float pixelScale)
    {
        _bufferData.Scale = pixelScale;
        _bufferDataDirty = true;
    }
}
