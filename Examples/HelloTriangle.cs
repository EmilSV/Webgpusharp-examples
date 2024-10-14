using System.Text;
using WebGpuSharp;
using static SDL2.SDL;
using static SDLWebgpu;


static class HelloTriangle
{
    static ReadOnlySpan<byte> ShaderSource =>
    """
    @vertex
    fn vs_main(@builtin(vertex_index) in_vertex_index: u32) -> @builtin(position) vec4f {
        var p = vec2f(0.0, 0.0);
        if (in_vertex_index == 0u) {
            p = vec2f(-0.5, -0.5);
        } else if (in_vertex_index == 1u) {
            p = vec2f(0.5, -0.5);
        } else {
            p = vec2f(0.0, 0.5);
        }
        return vec4f(p, 0.0, 1.0);
    }

    @fragment
    fn fs_main() -> @location(0) vec4f {
        return vec4f(0.0, 0.4, 1.0, 1.0);
    }
    """u8;


    public static async Task Run()
    {
        SDL_SetMainReady();
        if (SDL_Init(SDL_INIT_VIDEO) < 0)
        {
            Console.Error.WriteLine($"Could not initialize SDL! Error: {SDL_GetError()}");
        }

        SDL_WindowFlags windowFlags = 0;
        var window = SDL_CreateWindow("Hello Triangle", SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, 640, 480, windowFlags);

        var instance = WebGPU.CreateInstance()!;

        var surface = SDL_GetWGPUSurface(instance, window)!;

        var adapter = (await instance.RequestAdapterAsync(new()
        {
            CompatibleSurface = surface
        }))!;

        var device = (await adapter.RequestDeviceAsync(new()
        {
            Label = "My Device",
            DefaultQueue = new()
            {
                Label = "The default queue"
            },
            UncapturedErrorCallback = (type, message) =>
            {
                var messageString = Encoding.UTF8.GetString(message);
                Console.Error.WriteLine($"Uncaptured error: {type} {messageString}");
            },
            DeviceLostCallback = (reason, message) =>
            {
                var messageString = Encoding.UTF8.GetString(message);
                Console.Error.WriteLine($"Device lost: {reason} {messageString}");
            },
        }))!;

        var queue = device.GetQueue();

        var surfaceCapabilities = surface.GetCapabilities(adapter)!;

        var surfaceFormat = surfaceCapabilities.Formats[0];

        surface.Configure(new()
        {
            Width = 640,
            Height = 480,
            Usage = TextureUsage.RenderAttachment,
            Format = surfaceFormat,
            Device = device,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto,
        });

        ShaderModule CreateShaderModule()
        {
            ShaderModuleWGSLDescriptor shaderCodeDesc = new()
            {
                Code = ShaderSource
            };

            return device!.CreateShaderModule(new(ref shaderCodeDesc))!;
        }


        var shaderModule = CreateShaderModule();

        VertexState vertexState = new()
        {
            Module = shaderModule,
            EntryPoint = "vs_main",
        };

        MultisampleState multisample = new()
        {
            Count = 1,
            Mask = ~0u,
            AlphaToCoverageEnabled = false
        };

        var pipeline = device.CreateRenderPipeline(new()
        {
            Layout = null!,
            Vertex = ref vertexState,
            Primitive = new()
            {
                Topology = PrimitiveTopology.TriangleList,
                StripIndexFormat = IndexFormat.Undefined,
                FrontFace = FrontFace.CCW,
                CullMode = CullMode.None
            },
            Fragment = new FragmentState()
            {
                Module = shaderModule,
                EntryPoint = "fs_main",
                Targets = [
                      new(){
                        Format = surfaceFormat,
                        Blend = new()
                        {
                            Color = new()
                            {
                                SrcFactor = BlendFactor.SrcAlpha,
                                DstFactor = BlendFactor.OneMinusSrcAlpha,
                                Operation = BlendOperation.Add
                            },
                            Alpha = new()
                            {
                                SrcFactor = BlendFactor.Zero,
                                DstFactor = BlendFactor.One,
                                Operation = BlendOperation.Add
                            }
                        },
                        WriteMask = ColorWriteMask.All
                    }
                  ]
            },
            DepthStencil = null,
            Multisample = ref multisample
        });


        bool shouldClose = false;
        while (!shouldClose)
        {
            while (SDL_PollEvent(out SDL_Event @event) != 0)
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

            MainLoop();
        }

        TextureView? GetNextSurfaceTextureView()
        {
            SurfaceTexture surfaceTexture = surface!.GetCurrentTexture();
            if (surfaceTexture.Status != SurfaceGetCurrentTextureStatus.Success)
            {
                return null;
            }

            Texture texture = surfaceTexture.Texture!;
            return texture.CreateView(new()
            {
                Label = "Surface texture view",
                Format = texture.GetFormat(),
                Dimension = TextureViewDimension.D2,
                BaseMipLevel = 0,
                MipLevelCount = 1,
                BaseArrayLayer = 0,
                ArrayLayerCount = 1,
                Aspect = TextureAspect.All
            });
        }

        void MainLoop()
        {
            var targetView = GetNextSurfaceTextureView();
            if (targetView == null)
            {
                return;
            }

            CommandEncoder encoder = device.CreateCommandEncoder(new()
            {
                Label = "My command encoder"

            });

            RenderPassEncoder renderPass = encoder.BeginRenderPass(new()
            {
                ColorAttachments = [
                    new()
                    {
                        View = targetView,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearValue = new Color(0.9f, 0.1f, 0.2f, 1.0f),
                    }
                ],
            });

            renderPass.SetPipeline(pipeline!);
            renderPass.Draw(3, 1, 0, 0);

            renderPass.End();

            CommandBuffer commandBuffer = encoder.Finish(new()
            {
                Label = "My command buffer"
            });

            queue!.Submit([
                commandBuffer
            ]);


            surface.Present();

            instance.ProcessEvents();
        }

        SDL_DestroyWindow(window);
        SDL_Quit();
    }
}