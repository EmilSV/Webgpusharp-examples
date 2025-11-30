// A minimalistic perf timer class that computes mean + stddev online
class PerfCounter
{
    public double SampleCount = 0;
    public double Accumulated = 0;
    public double AccumulatedSq = 0;

    public void AddSample(double value)
    {
        SampleCount += 1;
        Accumulated += value;
        AccumulatedSq += value * value;
    }

    public double GetAverage()
    {
        return SampleCount == 0 ? 0 : Accumulated / SampleCount;
    }

    public double GetStddev()
    {
        if (SampleCount == 0) return 0;
        var avg = GetAverage();
        var variance = AccumulatedSq / SampleCount - avg * avg;
        return Math.Sqrt(Math.Max(0.0, variance));
    }
}
