using System.Text.Json.Serialization;

sealed class MsdfKerning
{
    [JsonPropertyName("first")]
    public int First { get; init; }

    [JsonPropertyName("second")]
    public int Second { get; init; }

    [JsonPropertyName("amount")]
    public int Amount { get; init; }
}
