namespace MicBridge.Audio.Devices;

/// <summary>Verification is valid only for this session and the current device topology.</summary>
public sealed class HardwareRouteCache
{
    private readonly HashSet<(string Capture, string Output)> _routes = [];
    private readonly object _gate = new();
    private int _revision;
    public int Revision => Volatile.Read(ref _revision);
    public bool Contains(string captureId, string outputId) { lock (_gate) return _routes.Contains((captureId, outputId)); }
    public bool Confirm(string captureId, string outputId, int observedRevision)
    {
        lock (_gate)
        {
            if (observedRevision != _revision) return false;
            _routes.Add((captureId, outputId)); return true;
        }
    }
    public void Invalidate() { lock (_gate) { _routes.Clear(); _revision++; } }
}
