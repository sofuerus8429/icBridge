using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicBridge.Audio.Devices;

/// <summary>System endpoint notifications; no idle polling or device enumeration.</summary>
public sealed class AudioDeviceNotifications : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private int _disposed;
    public event Action? Changed;
    public AudioDeviceNotifications() => _enumerator.RegisterEndpointNotificationCallback(this);
    private void Notify()
    {
        if (Volatile.Read(ref _disposed) == 0) Changed?.Invoke();
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Notify();
    public void OnDeviceAdded(string deviceId) => Notify();
    public void OnDeviceRemoved(string deviceId) => Notify();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Notify();
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _enumerator.UnregisterEndpointNotificationCallback(this); _enumerator.Dispose();
    }
}
