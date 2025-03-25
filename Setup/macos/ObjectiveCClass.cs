using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Setup.Macos;

internal unsafe struct ObjectiveCClass
{
    public readonly nint NativePtr;

    public static implicit operator nint(ObjectiveCClass c)
    {
        return c.NativePtr;
    }

    public ObjectiveCClass(string name)
    {
        var namePtr = Marshal.StringToHGlobalAnsi(name);
        NativePtr = ObjectiveCRuntime.objc_getClass(namePtr);
        Marshal.FreeHGlobal(namePtr);
    }

    public T AllocInit<T>() where T : struct
    {
        var value = ObjectiveCRuntime.ptr_objc_msgSend(NativePtr, "alloc");
        ObjectiveCRuntime.objc_msgSend(value, "init");
        return Unsafe.AsRef<T>(&value);
    }
}