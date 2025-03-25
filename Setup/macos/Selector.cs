using System.Runtime.InteropServices;

namespace Setup.Macos;

internal struct Selector
{
    public readonly nint NativePtr;

    public Selector(nint ptr)
    {
        NativePtr = ptr;
    }

    public Selector(string name)
    {
        var namePtr = Marshal.StringToHGlobalAnsi(name);
        NativePtr = ObjectiveCRuntime.sel_registerName(namePtr);
        Marshal.FreeHGlobal(namePtr);
    }

    public static implicit operator Selector(string s)
    {
        return new Selector(s);
    }
}