using System.Numerics;
using System.Runtime.InteropServices;
using WebGpuSharp;
using GPUBuffer = WebGpuSharp.Buffer;

sealed class MsdfText
{
    private readonly Queue _queue;
    private readonly float[] _bufferArray = new float[24];
    private bool _bufferArrayDirty = true;

    public RenderBundle RenderBundle { get; }
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
        RenderBundle = renderBundle;
        Measurements = measurements;
        Font = font;
        TextBuffer = textBuffer;

        SetTransform(Matrix4x4.Identity);
        SetColor(1, 1, 1, 1);
        SetPixelScale(1f / 512f);
        _bufferArrayDirty = true;
    }



    public RenderBundle GetRenderBundle()
    {
        if (_bufferArrayDirty)
        {
            _bufferArrayDirty = false;
            _queue.WriteBuffer(TextBuffer, 0, _bufferArray);
        }
        return RenderBundle;
    }

    public void SetTransform(Matrix4x4 matrix)
    {
        CopyMatrixToSpan(matrix, _bufferArray.AsSpan(0, 16));
        _bufferArrayDirty = true;
    }

    public void SetColor(float r, float g, float b, float a = 1.0f)
    {
        _bufferArray[16] = r;
        _bufferArray[17] = g;
        _bufferArray[18] = b;
        _bufferArray[19] = a;
        _bufferArrayDirty = true;
    }

    public void SetPixelScale(float pixelScale)
    {
        _bufferArray[20] = pixelScale;
        _bufferArrayDirty = true;
    }

    private static void CopyMatrixToSpan(Matrix4x4 matrix, Span<float> destination)
    {
        var matrixSpan = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateSpan(ref matrix, 1));
        matrixSpan.CopyTo(destination);
    }
}
