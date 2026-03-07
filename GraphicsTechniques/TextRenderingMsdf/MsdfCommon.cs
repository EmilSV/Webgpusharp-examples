using System.Text.Json.Serialization;

sealed class MsdfCommon
{
    [JsonPropertyName("lineHeight")]
    public float LineHeight { get; init; }

    [JsonPropertyName("scaleW")]
    public float ScaleW { get; init; }

    [JsonPropertyName("scaleH")]
    public float ScaleH { get; init; }
}
