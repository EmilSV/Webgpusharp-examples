using System.Text.Json.Serialization;

sealed class MsdfFontJson
{
    [JsonPropertyName("common")]
    public required MsdfCommon Common { get; init; }

    [JsonPropertyName("pages")]
    public required string[] Pages { get; init; }

    [JsonPropertyName("chars")]
    public required MsdfChar[] Chars { get; init; }

    [JsonPropertyName("kernings")]
    public MsdfKerning[]? Kernings { get; init; }
}
