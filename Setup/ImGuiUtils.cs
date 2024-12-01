using System.Collections.Frozen;
using ImGuiNET;

namespace Setup;


public static class ImGuiUtils
{
    private static class EnumTypeStore<T>
        where T : struct, Enum
    {
        public static readonly string[] Names = Enum.GetNames<T>();
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
}