namespace Setup;

public class InputEvents
{
    private Action<SDL2.SDL.SDL_Keysym>? _onKeyDown;
    private Action<SDL2.SDL.SDL_Keysym>? _onKeyUp;
    private Action<SDL2.SDL.SDL_MouseWheelEvent>? _onMouseWheel;
    private Action<SDL2.SDL.SDL_MouseButtonEvent>? _onMouseButtonDown;
    private Action<SDL2.SDL.SDL_MouseButtonEvent>? _onMouseButtonUp;
    private Action<SDL2.SDL.SDL_MouseMotionEvent>? _onMouseMotion;

    public event Action<SDL2.SDL.SDL_Keysym> OnKeyDown
    {
        add => _onKeyDown += value;
        remove => _onKeyDown -= value;
    }

    public event Action<SDL2.SDL.SDL_Keysym> OnKeyUp
    {
        add => _onKeyUp += value;
        remove => _onKeyUp -= value;
    }

    public event Action<SDL2.SDL.SDL_MouseWheelEvent> OnMouseWheel
    {
        add => _onMouseWheel += value;
        remove => _onMouseWheel -= value;
    }

    public event Action<SDL2.SDL.SDL_MouseButtonEvent> OnMouseButtonDown
    {
        add => _onMouseButtonDown += value;
        remove => _onMouseButtonDown -= value;
    }

    public event Action<SDL2.SDL.SDL_MouseButtonEvent> OnMouseButtonUp
    {
        add => _onMouseButtonUp += value;
        remove => _onMouseButtonUp -= value;
    }

    public event Action<SDL2.SDL.SDL_MouseMotionEvent> OnMouseMotion
    {
        add => _onMouseMotion += value;
        remove => _onMouseMotion -= value;
    }

    internal void HandleEvent(SDL2.SDL.SDL_Event sdlEvent)
    {
        switch (sdlEvent.type)
        {
            case SDL2.SDL.SDL_EventType.SDL_KEYDOWN:
                _onKeyDown?.Invoke(sdlEvent.key.keysym);
                break;
            case SDL2.SDL.SDL_EventType.SDL_KEYUP:
                _onKeyUp?.Invoke(sdlEvent.key.keysym);
                break;
            case SDL2.SDL.SDL_EventType.SDL_MOUSEWHEEL:
                _onMouseWheel?.Invoke(sdlEvent.wheel);
                break;
            case SDL2.SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN:
                _onMouseButtonDown?.Invoke(sdlEvent.button);
                break;
            case SDL2.SDL.SDL_EventType.SDL_MOUSEBUTTONUP:
                _onMouseButtonUp?.Invoke(sdlEvent.button);
                break;
            case SDL2.SDL.SDL_EventType.SDL_MOUSEMOTION:
                _onMouseMotion?.Invoke(sdlEvent.motion);
                break;
            default:
                break;
        }
    }
}