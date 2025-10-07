using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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

        public static readonly FrozenDictionary<T, string> ValueToName =
            FrozenDictionary.ToFrozenDictionary(Values.Select((value, index) => KeyValuePair.Create(value, Names[index])));

    }

    public static bool EnumDropdown<T>(ReadOnlySpan<char> label, ref T current)
        where T : struct, Enum
    {
        var index = EnumTypeStore<T>.ValueToIndex[current];
        if (ImGui.Combo(label, ref index, EnumTypeStore<T>.Names, EnumTypeStore<T>.Names.Length))
        {
            current = EnumTypeStore<T>.Values[index];
            return true;
        }
        return false;
    }

    public static bool EnumDropdown<T>(ReadOnlySpan<char> label, ref T current, ReadOnlySpan<T> values)
        where T : unmanaged, Enum
    {
        var comparer = EqualityComparer<T>.Default;
        int index = 0;
        for (; index < values.Length; index++)
        {
            if (comparer.Equals(values[index], current))
            {
                break;
            }
        }
        if (index == -1)
        {
            index = 0;
        }

        var array = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            array[i] = EnumTypeStore<T>.ValueToName[values[i]];
        }
        if (ImGui.Combo(label, ref index, array, values.Length))
        {
            current = values[index];
            return true;
        }
        return false;
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