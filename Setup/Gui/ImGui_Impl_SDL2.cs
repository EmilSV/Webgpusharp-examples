using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using ImGuiNET;
using SDL2;

using static SDL2.SDL;


// Does not have support for multiple ImGUI contexts
// Does not have vulkan or opengl support for now

// From https://github.com/ocornut/imgui/blob/master/backends/imgui_impl_sdl2.cpp


public class ImGui_Impl_SDL2
{
    public enum GamepadMode { AutoFirst, AutoAll, Manual };

    public struct ImGui_ImplSDL2_Data
    {
        public nint Window;
        public uint WindowID;
        //public SDL_Renderer* Renderer;
        public ulong Time;
        public string ClipboardTextData;
        public bool UseVulkan;
        public bool WantUpdateMonitors;

        // Mouse handling
        public uint MouseWindowID;
        public int MouseButtonsDown;
        public Dictionary<ImGuiMouseCursor, nint> MouseCursors;
        public nint MouseLastCursor;
        public int MouseLastLeaveFrame;
        public bool MouseCanUseGlobalState;
        public bool MouseCanReportHoveredViewport;  // This is hard to use/unreliable on SDL so we'll set ImGuiBackendFlags_HasMouseHoveredViewport dynamically based on state.

        // Gamepad handling
        public List<nint> Gamepads;
        public GamepadMode GamepadMode;
        public bool WantUpdateGamepadsList;

        public ImGui_ImplSDL2_Data()
        {
            MouseCursors = new();
            Gamepads = new();
        }
    };

    public static bool HasCaptureAndGlobalMouse()
    {
        return !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();
    }


    public static ImGui_ImplSDL2_Data data;

    internal static ref ImGui_ImplSDL2_Data GetBackendData()
    {
        return ref data; //ImGui::GetCurrentContext() ? (ImGui_ImplSDL2_Data*)ImGui::GetIO().BackendPlatformUserData : nullptr;
    }

    internal static string GetClipboardText()
    {
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        bd.ClipboardTextData = SDL_GetClipboardText();
        return bd.ClipboardTextData;
    }
    internal static void SetClipboardText(string text)
    {
        SDL_SetClipboardText(text);
    }

    // Note: native IME will only display if user calls SDL_SetHint(SDL_HINT_IME_SHOW_UI, "1") _before_ SDL_CreateWindow().
    internal static void PlatformSetImeData(ImGuiViewportPtr viewport, ImGuiPlatformImeDataPtr data)
    {
        if (data.WantVisible)
        {
            SDL_Rect r;
            r.x = (int)(data.InputPos.X - viewport.Pos.X);
            r.y = (int)(data.InputPos.Y - viewport.Pos.Y + data.InputLineHeight);
            r.w = 1;
            r.h = (int)data.InputLineHeight;
            SDL_SetTextInputRect(ref r);
        }
    }

    internal static ImGuiKey KeyEventToImGuiKey(SDL_Keycode keycode, SDL_Scancode scancode)
    {
        switch (keycode)
        {
            case SDL_Keycode.SDLK_TAB: return ImGuiKey.Tab;
            case SDL_Keycode.SDLK_LEFT: return ImGuiKey.LeftArrow;
            case SDL_Keycode.SDLK_RIGHT: return ImGuiKey.RightArrow;
            case SDL_Keycode.SDLK_UP: return ImGuiKey.UpArrow;
            case SDL_Keycode.SDLK_DOWN: return ImGuiKey.DownArrow;
            case SDL_Keycode.SDLK_PAGEUP: return ImGuiKey.PageUp;
            case SDL_Keycode.SDLK_PAGEDOWN: return ImGuiKey.PageDown;
            case SDL_Keycode.SDLK_HOME: return ImGuiKey.Home;
            case SDL_Keycode.SDLK_END: return ImGuiKey.End;
            case SDL_Keycode.SDLK_INSERT: return ImGuiKey.Insert;
            case SDL_Keycode.SDLK_DELETE: return ImGuiKey.Delete;
            case SDL_Keycode.SDLK_BACKSPACE: return ImGuiKey.Backspace;
            case SDL_Keycode.SDLK_SPACE: return ImGuiKey.Space;
            case SDL_Keycode.SDLK_RETURN: return ImGuiKey.Enter;
            case SDL_Keycode.SDLK_ESCAPE: return ImGuiKey.Escape;
            case SDL_Keycode.SDLK_QUOTE: return ImGuiKey.Apostrophe;
            case SDL_Keycode.SDLK_COMMA: return ImGuiKey.Comma;
            case SDL_Keycode.SDLK_MINUS: return ImGuiKey.Minus;
            case SDL_Keycode.SDLK_PERIOD: return ImGuiKey.Period;
            case SDL_Keycode.SDLK_SLASH: return ImGuiKey.Slash;
            case SDL_Keycode.SDLK_SEMICOLON: return ImGuiKey.Semicolon;
            case SDL_Keycode.SDLK_EQUALS: return ImGuiKey.Equal;
            case SDL_Keycode.SDLK_LEFTBRACKET: return ImGuiKey.LeftBracket;
            case SDL_Keycode.SDLK_BACKSLASH: return ImGuiKey.Backslash;
            case SDL_Keycode.SDLK_RIGHTBRACKET: return ImGuiKey.RightBracket;
            case SDL_Keycode.SDLK_BACKQUOTE: return ImGuiKey.GraveAccent;
            case SDL_Keycode.SDLK_CAPSLOCK: return ImGuiKey.CapsLock;
            case SDL_Keycode.SDLK_SCROLLLOCK: return ImGuiKey.ScrollLock;
            case SDL_Keycode.SDLK_NUMLOCKCLEAR: return ImGuiKey.NumLock;
            case SDL_Keycode.SDLK_PRINTSCREEN: return ImGuiKey.PrintScreen;
            case SDL_Keycode.SDLK_PAUSE: return ImGuiKey.Pause;
            case SDL_Keycode.SDLK_KP_0: return ImGuiKey.Keypad0;
            case SDL_Keycode.SDLK_KP_1: return ImGuiKey.Keypad1;
            case SDL_Keycode.SDLK_KP_2: return ImGuiKey.Keypad2;
            case SDL_Keycode.SDLK_KP_3: return ImGuiKey.Keypad3;
            case SDL_Keycode.SDLK_KP_4: return ImGuiKey.Keypad4;
            case SDL_Keycode.SDLK_KP_5: return ImGuiKey.Keypad5;
            case SDL_Keycode.SDLK_KP_6: return ImGuiKey.Keypad6;
            case SDL_Keycode.SDLK_KP_7: return ImGuiKey.Keypad7;
            case SDL_Keycode.SDLK_KP_8: return ImGuiKey.Keypad8;
            case SDL_Keycode.SDLK_KP_9: return ImGuiKey.Keypad9;
            case SDL_Keycode.SDLK_KP_PERIOD: return ImGuiKey.KeypadDecimal;
            case SDL_Keycode.SDLK_KP_DIVIDE: return ImGuiKey.KeypadDivide;
            case SDL_Keycode.SDLK_KP_MULTIPLY: return ImGuiKey.KeypadMultiply;
            case SDL_Keycode.SDLK_KP_MINUS: return ImGuiKey.KeypadSubtract;
            case SDL_Keycode.SDLK_KP_PLUS: return ImGuiKey.KeypadAdd;
            case SDL_Keycode.SDLK_KP_ENTER: return ImGuiKey.KeypadEnter;
            case SDL_Keycode.SDLK_KP_EQUALS: return ImGuiKey.KeypadEqual;
            case SDL_Keycode.SDLK_LCTRL: return ImGuiKey.LeftCtrl;
            case SDL_Keycode.SDLK_LSHIFT: return ImGuiKey.LeftShift;
            case SDL_Keycode.SDLK_LALT: return ImGuiKey.LeftAlt;
            case SDL_Keycode.SDLK_LGUI: return ImGuiKey.LeftSuper;
            case SDL_Keycode.SDLK_RCTRL: return ImGuiKey.RightCtrl;
            case SDL_Keycode.SDLK_RSHIFT: return ImGuiKey.RightShift;
            case SDL_Keycode.SDLK_RALT: return ImGuiKey.RightAlt;
            case SDL_Keycode.SDLK_RGUI: return ImGuiKey.RightSuper;
            case SDL_Keycode.SDLK_APPLICATION: return ImGuiKey.Menu;
            case SDL_Keycode.SDLK_0: return ImGuiKey._0;
            case SDL_Keycode.SDLK_1: return ImGuiKey._1;
            case SDL_Keycode.SDLK_2: return ImGuiKey._2;
            case SDL_Keycode.SDLK_3: return ImGuiKey._3;
            case SDL_Keycode.SDLK_4: return ImGuiKey._4;
            case SDL_Keycode.SDLK_5: return ImGuiKey._5;
            case SDL_Keycode.SDLK_6: return ImGuiKey._6;
            case SDL_Keycode.SDLK_7: return ImGuiKey._7;
            case SDL_Keycode.SDLK_8: return ImGuiKey._8;
            case SDL_Keycode.SDLK_9: return ImGuiKey._9;
            case SDL_Keycode.SDLK_a: return ImGuiKey.A;
            case SDL_Keycode.SDLK_b: return ImGuiKey.B;
            case SDL_Keycode.SDLK_c: return ImGuiKey.C;
            case SDL_Keycode.SDLK_d: return ImGuiKey.D;
            case SDL_Keycode.SDLK_e: return ImGuiKey.E;
            case SDL_Keycode.SDLK_f: return ImGuiKey.F;
            case SDL_Keycode.SDLK_g: return ImGuiKey.G;
            case SDL_Keycode.SDLK_h: return ImGuiKey.H;
            case SDL_Keycode.SDLK_i: return ImGuiKey.I;
            case SDL_Keycode.SDLK_j: return ImGuiKey.J;
            case SDL_Keycode.SDLK_k: return ImGuiKey.K;
            case SDL_Keycode.SDLK_l: return ImGuiKey.L;
            case SDL_Keycode.SDLK_m: return ImGuiKey.M;
            case SDL_Keycode.SDLK_n: return ImGuiKey.N;
            case SDL_Keycode.SDLK_o: return ImGuiKey.O;
            case SDL_Keycode.SDLK_p: return ImGuiKey.P;
            case SDL_Keycode.SDLK_q: return ImGuiKey.Q;
            case SDL_Keycode.SDLK_r: return ImGuiKey.R;
            case SDL_Keycode.SDLK_s: return ImGuiKey.S;
            case SDL_Keycode.SDLK_t: return ImGuiKey.T;
            case SDL_Keycode.SDLK_u: return ImGuiKey.U;
            case SDL_Keycode.SDLK_v: return ImGuiKey.V;
            case SDL_Keycode.SDLK_w: return ImGuiKey.W;
            case SDL_Keycode.SDLK_x: return ImGuiKey.X;
            case SDL_Keycode.SDLK_y: return ImGuiKey.Y;
            case SDL_Keycode.SDLK_z: return ImGuiKey.Z;
            case SDL_Keycode.SDLK_F1: return ImGuiKey.F1;
            case SDL_Keycode.SDLK_F2: return ImGuiKey.F2;
            case SDL_Keycode.SDLK_F3: return ImGuiKey.F3;
            case SDL_Keycode.SDLK_F4: return ImGuiKey.F4;
            case SDL_Keycode.SDLK_F5: return ImGuiKey.F5;
            case SDL_Keycode.SDLK_F6: return ImGuiKey.F6;
            case SDL_Keycode.SDLK_F7: return ImGuiKey.F7;
            case SDL_Keycode.SDLK_F8: return ImGuiKey.F8;
            case SDL_Keycode.SDLK_F9: return ImGuiKey.F9;
            case SDL_Keycode.SDLK_F10: return ImGuiKey.F10;
            case SDL_Keycode.SDLK_F11: return ImGuiKey.F11;
            case SDL_Keycode.SDLK_F12: return ImGuiKey.F12;
            case SDL_Keycode.SDLK_F13: return ImGuiKey.F13;
            case SDL_Keycode.SDLK_F14: return ImGuiKey.F14;
            case SDL_Keycode.SDLK_F15: return ImGuiKey.F15;
            case SDL_Keycode.SDLK_F16: return ImGuiKey.F16;
            case SDL_Keycode.SDLK_F17: return ImGuiKey.F17;
            case SDL_Keycode.SDLK_F18: return ImGuiKey.F18;
            case SDL_Keycode.SDLK_F19: return ImGuiKey.F19;
            case SDL_Keycode.SDLK_F20: return ImGuiKey.F20;
            case SDL_Keycode.SDLK_F21: return ImGuiKey.F21;
            case SDL_Keycode.SDLK_F22: return ImGuiKey.F22;
            case SDL_Keycode.SDLK_F23: return ImGuiKey.F23;
            case SDL_Keycode.SDLK_F24: return ImGuiKey.F24;
            case SDL_Keycode.SDLK_AC_BACK: return ImGuiKey.AppBack;
            case SDL_Keycode.SDLK_AC_FORWARD: return ImGuiKey.AppForward;
            default: break;
        }
        return ImGuiKey.None;
    }

    internal static void UpdateKeyModifiers(SDL_Keymod sdl_key_mods)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, (sdl_key_mods & SDL_Keymod.KMOD_CTRL) != 0);
        io.AddKeyEvent(ImGuiKey.ModShift, (sdl_key_mods & SDL_Keymod.KMOD_SHIFT) != 0);
        io.AddKeyEvent(ImGuiKey.ModAlt, (sdl_key_mods & SDL_Keymod.KMOD_ALT) != 0);
        io.AddKeyEvent(ImGuiKey.ModSuper, (sdl_key_mods & SDL_Keymod.KMOD_GUI) != 0);
    }

    internal static ImGuiViewportPtr GetViewportForWindowID(nint window_id)
    {
        return ImGui.FindViewportByPlatformHandle(window_id);
    }

    public static unsafe bool ProcessEvent(in SDL_Event e)
    {
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        Debug.Assert(!bd.Equals(default(ImGui_ImplSDL2_Data)));
        ImGuiIOPtr io = ImGui.GetIO();

        switch (e.type)
        {
            case SDL_EventType.SDL_MOUSEMOTION:
                if (GetViewportForWindowID((nint)e.motion.windowID).NativePtr == null)
                    return false;
                Vector2 mousePos = new Vector2(e.motion.x, e.motion.y);
                if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
                {
                    int window_x, window_y;
                    SDL_GetWindowPosition(SDL_GetWindowFromID(e.motion.windowID), out window_x, out window_y);
                    mousePos.X += window_x;
                    mousePos.Y += window_y;
                }

                io.AddMouseSourceEvent(e.motion.which == SDL_TOUCH_MOUSEID ? ImGuiMouseSource.TouchScreen : ImGuiMouseSource.Mouse);
                io.AddMousePosEvent(mousePos.X, mousePos.Y);
                return true;
            case SDL_EventType.SDL_MOUSEWHEEL:
                if (GetViewportForWindowID((nint)e.wheel.windowID).NativePtr == null)
                    return false;
                Vector2 wheel = new Vector2(e.wheel.x, e.wheel.y);

                io.AddMouseSourceEvent(e.motion.which == SDL_TOUCH_MOUSEID ? ImGuiMouseSource.TouchScreen : ImGuiMouseSource.Mouse);
                io.AddMouseWheelEvent(wheel.X, wheel.Y);
                return true;
            case SDL_EventType.SDL_MOUSEBUTTONDOWN:
            case SDL_EventType.SDL_MOUSEBUTTONUP:
                if (GetViewportForWindowID((nint)e.button.windowID).NativePtr == null)
                    return false;

                int mouse_button = -1;

                switch (e.button.button)
                {
                    case (byte)SDL_BUTTON_LEFT:
                        mouse_button = 0;
                        break;
                    case (byte)SDL_BUTTON_RIGHT:
                        mouse_button = 1;
                        break;
                    case (byte)SDL_BUTTON_MIDDLE:
                        mouse_button = 2;
                        break;
                    case (byte)SDL_BUTTON_X1:
                        mouse_button = 3;
                        break;
                    case (byte)SDL_BUTTON_X2:
                        mouse_button = 4;
                        break;
                    default:
                        break;
                }

                if (mouse_button == -1)
                    break;

                io.AddMouseSourceEvent(e.motion.which == SDL_TOUCH_MOUSEID ? ImGuiMouseSource.TouchScreen : ImGuiMouseSource.Mouse);
                io.AddMouseButtonEvent(mouse_button, e.type == SDL_EventType.SDL_MOUSEBUTTONDOWN);
                bd.MouseButtonsDown = (e.type == SDL_EventType.SDL_MOUSEBUTTONDOWN) ? (bd.MouseButtonsDown | (1 << mouse_button)) : (bd.MouseButtonsDown & ~(1 << mouse_button));
                return true;
            case SDL_EventType.SDL_TEXTINPUT:
                if (GetViewportForWindowID((nint)e.text.windowID).NativePtr == null)
                    return false;
                fixed (byte* ptr = e.text.text)
                {
                    ImGuiNative.ImGuiIO_AddInputCharactersUTF8(io.NativePtr, ptr);
                }
                return true;

            case SDL_EventType.SDL_KEYDOWN:
            case SDL_EventType.SDL_KEYUP:
                if (GetViewportForWindowID((nint)e.key.windowID).NativePtr == null)
                    return false;
                UpdateKeyModifiers(e.key.keysym.mod);
                var key = KeyEventToImGuiKey(e.key.keysym.sym, e.key.keysym.scancode);
                io.AddKeyEvent(key, e.type == SDL_EventType.SDL_KEYDOWN);
                io.SetKeyEventNativeData(key, (int)e.key.keysym.sym, (int)e.key.keysym.scancode, (int)e.key.keysym.scancode);
                return true;

            case SDL_EventType.SDL_DISPLAYEVENT:
                // 2.0.26 has SDL_DISPLAYEVENT_CONNECTED/SDL_DISPLAYEVENT_DISCONNECTED/SDL_DISPLAYEVENT_ORIENTATION,
                // so change of DPI/Scaling are not reflected in this event. (SDL3 has it)
                bd.WantUpdateMonitors = true;
                return true;

            case SDL_EventType.SDL_WINDOWEVENT:
                var viewport = GetViewportForWindowID((nint)e.window.windowID);
                if (viewport.NativePtr == null)
                    return false;
                // - When capturing mouse, SDL will send a bunch of conflicting LEAVE/ENTER event on every mouse move, but the final ENTER tends to be right.
                // - However we won't get a correct LEAVE event for a captured window.
                // - In some cases, when detaching a window from main viewport SDL may send SDL_WINDOWEVENT_ENTER one frame too late,
                //   causing SDL_WINDOWEVENT_LEAVE on previous frame to interrupt drag operation by clear mouse position. This is why
                //   we delay process the SDL_WINDOWEVENT_LEAVE events by one frame. See issue #5012 for details.
                var window_event = e.window.windowEvent;
                if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_ENTER)
                {
                    bd.MouseWindowID = e.window.windowID;
                    bd.MouseLastLeaveFrame = 0;
                }

                if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_LEAVE)
                    bd.MouseLastLeaveFrame = ImGui.GetFrameCount() + 1;

                if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_GAINED)
                    io.AddFocusEvent(true);
                if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST)
                    io.AddFocusEvent(false);

                if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_GAINED)
                    io.AddFocusEvent(true);
                else if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST)
                    io.AddFocusEvent(false);
                else if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE)
                    viewport.PlatformRequestClose = true;
                else if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_MOVED)
                    viewport.PlatformRequestMove = true;
                else if (window_event == SDL_WindowEventID.SDL_WINDOWEVENT_RESIZED)
                    viewport.PlatformRequestResize = true;
                return true;
            case SDL_EventType.SDL_CONTROLLERDEVICEADDED:
            case SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                bd.WantUpdateGamepadsList = true;
                return true;
        }
        return false;
    }

    public static bool Init(nint window, bool mouseFocusClickthrough = false, bool imeShowUi = false, bool mouseAutoCapture = false)
    {
        ImGuiIOPtr io = ImGui.GetIO();

        bool mouse_can_use_global_state = false;

        if (HasCaptureAndGlobalMouse())
        {
            string sdl_backend = SDL_GetCurrentVideoDriver();
            string[] global_mouse_whitelist = ["windows", "cocoa", "x11", "DIVE", "VMAN"];
            for (int n = 0; n < global_mouse_whitelist.Length; n++)
                if (sdl_backend.Length == global_mouse_whitelist[n].Length)
                    mouse_can_use_global_state = true;
        }

        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        bd = new();
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;       // We can honor GetMouseCursor() values (optional)
        io.BackendFlags |= ImGuiBackendFlags.HasSetMousePos;        // We can honor io.WantSetMousePos requests (optional, rarely used)
        if (mouse_can_use_global_state)
            io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports;

        bd.Window = window;
        bd.WindowID = SDL_GetWindowID(window);
        //bd.Renderer = renderer; Support for renderer not added

        // SDL on Linux/OSX doesn't report events for unfocused windows (see https://github.com/ocornut/imgui/issues/4960)
        // We will use 'MouseCanReportHoveredViewport' to set 'ImGuiBackendFlags_HasMouseHoveredViewport' dynamically each frame.
        bd.MouseCanUseGlobalState = mouse_can_use_global_state;

        if (!OperatingSystem.IsMacOS())
        {
            bd.MouseCanReportHoveredViewport = bd.MouseCanUseGlobalState;
        }
        else
        {
            bd.MouseCanReportHoveredViewport = false;
        }
        bd.WantUpdateMonitors = true;

        // Not supported by bindings
        //var platform_io = ImGui.GetPlatformIO();
        //platform_io.Platform_SetClipboardTextFn = ImGui_ImplSDL2_SetClipboardText;
        //platform_io.Platform_GetClipboardTextFn = ImGui_ImplSDL2_GetClipboardText;
        //platform_io.Platform_ClipboardUserData = nullptr;
        //platform_io.Platform_SetImeDataFn = ImGui_ImplSDL2_PlatformSetImeData;

        // Gamepad handling
        bd.GamepadMode = GamepadMode.AutoFirst;
        bd.WantUpdateGamepadsList = true;

        // Load mouse cursors
        bd.MouseCursors.Add(ImGuiMouseCursor.Arrow, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_ARROW));
        bd.MouseCursors.Add(ImGuiMouseCursor.TextInput, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_IBEAM));
        bd.MouseCursors.Add(ImGuiMouseCursor.ResizeAll, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_SIZEALL));
        bd.MouseCursors.Add(ImGuiMouseCursor.ResizeNS, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_SIZENS));
        bd.MouseCursors.Add(ImGuiMouseCursor.ResizeEW, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_SIZEWE));
        bd.MouseCursors.Add(ImGuiMouseCursor.ResizeNESW, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_SIZENESW));
        bd.MouseCursors.Add(ImGuiMouseCursor.ResizeNWSE, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_SIZENWSE));
        bd.MouseCursors.Add(ImGuiMouseCursor.Hand, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_HAND));
        bd.MouseCursors.Add(ImGuiMouseCursor.NotAllowed, SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_NO));

        // Set platform dependent data in viewport
        // Our mouse update function expect PlatformHandle to be filled for the main viewport
        var main_viewport = ImGui.GetMainViewport();
        main_viewport.PlatformHandle = (nint)bd.WindowID;
        main_viewport.PlatformHandleRaw = 0;

        SDL_SysWMinfo info = new();
        SDL_VERSION(out info.version);
        if (SDL_GetWindowWMInfo(window, ref info) == SDL_bool.SDL_TRUE)
        {
            if (OperatingSystem.IsWindows())
            {
                main_viewport.PlatformHandleRaw = info.info.win.window;
            }
            else if (OperatingSystem.IsMacOS())
            {
                main_viewport.PlatformHandleRaw = info.info.cocoa.window;
            }
        }

        // From 2.0.5: Set SDL hint to receive mouse click events on window focus, otherwise SDL doesn't emit the event.
        // Without this, when clicking to gain focus, our widgets wouldn't activate even though they showed as hovered.
        // (This is unfortunately a global SDL setting, so enabling it might have a side-effect on your application.
        // It is unlikely to make a difference, but if your app absolutely needs to ignore the initial on-focus click:
        // you can ignore SDL_MOUSEBUTTONDOWN events coming right after a SDL_WINDOWEVENT_FOCUS_GAINED)
        if (mouseFocusClickthrough)
        {
            SDL_SetHint(SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");
        }

        // From 2.0.18: Enable native IME.
        // IMPORTANT: This is used at the time of SDL_CreateWindow() so this will only affects secondary windows, if any.
        // For the main window to be affected, your application needs to call this manually before calling SDL_CreateWindow().
        if (imeShowUi)
        {
            SDL_SetHint("SDL_IME_SHOW_UI", "1");
        }

        // From 2.0.22: Disable auto-capture, this is preventing drag and drop across multiple windows (see #5710)
        if (mouseAutoCapture)
        {
            SDL_SetHint("SDL_MOUSE_AUTO_CAPTURE", "0");
        }

        // We need SDL_CaptureMouse(), SDL_GetGlobalMouseState() from SDL 2.0.4+ to support multiple viewports.
        // We left the call to ImGui_ImplSDL2_InitPlatformInterface() outside of #ifdef to avoid unused-function warnings.
        if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0 && ((io.BackendFlags & ImGuiBackendFlags.PlatformHasViewports) != 0))
            InitPlatformInterface(window);

        return true;
    }

    public static void ShutDown()
    {
        throw new NotImplementedException();
    }

    internal unsafe static void UpdateMouseData()
    {
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        ImGuiIOPtr io = ImGui.GetIO();

        bool is_app_focused;
        if (HasCaptureAndGlobalMouse())
        {
            // SDL_CaptureMouse() let the OS know e.g. that our imgui drag outside the SDL window boundaries shouldn't e.g. trigger other operations outside
            SDL_CaptureMouse((bd.MouseButtonsDown != 0) ? SDL_bool.SDL_TRUE : SDL_bool.SDL_FALSE);
            nint focused_window = SDL_GetKeyboardFocus();
            is_app_focused = focused_window != 0 && (bd.Window == focused_window || GetViewportForWindowID((nint)SDL_GetWindowID((nint)focused_window)).NativePtr != null);
        }
        else
        {
            nint focused_window = bd.Window;
            is_app_focused = (SDL_GetWindowFlags(bd.Window) & (uint)SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS) != 0; // SDL 2.0.3 and non-windowed systems: single-viewport only
        }

        if (is_app_focused)
        {
            // (Optional) Set OS mouse position from Dear ImGui if requested (rarely used, only when ImGuiConfigFlags_NavEnableSetMousePos is enabled by user)
            if (io.WantSetMousePos)
            {
                if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0 && HasCaptureAndGlobalMouse())
                {
                    SDL_WarpMouseGlobal((int)io.MousePos.X, (int)io.MousePos.Y);
                }
                else
                {

                    SDL_WarpMouseInWindow(bd.Window, (int)io.MousePos.X, (int)io.MousePos.Y);
                }
            }


            // (Optional) Fallback to provide mouse position when focused (SDL_MOUSEMOTION already provides this when hovered or captured)
            if (bd.MouseCanUseGlobalState && bd.MouseButtonsDown == 0)
            {
                // Single-viewport mode: mouse position in client window coordinates (io.MousePos is (0,0) when the mouse is on the upper-left corner of the app window)
                // Multi-viewport mode: mouse position in OS absolute coordinates (io.MousePos is (0,0) when the mouse is on the upper-left of the primary monitor)
                int mouse_x, mouse_y, window_x, window_y;
                SDL_GetGlobalMouseState(out mouse_x, out mouse_y);
                if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0)
                {
                    SDL_GetWindowPosition(bd.Window, out window_x, out window_y);
                    mouse_x -= window_x;
                    mouse_y -= window_y;
                }

                io.AddMousePosEvent((float)(mouse_x), (float)(mouse_y));
            }
        }


        // (Optional) When using multiple viewports: call io.AddMouseViewportEvent() with the viewport the OS mouse cursor is hovering.
        // If ImGuiBackendFlags_HasMouseHoveredViewport is not set by the backend, Dear imGui will ignore this field and infer the information using its flawed heuristic.
        // - [!] SDL backend does NOT correctly ignore viewports with the _NoInputs flag.
        //       Some backend are not able to handle that correctly. If a backend report an hovered viewport that has the _NoInputs flag (e.g. when dragging a window
        //       for docking, the viewport has the _NoInputs flag in order to allow us to find the viewport under), then Dear ImGui is forced to ignore the value reported
        //       by the backend, and use its flawed heuristic to guess the viewport behind.
        // - [X] SDL backend correctly reports this regardless of another viewport behind focused and dragged from (we need this to find a useful drag and drop target).
        if ((io.BackendFlags & ImGuiBackendFlags.HasMouseHoveredViewport) != 0)
        {
            uint mouse_viewport_id = 0;
            ImGuiViewportPtr mouse_viewport = GetViewportForWindowID((nint)bd.MouseWindowID);
            if (mouse_viewport.NativePtr != null)
                mouse_viewport_id = mouse_viewport.ID;
            io.AddMouseViewportEvent(mouse_viewport_id);
        }

    }

    internal static void UpdateMouseCursor()
    {
        ImGuiIOPtr io = ImGui.GetIO();

        if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0)
            return;

        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();

        ImGuiMouseCursor imgui_cursor = ImGui.GetMouseCursor();
        if (io.MouseDrawCursor || imgui_cursor == ImGuiMouseCursor.None)
        {
            // Hide OS mouse cursor if imgui is drawing it or if it wants no cursor
            SDL_ShowCursor((int)SDL_bool.SDL_FALSE);
        }
        else
        {
            // Show OS mouse cursor
            var expected_cursor = bd.MouseCursors.ContainsKey(imgui_cursor) ? bd.MouseCursors[imgui_cursor] : bd.MouseCursors[ImGuiMouseCursor.Arrow];
            if (bd.MouseLastCursor != expected_cursor)
            {
                SDL_SetCursor(expected_cursor); // SDL function doesn't have an early out (see #6113)
                bd.MouseLastCursor = expected_cursor;
            }
            SDL_ShowCursor((int)SDL_bool.SDL_TRUE);
        }
    }

    static void CloseGamepads()
    {
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        if (bd.GamepadMode != GamepadMode.Manual)
            foreach (var gamepad in bd.Gamepads)
                SDL_GameControllerClose(gamepad);
        bd.Gamepads.Clear();
    }
    static void SetGamepadMode(GamepadMode mode, Span<nint> manual_gamepads)
    {
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        CloseGamepads();
        if (mode == GamepadMode.Manual)
        {
            // IM_ASSERT(manual_gamepads_array != nullptr && manual_gamepads_count > 0);
            for (int n = 0; n < manual_gamepads.Length; n++)
                bd.Gamepads.Add(manual_gamepads[n]);
        }
        else
        {
            //IM_ASSERT(manual_gamepads_array == nullptr && manual_gamepads_count <= 0);
            bd.WantUpdateGamepadsList = true;
        }
        bd.GamepadMode = mode;
    }
    static void UpdateGamepadButton(ref ImGui_ImplSDL2_Data bd, ImGuiIOPtr io, ImGuiKey key, SDL_GameControllerButton button_no)
    {
        bool merged_value = false;
        foreach (var gamepad in bd.Gamepads)
            merged_value |= SDL_GameControllerGetButton(gamepad, button_no) != 0;
        io.AddKeyEvent(key, merged_value);
    }
    internal static void UpdateGamepadAnalog(ref ImGui_ImplSDL2_Data bd, ImGuiIOPtr io, ImGuiKey key, SDL_GameControllerAxis axis_no, float v0, float v1)
    {
        float Saturate(float v) { return v < 0.0f ? 0.0f : v > 1.0f ? 1.0f : v; }

        float merged_value = 0.0f;
        foreach (var gamepad in bd.Gamepads)
        {
            float vn = Saturate((float)(SDL_GameControllerGetAxis(gamepad, axis_no) - v0) / (float)(v1 - v0));
            if (merged_value < vn)
                merged_value = vn;
        }
        io.AddKeyAnalogEvent(key, merged_value > 0.1f, merged_value);
    }
    internal static void UpdateGamepads()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();

        // Update list of controller(s) to use
        if (bd.WantUpdateGamepadsList && bd.GamepadMode != GamepadMode.Manual)
        {
            CloseGamepads();
            int joystick_count = SDL_NumJoysticks();
            for (int n = 0; n < joystick_count; n++)
            {
                if (SDL_IsGameController(n) == SDL_bool.SDL_TRUE)
                {
                    nint gamepad = SDL_GameControllerOpen(n);
                    if (gamepad != 0)
                    {
                        bd.Gamepads.Add(gamepad);
                        if (bd.GamepadMode == GamepadMode.AutoFirst)
                            break;
                    }
                }
            }

            bd.WantUpdateGamepadsList = false;
        }

        // FIXME: Technically feeding gamepad shouldn't depend on this now that they are regular inputs.
        if ((io.ConfigFlags & ImGuiConfigFlags.NavEnableGamepad) == 0)
            return;
        io.BackendFlags &= ~ImGuiBackendFlags.HasGamepad;
        if (bd.Gamepads.Count == 0)
            return;
        io.BackendFlags |= ImGuiBackendFlags.HasGamepad;

        // Update gamepad inputs
        const int thumb_dead_zone = 8000; // SDL_gamecontroller.h suggests using this value.
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadStart, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadBack, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadFaceLeft, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X);              // Xbox X, PS Square
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadFaceRight, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B);              // Xbox B, PS Circle
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadFaceUp, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y);              // Xbox Y, PS Triangle
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadFaceDown, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A);              // Xbox A, PS Cross
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadDpadLeft, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadDpadRight, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadDpadUp, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadDpadDown, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadL1, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadR1, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadL2, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERLEFT, 0.0f, 32767);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadR2, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERRIGHT, 0.0f, 32767);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadL3, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSTICK);
        UpdateGamepadButton(ref bd, io, ImGuiKey.GamepadR3, SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSTICK);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadLStickLeft, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX, -thumb_dead_zone, -32768);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadLStickRight, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX, +thumb_dead_zone, +32767);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadLStickUp, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY, -thumb_dead_zone, -32768);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadLStickDown, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY, +thumb_dead_zone, +32767);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadRStickLeft, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTX, -thumb_dead_zone, -32768);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadRStickRight, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTX, +thumb_dead_zone, +32767);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadRStickUp, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTY, -thumb_dead_zone, -32768);
        UpdateGamepadAnalog(ref bd, io, ImGuiKey.GamepadRStickDown, SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTY, +thumb_dead_zone, +32767);
    }

    static unsafe void UpdateMonitors()
    {
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        Marshal.FreeHGlobal(platform_io.NativePtr->Monitors.Data);
        bd.WantUpdateMonitors = false;

        int display_count = SDL_GetNumVideoDisplays();

        IntPtr data = Marshal.AllocHGlobal(Unsafe.SizeOf<ImGuiPlatformMonitor>() * display_count);
        platform_io.NativePtr->Monitors = new ImVector(2, 2, data);

        for (int n = 0; n < display_count; n++)
        {
            // Warning: the validity of monitor DPI information on Windows depends on the application DPI awareness settings, which generally needs to be set in the manifest or at runtime.
            ImGuiPlatformMonitorPtr monitor = platform_io.Monitors[n];
            SDL_Rect r;
            SDL_GetDisplayBounds(n, out r);
            monitor.MainPos = monitor.WorkPos = new Vector2((float)r.x, (float)r.y);
            monitor.MainSize = monitor.WorkSize = new Vector2((float)r.w, (float)r.h);
            SDL_GetDisplayUsableBounds(n, out r);
            monitor.WorkPos = new Vector2((float)r.x, (float)r.y);
            monitor.WorkSize = new Vector2((float)r.w, (float)r.h);
            // FIXME-VIEWPORT: On MacOS SDL reports actual monitor DPI scale, ignoring OS configuration. We may want to set
            //  DpiScale to cocoa_window.backingScaleFactor here.
            if (SDL_GetDisplayDPI(n, out float dpi, out _, out _) != 0)
            {
                if (dpi <= 0.0f)
                    continue; // Some accessibility applications are declaring virtual monitors with a DPI of 0, see #7902.
                monitor.DpiScale = dpi / 96.0f;
            }
            monitor.PlatformHandle = n;
        }
    }

    public unsafe static void NewFrame()
    {
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        ImGuiIOPtr io = ImGui.GetIO();

        // Setup display size (every frame to accommodate for window resizing)
        int w, h;
        int display_w, display_h;

        SDL_GetWindowSize(bd.Window, out w, out h);

        if ((SDL_GetWindowFlags(bd.Window) & (uint)SDL_WindowFlags.SDL_WINDOW_MINIMIZED) != 0)
            w = h = 0;
        // if (bd.Renderer != nullptr)
        //    SDL_GetRendererOutputSize(bd->Renderer, &display_w, &display_h);
        //else
        SDL_GL_GetDrawableSize(bd.Window, out display_w, out display_h);

        io.DisplaySize = new Vector2((float)w, (float)h);
        if (w > 0 && h > 0)
            io.DisplayFramebufferScale = new Vector2((float)display_w / w, (float)display_h / h);

        // Update monitors
        if (bd.WantUpdateMonitors)
            UpdateMonitors();

        // Setup time step (we don't use SDL_GetTicks() because it is using millisecond resolution)
        // (Accept SDL_GetPerformanceCounter() not returning a monotonically increasing value. Happens in VMs and Emscripten, see #6189, #6114, #3644)
        ulong frequency = SDL_GetPerformanceFrequency();
        ulong current_time = SDL_GetPerformanceCounter();
        if (current_time <= bd.Time)
            current_time = bd.Time + 1;
        io.DeltaTime = bd.Time > 0 ? (float)((double)(current_time - bd.Time) / frequency) : (float)(1.0f / 60.0f);
        bd.Time = current_time;

        if (bd.MouseLastLeaveFrame >= ImGui.GetFrameCount() && bd.MouseButtonsDown == 0)
        {
            bd.MouseWindowID = 0;
            bd.MouseLastLeaveFrame = 0;
            io.AddMousePosEvent(float.MaxValue, float.MinValue);
        }

        // Our io.AddMouseViewportEvent() calls will only be valid when not capturing.
        // Technically speaking testing for 'bd->MouseButtonsDown == 0' would be more rigorous, but testing for payload reduces noise and potential side-effects.
        if (bd.MouseCanReportHoveredViewport && ImGui.GetDragDropPayload().NativePtr == null)
            io.BackendFlags |= ImGuiBackendFlags.HasMouseHoveredViewport;
        else
            io.BackendFlags &= ~ImGuiBackendFlags.HasMouseHoveredViewport;

        UpdateMouseData();
        UpdateMouseCursor();

        // Update game controllers (if enabled and available)
        UpdateGamepads();
    }


    //--------------------------------------------------------------------------------------------------------
    // MULTI-VIEWPORT / PLATFORM INTERFACE SUPPORT
    // This is an _advanced_ and _optional_ feature, allowing the backend to create and handle multiple viewports simultaneously.
    // If you are new to dear imgui or creating a new binding for dear imgui, it is recommended that you completely ignore this section first..
    //--------------------------------------------------------------------------------------------------------

    public struct ImGui_ImplSDL2_ViewportData
    {
        public nint Window;
        public UInt32 WindowID;
        public bool WindowOwned;
        //SDL_GLcontext GLContext;
    }

    static unsafe void CreateWindow(ImGuiViewportPtr viewport)
    {
        ref ImGui_ImplSDL2_Data bd = ref GetBackendData();
        nint ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ImGui_ImplSDL2_ViewportData>());
        ref ImGui_ImplSDL2_ViewportData vd = ref Unsafe.AsRef<ImGui_ImplSDL2_ViewportData>((void*)ptr);

        viewport.PlatformUserData = ptr;


        ImGuiViewportPtr main_viewport = ImGui.GetMainViewport();
        ref ImGui_ImplSDL2_ViewportData main_viewport_data = ref Unsafe.AsRef<ImGui_ImplSDL2_ViewportData>((void*)main_viewport.PlatformUserData);


        SDL_WindowFlags sdl_flags = 0;

        sdl_flags |= (bd.UseVulkan ? SDL_WindowFlags.SDL_WINDOW_VULKAN : 0);
        sdl_flags |= ((SDL_WindowFlags)SDL_GetWindowFlags(bd.Window) & SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI);
        sdl_flags |= SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        sdl_flags |= ((viewport.Flags & ImGuiViewportFlags.NoDecoration) != 0) ? SDL_WindowFlags.SDL_WINDOW_BORDERLESS : 0;
        sdl_flags |= ((viewport.Flags & ImGuiViewportFlags.NoDecoration) != 0) ? 0 : SDL_WindowFlags.SDL_WINDOW_RESIZABLE;

        if (OperatingSystem.IsWindows())
        {
            // See SDL hack in ImGui_ImplSDL2_ShowWindow().
            sdl_flags |= (((viewport.Flags & ImGuiViewportFlags.NoTaskBarIcon) != 0)) ? SDL_WindowFlags.SDL_WINDOW_SKIP_TASKBAR : 0;
        }
        sdl_flags |= ((viewport.Flags & ImGuiViewportFlags.TopMost) != 0) ? SDL_WindowFlags.SDL_WINDOW_ALWAYS_ON_TOP : 0;

        vd.Window = SDL_CreateWindow("No Title Yet", (int)viewport.Pos.X, (int)viewport.Pos.Y, (int)viewport.Size.X, (int)(int)viewport.Size.Y, sdl_flags);
        vd.WindowOwned = true;

        viewport.PlatformHandle = (nint)SDL_GetWindowID(vd.Window);
        viewport.PlatformHandleRaw = 0;

        SDL_SysWMinfo info = new();
        SDL_VERSION(out info.version);
        if (SDL_GetWindowWMInfo(vd.Window, ref info) == SDL_bool.SDL_TRUE)
        {
            if (OperatingSystem.IsWindows())
            {
                viewport.PlatformHandleRaw = info.info.win.window;
            }
            else if (OperatingSystem.IsMacOS())
            {
                viewport.PlatformHandleRaw = info.info.cocoa.window;
            }
        }
    }

    static unsafe void DestroyWindow(ImGuiViewportPtr viewport)
    {

        if (viewport.PlatformUserData != 0)
        {
            ref ImGui_ImplSDL2_ViewportData vd = ref Unsafe.AsRef<ImGui_ImplSDL2_ViewportData>((void*)viewport.PlatformUserData);


            //if (vd.GLContext && vd.WindowOwned)
            //    SDL_GL_DeleteContext(vd.GLContext);
            if (vd.Window != 0 && vd.WindowOwned)
                SDL_DestroyWindow(vd.Window);
            //vd.GLContext = nullptr;
            vd.Window = 0;
            Marshal.FreeHGlobal(viewport.PlatformUserData);
        }
        viewport.PlatformUserData = viewport.PlatformHandle = 0;
    }

    static unsafe void ShowWindow(ImGuiViewportPtr viewport)
    {
        ref ImGui_ImplSDL2_ViewportData vd = ref Unsafe.AsRef<ImGui_ImplSDL2_ViewportData>((void*)viewport.PlatformUserData);
        SDL_ShowWindow(vd.Window);
    }
    static unsafe Vector2 GetWindowPos(ImGuiViewport* viewport)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;

        SDL_GetWindowPosition(vd->Window, out int x, out int y);
        return new Vector2((float)x, (float)y);
    }

    static unsafe void SetWindowPos(ImGuiViewport* viewport, Vector2 pos)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        SDL_SetWindowPosition(vd->Window, (int)pos.X, (int)pos.Y);
    }

    static unsafe Vector2 GetWindowSize(ImGuiViewport* viewport)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        SDL_GetWindowSize(vd->Window, out int w, out int h);
        return new Vector2((float)w, (float)h);
    }

    static unsafe void SetWindowSize(ImGuiViewport* viewport, Vector2 size)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        SDL_SetWindowSize(vd->Window, (int)size.X, (int)size.Y);
    }

    static unsafe void SetWindowTitle(ImGuiViewport* viewport, char* title)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        SDL_SetWindowTitle(vd->Window, Marshal.PtrToStringUTF8((nint)title));
    }

    static unsafe void SetWindowAlpha(ImGuiViewport* viewport, float alpha)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        SDL_SetWindowOpacity(vd->Window, alpha);
    }

    static unsafe void SetWindowFocus(ImGuiViewport* viewport)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        SDL_RaiseWindow(vd->Window);
    }

    static unsafe bool GetWindowFocus(ImGuiViewport* viewport)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        return ((SDL_WindowFlags)SDL_GetWindowFlags(vd->Window) & SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS) != 0;
    }

    static unsafe bool GetWindowMinimized(ImGuiViewport* viewport)
    {
        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        return ((SDL_WindowFlags)SDL_GetWindowFlags(vd->Window) & SDL_WindowFlags.SDL_WINDOW_MINIMIZED) != 0;
    }

    static unsafe void RenderWindow(ImGuiViewport* viewport, void* unused)
    {
        /*ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
        if (vd->GLContext)
            SDL_GL_MakeCurrent(vd->Window, vd->GLContext);*/
    }

    static unsafe void SwapBuffers(ImGuiViewport* viewport, void* unused)
    {
        /* ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)viewport->PlatformUserData;
         if (vd->GLContext)
         {
             SDL_GL_MakeCurrent(vd->Window, vd->GLContext);
             SDL_GL_SwapWindow(vd->Window);
         }*/
    }

    static unsafe void InitPlatformInterface(nint window)
    {
        // Register platform interface (will be coupled with a renderer interface)
        ImGuiPlatformIOPtr platform_io = ImGui.GetPlatformIO();
        platform_io.Platform_CreateWindow = Marshal.GetFunctionPointerForDelegate(CreateWindow);
        platform_io.Platform_DestroyWindow = Marshal.GetFunctionPointerForDelegate(DestroyWindow);
        platform_io.Platform_ShowWindow = Marshal.GetFunctionPointerForDelegate(ShowWindow);
        platform_io.Platform_SetWindowPos = Marshal.GetFunctionPointerForDelegate(SetWindowPos);
        platform_io.Platform_GetWindowPos = Marshal.GetFunctionPointerForDelegate(GetWindowPos);
        platform_io.Platform_SetWindowSize = Marshal.GetFunctionPointerForDelegate(SetWindowSize);
        platform_io.Platform_GetWindowSize = Marshal.GetFunctionPointerForDelegate(GetWindowSize);
        platform_io.Platform_SetWindowFocus = Marshal.GetFunctionPointerForDelegate(SetWindowFocus);
        platform_io.Platform_GetWindowFocus = Marshal.GetFunctionPointerForDelegate(GetWindowFocus);
        platform_io.Platform_GetWindowMinimized = Marshal.GetFunctionPointerForDelegate(GetWindowMinimized);
        platform_io.Platform_SetWindowTitle = Marshal.GetFunctionPointerForDelegate(SetWindowTitle);
        platform_io.Platform_RenderWindow = Marshal.GetFunctionPointerForDelegate(RenderWindow);
        platform_io.Platform_SwapBuffers = Marshal.GetFunctionPointerForDelegate(SwapBuffers);
        platform_io.Platform_SetWindowAlpha = Marshal.GetFunctionPointerForDelegate(SetWindowAlpha);

        // Register main window handle (which is owned by the main application, not by us)
        // This is mostly for simplicity and consistency, so that our code (e.g. mouse handling etc.) can use same logic for main and secondary viewports.
        ImGuiViewportPtr main_viewport = ImGui.GetMainViewport();


        ImGui_ImplSDL2_ViewportData* vd = (ImGui_ImplSDL2_ViewportData*)Marshal.AllocHGlobal(Marshal.SizeOf<ImGui_ImplSDL2_ViewportData>());
        vd->Window = window;
        vd->WindowID = SDL_GetWindowID(window);
        vd->WindowOwned = false;
        main_viewport.PlatformUserData = (nint)vd;
        main_viewport.PlatformHandle = (nint)vd->WindowID;
    }

}