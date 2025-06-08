using Setup.Macos;
using WebGpuSharp;
using static SDL2.SDL;

namespace Setup
{
    internal static class SDLWebgpu
    {
        internal static unsafe Surface? SDL_GetWGPUSurface(Instance instance, nint window)
        {
            SDL_SysWMinfo windowWMInfo = new();
            SDL_VERSION(out windowWMInfo.version);
            SDL_GetWindowWMInfo(window, ref windowWMInfo);

            if (windowWMInfo.subsystem == SDL_SYSWM_TYPE.SDL_SYSWM_WINDOWS)
            {
                var wsDescriptor = new WebGpuSharp.FFI.SurfaceSourceWindowsHWNDFFI()
                {
                    Hinstance = (void*)windowWMInfo.info.win.hinstance,
                    Hwnd = (void*)windowWMInfo.info.win.window,
                    Chain = new()
                    {
                        SType = SType.SurfaceSourceWindowsHWND
                    }
                };

                SurfaceDescriptor descriptor_surface = new(ref wsDescriptor);
                return instance.CreateSurface(descriptor_surface);
            }
            else if (windowWMInfo.info.wl.surface != 0 && windowWMInfo.subsystem == SDL_SYSWM_TYPE.SDL_SYSWM_WAYLAND)
            {
                var wlDescriptor = new WebGpuSharp.FFI.SurfaceSourceWaylandSurfaceFFI
                {
                    Chain = new ChainedStruct
                    {
                        Next = null,
                        SType = SType.SurfaceSourceWaylandSurface
                    },
                    Display = (void*)windowWMInfo.info.wl.display,
                    Surface = (void*)windowWMInfo.info.wl.surface
                };
                SurfaceDescriptor descriptor_surface = new(ref wlDescriptor);
                return instance.CreateSurface(descriptor_surface);
            }
            else if (windowWMInfo.info.x11.window != 0 && windowWMInfo.subsystem == SDL_SYSWM_TYPE.SDL_SYSWM_X11)
            {
                var xlibDescriptor = new WebGpuSharp.FFI.SurfaceSourceXlibWindowFFI
                {
                    Chain = new ChainedStruct
                    {
                        Next = null,
                        SType = SType.SurfaceSourceXlibWindow
                    },
                    Display = (void*)windowWMInfo.info.x11.display,
                    Window = (uint)windowWMInfo.info.x11.window
                };
                SurfaceDescriptor descriptor_surface = new(ref xlibDescriptor);
                return instance.CreateSurface(descriptor_surface);
            }
            else if (windowWMInfo.subsystem == SDL_SYSWM_TYPE.SDL_SYSWM_COCOA)
            {
                // Based on the Veldrid Metal bindings implementation:
                // https://github.com/veldrid/veldrid/tree/master/src/Veldrid.MetalBindings

                var cocoa = windowWMInfo.info.cocoa.window;
                CAMetalLayer metalLayer = CAMetalLayer.New();
                NSWindow nsWindow = new(cocoa);
                var contentView = nsWindow.contentView;
                contentView.wantsLayer = 1;
                contentView.layer = metalLayer.NativePtr;

                var cocoaDescriptor = new WebGpuSharp.FFI.SurfaceSourceMetalLayerFFI
                {
                    Chain = new ChainedStruct
                    {
                        Next = null,
                        SType = SType.SurfaceSourceMetalLayer
                    },
                    Layer = (void*)metalLayer.NativePtr
                };

                SurfaceDescriptor descriptor_surface = new(ref cocoaDescriptor);
                return instance.CreateSurface(descriptor_surface);
            }

            throw new Exception("Platform not supported");
        }
    }
}