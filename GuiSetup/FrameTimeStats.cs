using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Setup;

public class FrameTimeStats
{
    private long? _lastFrameTimeStamp;

    private readonly List<float> _frameTimes = new();
    private readonly List<long> _startFrameTimeStamps = new();

    public double FrameHistoryLengthSeconds { get; init; } = 1;
    public float MinMeasurementTimeMilliseconds { get; init; } = 0;
    public float MaxMeasurementTimeMilliseconds { get; init; } = 15f;

    public void EndFrame()
    {
        if (_lastFrameTimeStamp == null)
        {
            _lastFrameTimeStamp = Stopwatch.GetTimestamp();
            return;
        }

        var currentFrameTimeStamp = _lastFrameTimeStamp.Value;
        _lastFrameTimeStamp = Stopwatch.GetTimestamp();

        var frameInMilliseconds = Stopwatch.GetElapsedTime(currentFrameTimeStamp).TotalMilliseconds;
        _frameTimes.Add((float)frameInMilliseconds);
        _startFrameTimeStamps.Add(currentFrameTimeStamp);

        if (_frameTimes.Count != _startFrameTimeStamps.Count)
        {
            throw new Exception("Frame times and start frame times are not the same length");
        }

        var Length = _startFrameTimeStamps.Count;
        int removeCount = 0;
        for (int i = 0; i < Length; i++)
        {
            var frameTimeStamp = _startFrameTimeStamps[i];
            var elapsedTime = Stopwatch.GetElapsedTime(frameTimeStamp).TotalSeconds;
            if (elapsedTime > FrameHistoryLengthSeconds)
            {
                removeCount++;
            }
            else
            {
                break;
            }
        }

        // Remove the oldest frames
        if (removeCount > 0)
        {
            _frameTimes.RemoveRange(0, removeCount);
            _startFrameTimeStamps.RemoveRange(0, removeCount);
        }
    }

    public void DrawGUI()
    {
        ImGuiUtils.PlotLines(
            label: "Frame Times",
            values: CollectionsMarshal.AsSpan(_frameTimes), valuesOffset: 0,
            overlayText: $"Max {MaxMeasurementTimeMilliseconds}",
            scaleMin: MinMeasurementTimeMilliseconds,
            scaleMax: MaxMeasurementTimeMilliseconds,
            graphSize: new Vector2(100f, 50f)
        );
    }
}