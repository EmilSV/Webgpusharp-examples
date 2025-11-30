using WebGpuSharp;

class ManagedRenderPassDescriptor
{
    public string? Label = null;
    public required RenderPassColorAttachment[] ColorAttachments;
    public RenderPassDepthStencilAttachment? DepthStencilAttachment = null;
    public QuerySet? OcclusionQuerySet = null;
    public PassTimestampWrites? TimestampWrites = null;

    public ManagedRenderPassDescriptor()
    {

    }
}