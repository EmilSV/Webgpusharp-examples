
using WebGpuSharp;

class MangedRenderPassDescriptor
{
    public string? Label;
    public required RenderPassColorAttachment[] ColorAttachments;
    public RenderPassDepthStencilAttachment? DepthStencilAttachment;
    public QuerySet? OcclusionQuerySet;
    public PassTimestampWrites? TimestampWrites;
}