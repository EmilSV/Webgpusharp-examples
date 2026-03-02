using System.Runtime.InteropServices;

public unsafe static class EmscriptenInterop
{
    [DllImport("emscripten", CallingConvention = CallingConvention.Cdecl, EntryPoint = "emscripten_set_main_loop_arg")]
    public static extern void emscripten_set_main_loop_arg(delegate* unmanaged[Cdecl]<nint, void> func, nint arg, int fps, int simulate_infinite_loop);
}