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
                var wsDescriptor = new WebGpuSharp.FFI.SurfaceDescriptorFromWindowsHWNDFFI()
                {
                    Value = new WebGpuSharp.FFI.SurfaceSourceWindowsHWNDFFI()
                    {
                        Hinstance = (void*)windowWMInfo.info.win.hinstance,
                        Hwnd = (void*)windowWMInfo.info.win.window,
                        Chain = new()
                        {
                            SType = SType.SurfaceSourceWindowsHWND
                        }
                    }
                };

                SurfaceDescriptor descriptor_surface = new(ref wsDescriptor);
                return instance.CreateSurface(descriptor_surface);
            }

            throw new Exception("Platform not supported");
        }
    }
}