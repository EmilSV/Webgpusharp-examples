using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ImGuiNET;

namespace Setup;


public static partial class ImGuiUtils
{

    [GeneratedRegex("[a-z][A-Z]")]
    private static partial Regex SentenceCaseRegex();

    public static string ToSentenceCase(this string str)
    {
        return SentenceCaseRegex().Replace(str, m => $"{m.Value[0]} {char.ToLower(m.Value[1])}");
    }

    private static class EnumTypeStore<T>
        where T : struct, Enum
    {
        public static readonly string[] Names = Enum.GetNames<T>().Select(name => name.ToSentenceCase()).ToArray();
        public static readonly T[] Values = Enum.GetValues<T>();
        public static readonly FrozenDictionary<T, int> ValueToIndex =
            FrozenDictionary.ToFrozenDictionary(Values.Select((value, index) => KeyValuePair.Create(value, index)));
    }

    public static void EnumDropdown<T>(ReadOnlySpan<char> label, ref T current)
        where T : struct, Enum
    {
        var index = EnumTypeStore<T>.ValueToIndex[current];
        if (ImGui.Combo(label, ref index, EnumTypeStore<T>.Names, EnumTypeStore<T>.Names.Length))
        {
            current = EnumTypeStore<T>.Values[index];
        }
    }

    public static void PlotLines(
        ReadOnlySpan<char> label,
        ReadOnlySpan<float> values,
        int valuesOffset,
        ReadOnlySpan<char> overlayText, float scaleMin, float scaleMax, Vector2 graphSize)
    {
        ImGui.PlotLines(label, ref MemoryMarshal.GetReference(values), values.Length, valuesOffset, overlayText, scaleMin, scaleMax, graphSize);
    }
}