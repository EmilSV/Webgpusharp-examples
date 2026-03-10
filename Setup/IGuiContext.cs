namespace Setup;

public interface IGuiContext
{
    bool ProcessEvent(in SDL2.SDL.SDL_Event @event);
}

public interface IGuiContext<TSelf> : IGuiContext where TSelf : class, IGuiContext<TSelf>
{
    abstract static TSelf Create(nint window); 
}