using System.Text.Json.Serialization;

sealed class MsdfChar
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("char")]
    public string Character { get; init; } = string.Empty;

    [JsonPropertyName("width")]
    public float Width { get; init; }

    [JsonPropertyName("height")]
    public float Height { get; init; }

    [JsonPropertyName("xoffset")]
    public float XOffset { get; init; }

    [JsonPropertyName("yoffset")]
    public float YOffset { get; init; }

    [JsonPropertyName("xadvance")]
    public float XAdvance { get; init; }

    [JsonPropertyName("chnl")]
    public int Channel { get; init; }

    [JsonPropertyName("x")]
    public float X { get; init; }

    [JsonPropertyName("y")]
    public float Y { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonIgnore]
    public int CharIndex { get; set; }
}
