using System.Diagnostics;

namespace MicBridge.App.Services;

// Bounded diagnostic load only. Normal application startup never constructs this class.
internal sealed class VerificationCpuLoad : IDisposable
{
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread[] _workers;
    internal int Workers => _workers.Length;
    internal VerificationCpuLoad(bool enabled)
    {
        int count = enabled ? Math.Min(4, Math.Max(1, Environment.ProcessorCount / 4)) : 0;
        _workers = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            _workers[i] = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "MicBridge verification CPU load" };
            _workers[i].Start();
        }
    }
    private void Work()
    {
        while (!_stop.IsSet)
        {
            long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50;
            while (!_stop.IsSet && Stopwatch.GetTimestamp() < end) Thread.SpinWait(256);
            _stop.Wait(5);
        }
    }
    public void Dispose()
    {
        _stop.Set(); foreach (var worker in _workers) worker.Join(); _stop.Dispose();
    }
}
