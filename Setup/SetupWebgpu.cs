﻿using Nito.AsyncEx;
using SDL2;
using WebGpuSharp;
using static SDL2.SDL;
namespace Setup;

public class SetupWebGPU
{
    public static int Run(string name, Func<Instance, Surface, Action<Action>, Task> callback) =>
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
            var window = SDL_CreateWindow(name, SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, 640, 480, windowFlags);

            var instance = WebGPU.CreateInstance()!;

            var surface = SDLWebgpu.SDL_GetWGPUSurface(instance, window)!;

            await callback(instance, surface, (action) =>
           {
               bool shouldClose = false;
               while (!shouldClose)
               {
                   while (SDL_PollEvent(out var @event) != 0)
                   {
                       switch (@event.type)
                       {
                           case SDL_EventType.SDL_QUIT:
                               shouldClose = true;
                               break;

                           default:
                               break;
                       }
                   }
                   action();
                   instance.ProcessEvents();
               }
               SDL_DestroyWindow(window);
               SDL_Quit();
           });

            return 0;
        });
}
