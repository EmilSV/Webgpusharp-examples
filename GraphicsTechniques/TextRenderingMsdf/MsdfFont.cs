using WebGpuSharp;

sealed class MsdfFont
{
    public int CharCount { get; }
    public MsdfChar DefaultChar { get; }

    public RenderPipeline Pipeline { get; }
    public BindGroup BindGroup { get; }
    public float LineHeight { get; }
    public Dictionary<int, MsdfChar> Chars { get; }
    public Dictionary<int, Dictionary<int, int>> Kernings { get; }

    public MsdfFont(
        RenderPipeline pipeline,
        BindGroup bindGroup,
        float lineHeight,
        Dictionary<int, MsdfChar> chars,
        Dictionary<int, Dictionary<int, int>> kernings)
    {
        Pipeline = pipeline;
        BindGroup = bindGroup;
        LineHeight = lineHeight;
        Chars = chars;
        Kernings = kernings;

        var charArray = chars.Values.ToArray();
        CharCount = charArray.Length;
        DefaultChar = charArray[0];
    }

    public MsdfChar GetChar(int charCode)
    {
        return Chars.TryGetValue(charCode, out var value) ? value : DefaultChar;
    }

    /// <summary>
    ///Gets the distance in pixels a line should advance for a given character code. If the upcoming
    /// character code is given any kerning between the two characters will be taken into account.
    /// </summary>
    public float GetXAdvance(int charCode, int nextCharCode = -1)
    {
        var ch = GetChar(charCode);
        if (nextCharCode >= 0 && Kernings.TryGetValue(charCode, out var kerning))
        {
            return ch.XAdvance + (kerning.TryGetValue(nextCharCode, out var amount) ? amount : 0);
        }
        return ch.XAdvance;
    }
}
