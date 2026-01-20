using WebGpuSharp;
using static SDL2.SDL;

namespace Setup;


public class RunContext(
    Instance instance,
    Surface surface,
    nint window)
{
    private readonly Instance _instance = instance;
    private readonly Surface _surface = surface;
    private readonly nint _window = window;
    private GuiContext? _guiContext;
    private Action? _onFrame;

    public readonly InputEvents Input = new();
    public event Action? OnFrame
    {
        add => _onFrame += value;
        remove => _onFrame -= value;
    }

    public Instance GetInstance() => _instance;
    public Surface GetSurface() => _surface;
    public GuiContext GetGuiContext()
    {
        _guiContext ??= new GuiContext(_window);
        return _guiContext;
    }

    /// <summary>
    /// Gets the device pixel ratio by comparing drawable size to window size.
    /// This is trying to mimic window.devicePixelRatio in web browsers.
    /// </summary>
    public float GetDevicePixelRatio()
    {
        SDL_GetWindowSize(_window, out int windowWidth, out _);
        SDL_GL_GetDrawableSize(_window, out int drawableWidth, out _);
        return windowWidth > 0 ? drawableWidth / (float)windowWidth : 1.0f;
    }

    internal bool ProcessEventIMGUi(in SDL2.SDL.SDL_Event @event)
    {
        if (_guiContext == null)
        {
            return false;
        }

        return ImGui_Impl_SDL2.ProcessEvent(@event);
    }

    internal void InvokeOnFrame()
    {
        _onFrame?.Invoke();
    }
}