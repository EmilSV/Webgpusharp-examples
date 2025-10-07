using WebGpuSharp;

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