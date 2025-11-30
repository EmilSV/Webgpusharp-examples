using SDL2;
using Setup;

readonly struct Input(Input.DigitalState digital, Input.AnalogState analog)
{
    public readonly struct DigitalState
    {
        public bool Up { get; init; }
        public bool Down { get; init; }
        public bool Left { get; init; }
        public bool Right { get; init; }
        public bool Forward { get; init; }
        public bool Backward { get; init; }
    }

    public readonly struct AnalogState
    {
        public float X { get; init; }
        public float Y { get; init; }
        public float Zoom { get; init; }
        public bool Touching { get; init; }
    }

    // Digital input (e.g keyboard state)
    public readonly DigitalState Digital = digital;
    // Analog input (e.g mouse, touchscreen)
    public readonly AnalogState Analog = analog;
}



class InputHandler
{
    private bool _forward;
    private bool _backward;
    private bool _left;
    private bool _right;
    private bool _up;
    private bool _down;
    private bool _mouseDown;
    private float _analogX;
    private float _analogY;
    private float _analogZoom;

    public InputHandler(InputEvents inputEvents)
    {
        inputEvents.OnKeyDown += e => SetDigital(e.sym, true);
        inputEvents.OnKeyUp += e => SetDigital(e.sym, false);
        inputEvents.OnMouseButtonDown += e =>
        {
            if (e.button == SDL.SDL_BUTTON_LEFT)
            {
                _mouseDown = true;
            }
        };
        inputEvents.OnMouseButtonUp += e =>
        {
            if (e.button == SDL.SDL_BUTTON_LEFT)
            {
                _mouseDown = false;
            }
        };

        inputEvents.OnMouseMotion += e =>
        {
            if ((e.state & SDL.SDL_BUTTON_LMASK) != 0)
            {
                _mouseDown = true;
            }
            else
            {
                _mouseDown = false;
            }
            if (_mouseDown)
            {
                _analogX += e.xrel;
                _analogY += e.yrel;
            }
        };

        inputEvents.OnMouseWheel += e =>
        {
            if (_mouseDown)
            {
                _analogZoom += Math.Sign(e.y);
            }
        };
    }


    private void SetDigital(SDL2.SDL.SDL_Keycode key, bool value)
    {
        switch (key)
        {
            case SDL.SDL_Keycode.SDLK_w:
                _forward = value;
                break;
            case SDL.SDL_Keycode.SDLK_s:
                _backward = value;
                break;
            case SDL.SDL_Keycode.SDLK_a:
                _left = value;
                break;
            case SDL.SDL_Keycode.SDLK_d:
                _right = value;
                break;
            case SDL.SDL_Keycode.SDLK_SPACE:
                _up = value;
                break;
            case SDL.SDL_Keycode.SDLK_LSHIFT:
            case SDL.SDL_Keycode.SDLK_RSHIFT:
            case SDL.SDL_Keycode.SDLK_c:
                _down = value;
                break;
        }
    }

    public Input GetInput()
    {
        var input = new Input(
            new()
            {
                Forward = _forward,
                Backward = _backward,
                Left = _left,
                Right = _right,
                Up = _up,
                Down = _down
            },
            new()
            {
                X = _analogX,
                Y = _analogY,
                Zoom = _analogZoom,
                Touching = _mouseDown
            }
        );

        // Reset analog values after reading
        _analogX = 0;
        _analogY = 0;
        _analogZoom = 0;

        return input;
    }
}