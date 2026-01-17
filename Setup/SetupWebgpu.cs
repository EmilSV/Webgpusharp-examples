using ImGuiNET;
using Nito.AsyncEx;
using SDL2;
using WebGpuSharp;
using static SDL2.SDL;
namespace Setup;

public class SetupWebGPU
{
    public static int Run(string name, int width, int height, Func<RunContext, Task> callback) =>
        Run(WebGPU.CreateInstance()!, name, width, height, callback);

    public static int Run(Instance instance, string name, int width, int height, Func<RunContext, Task> callback) =>
    //To allow async event though SDL2 by only using the same thread as the call 
    AsyncContext.Run(async () =>
    {
        SDL_SetMainReady();
        if (SDL_Init(SDL_INIT_EVERYTHING) < 0)
        {
            Console.Error.WriteLine($"Could not initialize SDL! Error: {SDL_GetError()}");
            return 1;
        }

        SDL_WindowFlags windowFlags = 0;
        var window = SDL_CreateWindow(name, SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, width, height, windowFlags);

        var surface = SDLWebgpu.SDL_GetWGPUSurface(instance, window)!;

        var runContext = new RunContext(instance, surface, window);

        await callback(runContext);
        bool shouldClose = false;
        while (!shouldClose)
        {
            while (SDL_PollEvent(out var @event) != 0)
            {
                bool handled = runContext.ProcessEventIMGUi(@event);
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
}
