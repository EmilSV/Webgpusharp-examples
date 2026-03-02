using System.Runtime.InteropServices;
using WebGpuSharp;
using WebGpuSharp.FFI;


/// <summary>
/// Mirrors the C struct <c>WGPUEmscriptenSurfaceSourceCanvasHTMLSelector</c> from the
/// emdawnwebgpu webgpu.h header.  It chains onto <see cref="SurfaceDescriptorFFI"/>
/// to tell the WebGPU implementation which &lt;canvas&gt; element to render into.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
unsafe struct EmscriptenSurfaceSourceCanvasHTMLSelectorFFI
{
    /// <summary>Chain header – must have SType = EmscriptenSurfaceSourceCanvasHTMLSelector.</summary>
    public ChainedStruct Chain;
    /// <summary>UTF-8 CSS selector string that identifies the canvas element (e.g. "canvas").</summary>
    public StringViewFFI Selector;
}