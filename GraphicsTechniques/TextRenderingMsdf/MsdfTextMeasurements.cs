readonly struct MsdfTextMeasurements
{
    public float Width { get; }
    public float Height { get; }
    public float[] LineWidths { get; }
    public int PrintedCharCount { get; }

    public MsdfTextMeasurements(
        float width,
        float height,
        float[] lineWidths,
        int printedCharCount)
    {
        Width = width;
        Height = height;
        LineWidths = lineWidths;
        PrintedCharCount = printedCharCount;
    }
}
