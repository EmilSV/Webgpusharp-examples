using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public unsafe static class EmscriptenInterop
{
    [DllImport("emscripten", CallingConvention = CallingConvention.Cdecl, EntryPoint = "emscripten_set_main_loop_arg")]
    public static extern void emscripten_set_main_loop_arg(delegate* unmanaged[Cdecl]<nint, void> func, nint arg, int fps, int simulate_infinite_loop);

    public static void emscriptenSetMainLoop(Action func, int fps, int simulate_infinite_loop)
    {
        var funcHandle = GCHandle.Alloc(func);
        emscripten_set_main_loop_arg(&emscripten_set_main_loop_argHandler, GCHandle.ToIntPtr(funcHandle), fps, simulate_infinite_loop);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void emscripten_set_main_loop_argHandler(nint arg)
    {
        var func = (Action)GCHandle.FromIntPtr(arg).Target!;
        func();
    }
}