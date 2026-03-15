using ImGuiNET;
using Nito.AsyncEx;
using SDL2;
using WebGpuSharp;
using static SDL2.SDL;
namespace Setup;

public class SetupWebGPU
{
    private const uint INITI_ARGS = SDL_INIT_AUDIO | SDL_INIT_VIDEO | SDL_INIT_EVENTS | SDL_INIT_TIMER | SDL_INIT_GAMECONTROLLER;

    public static int Run(string name, int width, int height, Func<RunContext, Task> callback) =>
        Run(WebGPU.CreateInstance()!, name, width, height, callback);

    public static int Run(Instance instance, string name, int width, int height, Func<RunContext, Task> callback)
    {
        if (OperatingSystem.IsBrowser())
        {
            _ = RunBrowser(instance, name, width, height, callback);
            return 1;
        }
        else
        {
            return RunNative(instance, name, width, height, callback);
        }
    }

    private static int RunNative(Instance instance, string name, int width, int height, Func<RunContext, Task> callback) =>
        //To allow async event though SDL2 by only using the same thread as the call 
        AsyncContext.Run(async () =>
        {
            SDL_SetMainReady();
            if (SDL_Init(INITI_ARGS) < 0)
            {
                Console.Error.WriteLine($"Could not initialize SDL! Error: {SDL_GetError()}");
                return 1;
            }

            SDL_WindowFlags windowFlags = SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI;
            var window = SDL_CreateWindow(name, SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, width, height, windowFlags);

            var surface = SDLWebgpu.SDL_GetWGPUSurface(instance, window)!;

            var runContext = new RunContext(instance, surface, window);

            await callback(runContext);
            bool shouldClose = false;
            while (!shouldClose)
            {
                while (SDL_PollEvent(out var @event) != 0)
                {
                    bool handled = runContext.ProcessGuiEvents(@event);
                    switch (@event.type)
                    {
                        case SDL_EventType.SDL_QUIT:
                            shouldClose = true;
                            break;

                        default:
                            runContext.Input.HandleEvent(@event);
                            break;
                    }
                }
                runContext.InvokeOnFrame();
            }
            SDL_DestroyWindow(window);
            SDL_Quit();

            return 0;
        });

    private static async Task RunBrowser(Instance instance, string name, int width, int height, Func<RunContext, Task> callback)
    {
        try
        {
            SDL_SetMainReady();
            if (SDL_Init(INITI_ARGS) < 0)
            {
                Console.Error.WriteLine($"Could not initialize SDL! Error: {SDL_GetError()}");
            }

            SDL_WindowFlags windowFlags = SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI;
            var window = SDL_CreateWindow(name, SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, width, height, windowFlags);

            var surface = SDLWebgpu.SDL_GetWGPUSurface(instance, window)!;

            var runContext = new RunContext(instance, surface, window);

            await callback(runContext);
            EmscriptenInterop.emscriptenSetMainLoop(() =>
            {
                bool shouldClose = false;
                while (SDL_PollEvent(out var @event) != 0)
                {
                    bool handled = runContext.ProcessGuiEvents(@event);
                    switch (@event.type)
                    {
                        case SDL_EventType.SDL_QUIT:
                            shouldClose = true;
                            break;

                        default:
                            runContext.Input.HandleEvent(@event);
                            break;
                    }
                }
                if (shouldClose)
                {
                    EmscriptenInterop.emscripten_cancel_main_loop();
                    SDL_DestroyWindow(window);
                    SDL_Quit();
                    return;
                }

                runContext.InvokeOnFrame();
            }, 0, 1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"An error occurred: {ex}");
        }
    }
}
