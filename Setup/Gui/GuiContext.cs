using ImGuiNET;
using WebGpuSharp;

public class GuiContext
{
    private IntPtr window;
    private Device device;

    internal GuiContext(IntPtr window)
    {
        this.window = window;
    }

    public void SetupIMGUI(Device device, TextureFormat ttFormat)
    {
        this.device = device;

        IntPtr context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        unsafe
        {
            io.NativePtr->IniFilename = null;
        }
        
        var initInfo = new ImGui_Impl_WebGPUSharp.ImGui_ImplWGPU_InitInfo()
        {
            device = device,
            num_frames_in_flight = 3,
            rt_format = ttFormat,
            depth_format = TextureFormat.Undefined,
        };

        ImGui_Impl_WebGPUSharp.Init(initInfo);
        ImGui_Impl_SDL2.Init(window);

        io.Fonts.AddFontDefault();
        io.Fonts.Build();
    }

    public void NewFrame()
    {
        ImGui_Impl_SDL2.NewFrame();
        ImGui_Impl_WebGPUSharp.NewFrame();
        ImGui.NewFrame();
    }

    public void EndFrame()
    {
        ImGui.EndFrame();
    }

    public CommandBuffer? Render(Surface surface)
    {
        // Perform rendering
        SurfaceTexture surfaceTexture = surface.GetCurrentTexture();
        // Failed to get the surface texture. TODO handle
        if (surfaceTexture.Status is not (SurfaceGetCurrentTextureStatus.SuccessOptimal or SurfaceGetCurrentTextureStatus.SuccessSuboptimal))
            return null;

        TextureViewDescriptor viewdescriptor = new()
        {
            Format = surfaceTexture.Texture.GetFormat(),
            Dimension = TextureViewDimension.D2,
            MipLevelCount = 1,
            BaseMipLevel = 0,
            BaseArrayLayer = 0,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All,
        };
        TextureView textureView = surfaceTexture.Texture.CreateView(viewdescriptor) ?? throw new Exception("Failed to create texture view");

        // Command Encoder
        var commandEncoder = device.CreateCommandEncoder(new() { Label = "Main Command Encoder" });

        Span<RenderPassColorAttachment> colorAttachments = [
            new(){
                        View = textureView,
                        ResolveTarget = default,
                        LoadOp = LoadOp.Load,
                        StoreOp = StoreOp.Store,
                        ClearValue = new Color(0,0,0,0)
                    }
        ];

        // Render Imgui
        {
            RenderPassDescriptor renderPassDesc = new()
            {
                label = "Pass IMGUI",
                ColorAttachments = colorAttachments,
                DepthStencilAttachment = null
            };
            var RenderPassEncoder = commandEncoder.BeginRenderPass(renderPassDesc);

            ImGui.Render();
            ImGui_Impl_WebGPUSharp.RenderDrawData(ImGui.GetDrawData(), RenderPassEncoder);

            RenderPassEncoder.End();
        }

        // Finish Rendering
        return commandEncoder.Finish(new() { });
    }
}