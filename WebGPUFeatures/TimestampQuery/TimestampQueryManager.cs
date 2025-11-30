// Regroups all timestamp-related operations and resources.
using System.Diagnostics.CodeAnalysis;
using WebGpuSharp;

using GPUBuffer = WebGpuSharp.Buffer;


public class TimestampQueryManager
{
    private ref struct MappedRangeState
    {
        public ref ulong ElapsedNs;
    }

    // The device may not support timestamp queries, on which case this whole
    // class does nothing.
    public bool TimestampSupported;

    // The query objects. This is meant to be used in a ComputePassDescriptor's
    // or RenderPassDescriptor's 'timestampWrites' field.
    public QuerySet? TimestampQuerySet;

    // A buffer where to store query results
    public GPUBuffer? TimestampBuffer;

    // A buffer to map this result back to CPU
    public GPUBuffer? TimestampMapBuffer;

    // Callback to call when results are available.
    public Action<double>? Callback;

    public TimestampQueryManager(
        Device device,
        Action<double> callback
    )
    {
        TimestampSupported = device.HasFeature(FeatureName.TimestampQuery);
        if (!TimestampSupported) return;

        Callback = callback;

        // Create timestamp queries
        TimestampQuerySet = device.CreateQuerySet(new()
        {
            Type = QueryType.Timestamp,
            Count = 2, // begin and end
        });

        // Create a buffer where to store the result of GPU queries
        TimestampBuffer = device.CreateBuffer(new()
        {
            Size = TimestampQuerySet.GetCount() * sizeof(ulong),
            Usage = BufferUsage.CopySrc | BufferUsage.QueryResolve,
        });

        // Create a buffer to map the result back to the CPU
        TimestampMapBuffer = device.CreateBuffer(new()
        {
            Size = TimestampBuffer.GetSize(),
            Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
        });
    }

    // Add both a start and end timestamp.
    public RenderPassDescriptor addTimestampWrite(ref RenderPassDescriptor passDescriptor)
    {
        if (TimestampSupported)
        {
            // We instruct the render pass to write to the timestamp query before/after
            passDescriptor.TimestampWrites = new()
            {
                QuerySet = TimestampQuerySet!,
                BeginningOfPassWriteIndex = 0,
                EndOfPassWriteIndex = 1
            };
        }
        return passDescriptor;
    }


    // Resolve the timestamp queries and copy the result into the mappable buffer if possible.
    public void Resolve(CommandEncoder commandEncoder)
    {
        if (!TimestampSupported) return;

        // After the end of the measured render pass, we resolve queries into a
        // dedicated buffer.
        commandEncoder.ResolveQuerySet(
            querySet: TimestampQuerySet!,
            firstQuery: 0 /* firstQuery */,
            queryCount: TimestampQuerySet!.GetCount() /* queryCount */,
            destination: TimestampBuffer!,
            destinationOffset: 0 /* destinationOffset */
        );

        if (TimestampMapBuffer!.GetMapState() == BufferMapState.Unmapped)
        {
            // Copy values to the mappable buffer
            commandEncoder.CopyBufferToBuffer(
                TimestampBuffer!,
                TimestampMapBuffer
            );
        }
    }


    // Read the values of timestamps.
    public async void TryInitiateTimestampDownload()
    {
        if (!TimestampSupported) return;
        if (TimestampMapBuffer!.GetMapState() != BufferMapState.Unmapped) return;

        var buffer = TimestampMapBuffer;
        var mapState = await buffer.MapAsync(MapMode.Read);
        ulong elapsedNs = 0;
        buffer.GetConstMappedRange<ulong, MappedRangeState>(static (timeStamps, state) =>
        {
            // Subtract the begin time from the end time.
            // Cast into number. Number can be 9007199254740991 as max integer
            state.ElapsedNs = timeStamps[1] - timeStamps[0];
        }, new MappedRangeState() { ElapsedNs = ref elapsedNs });

        // It's possible elapsedNs is negative which means it's invalid
        // (see spec https://gpuweb.github.io/gpuweb/#timestamp)
        if (elapsedNs >= 0)
        {
            Callback!(elapsedNs);
        }

        buffer.Unmap();
    }
}
