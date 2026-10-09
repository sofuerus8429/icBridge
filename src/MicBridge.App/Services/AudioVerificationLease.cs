namespace MicBridge.App.Services;

// Only diagnostic commands acquire this lease. Normal application startup does
// not create it, and helper tone processes remain part of their parent's test.
internal sealed class AudioVerificationLease : IDisposable
{
    private readonly Semaphore _semaphore = new(1, 1, "Local\\MicBridge.AudioVerification");
    private int _disposed;
    internal AudioVerificationLease()
    {
        if (_semaphore.WaitOne(0)) return;
        _semaphore.Dispose();
        throw new InvalidOperationException("另一个音频验证正在使用设备，请等待它完成后重试。");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _semaphore.Release(); _semaphore.Dispose();
    }
}
