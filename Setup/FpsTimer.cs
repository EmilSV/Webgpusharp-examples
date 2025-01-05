using System.Diagnostics;

namespace Setup;

public class FpsTimer
{
    private long _startFrameTimeStamp;
    private ulong _frameCount;
    public double MinMeasurementTimeSeconds { get; init; } = 1;

    public double? Fps { get; private set; }

    public void BeginRecording()
    {
        _startFrameTimeStamp = Stopwatch.GetTimestamp();
        _frameCount = 0;
    }

    public void FrameEnd()
    {
        _frameCount++;
        var secElapsed = Stopwatch.GetElapsedTime(_startFrameTimeStamp).TotalSeconds;
        if (secElapsed < MinMeasurementTimeSeconds)
        {
            return;
        }

        _startFrameTimeStamp = Stopwatch.GetTimestamp();
        Fps = _frameCount / secElapsed;
        _frameCount = 0;
    }
}