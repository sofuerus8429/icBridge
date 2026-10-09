using System.Runtime.InteropServices;
using MicBridge.Audio.Sources;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MicBridge.Audio.Interop;

/// <summary>Windows process-tree loopback. COM activation and capture live on a dedicated MTA thread.</summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private readonly int _processId;
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenRegistration _stopRegistration;
    private CancellationTokenRegistration _externalRegistration;
    private readonly ManualResetEvent _stop = new(false);
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private bool _disposed, _startedOnce;
    private int _multimediaScheduling;
    public static bool IsSupported => Platform.IsWindowsVersionAtLeast(10, 0, 20348);
    public CaptureBuffer Buffer { get; } = new(new WaveFormat(48000, 16, 2));
    public event Action<Exception>? Failed;
    public bool UsesMultimediaScheduling => Volatile.Read(ref _multimediaScheduling) != 0;

    public ProcessLoopbackCapture(int processId)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("指定应用采集需要 Windows 11 或 Windows build 20348 及以上。");
        if (processId <= 0 || processId == Platform.ProcessId) throw new ArgumentException("请选择其他程序进行采集。", nameof(processId));
        _processId = processId;
        _thread = new Thread(Capture) { IsBackground = true, Name = "MicBridge process loopback" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _cancellationToken = _cancellation.Token;
        _stopRegistration = _cancellationToken.Register(() => _stop.Set());
    }

    public Task StartAsync() => StartAsync(CancellationToken.None);
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProcessLoopbackCapture));
            cancellationToken.ThrowIfCancellationRequested();
            if (_startedOnce) throw new InvalidOperationException("进程采集对象只能启动一次。");
            _startedOnce = true;
            _externalRegistration = cancellationToken.Register(() => _cancellation.Cancel());
            _thread.Start(); return _started.Task;
        }
    }

    private void Capture()
    {
        AudioClient? client = null;
        bool started = false;
        int comResult = -1;
        IntPtr multimediaTask = IntPtr.Zero;
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            comResult = CoInitializeEx(IntPtr.Zero, 0);
            Marshal.ThrowExceptionForHR(comResult);
            client = ActivationRequest.Activate(_processId, _cancellationToken);
            _cancellationToken.ThrowIfCancellationRequested();
            using var ready = new AutoResetEvent(false);
            // Use PCM as in Microsoft's sample; the mixer converts it to float.
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | (AudioClientStreamFlags)0x80000000,
                0, 0, Buffer.Provider.WaveFormat, Guid.Empty);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            _cancellationToken.ThrowIfCancellationRequested();
            uint taskIndex = 0;
            multimediaTask = AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);
            Volatile.Write(ref _multimediaScheduling, multimediaTask == IntPtr.Zero ? 0 : 1);
            client.Start();
            started = true;
            _started.TrySetResult(true);
            byte[] bytes = new byte[Math.Max(1, client.BufferSize) * Buffer.Provider.WaveFormat.BlockAlign];
            WaitHandle[] events = [_stop, ready];
            while (WaitHandle.WaitAny(events, 1000) != 0)
            {
                int frames;
                while ((frames = capture.GetNextPacketSize()) > 0)
                {
                    IntPtr data = capture.GetBuffer(out frames, out var flags, out _, out _);
                    try
                    {
                        int count = frames * Buffer.Provider.WaveFormat.BlockAlign;
                        if (bytes.Length < count) bytes = new byte[count];
                        if ((flags & AudioClientBufferFlags.Silent) != 0 || data == IntPtr.Zero) Array.Clear(bytes, 0, count);
                        else Marshal.Copy(data, bytes, 0, count);
                        Buffer.Write(bytes, count);
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
        }
        catch (OperationCanceledException) { _started.TrySetCanceled(); }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            if (started && !_stop.WaitOne(0)) Failed?.Invoke(ex);
        }
        finally
        {
            try
            {
                if (client is not null)
                {
                    if (started) { try { client.Stop(); } catch (COMException) { } }
                    client.Dispose();
                }
            }
            finally
            {
                // MMCSS registration belongs to this capture thread, including error exits.
                if (multimediaTask != IntPtr.Zero) AvRevertMmThreadCharacteristics(multimediaTask);
                Volatile.Write(ref _multimediaScheduling, 0);
                if (comResult >= 0) CoUninitialize();
            }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true; _cancellation.Cancel();
        }
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join();
        _externalRegistration.Dispose(); _stopRegistration.Dispose(); _cancellation.Dispose();
        _stop.Dispose();
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr task);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationRequest : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<AudioClient> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IntPtr _parameters;
        private IntPtr _variant;

        internal static AudioClient Activate(int processId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ActivationRequest();
            IActivateAudioInterfaceAsyncOperation? operation = null;
            try
            {
                var parameters = new ActivationParameters { Type = 1, ProcessId = (uint)processId, Mode = 0 };
                request._parameters = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParameters>());
                Marshal.StructureToPtr(parameters, request._parameters, false);
                var variant = new BlobVariant { Type = 65, Blob = new Blob { Size = (uint)Marshal.SizeOf<ActivationParameters>(), Data = request._parameters } };
                request._variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
                Marshal.StructureToPtr(variant, request._variant, false);
                Guid iid = typeof(IAudioClient).GUID;
                int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, request._variant, request, out operation);
                if (hr < 0) { request.FreeParameters(); Marshal.ThrowExceptionForHR(hr); }
                try { return Platform.WithTimeout(request._completion.Task, TimeSpan.FromSeconds(10), cancellationToken).GetAwaiter().GetResult(); }
                catch (Exception error) when (error is TimeoutException or OperationCanceledException)
                {
                    // Windows owns the callback until it completes. Keep its parameter storage alive;
                    // if it eventually succeeds, release the unused client on a worker thread.
                    _ = request._completion.Task.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
                    if (error is OperationCanceledException) throw;
                    throw new TimeoutException("Windows 应用声音采集初始化超时，请停止后重试。");
                }
            }
            finally
            {
                if (operation is not null) Marshal.ReleaseComObject(operation);
                GC.KeepAlive(request);
            }
        }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            object? activated = null;
            try
            {
                operation.GetActivateResult(out int hr, out activated);
                Marshal.ThrowExceptionForHR(hr);
                var client = new AudioClient((IAudioClient)activated);
                activated = null; // ownership transfers to AudioClient
                _completion.TrySetResult(client);
            }
            catch (Exception ex) { _completion.TrySetException(ex); }
            finally
            {
                if (activated is not null && Marshal.IsComObject(activated)) Marshal.ReleaseComObject(activated);
                FreeParameters();
            }
        }
        private void FreeParameters()
        {
            IntPtr variant = Interlocked.Exchange(ref _variant, IntPtr.Zero);
            IntPtr parameters = Interlocked.Exchange(ref _parameters, IntPtr.Zero);
            if (variant != IntPtr.Zero) Marshal.FreeHGlobal(variant);
            if (parameters != IntPtr.Zero) Marshal.FreeHGlobal(parameters);
        }

        [StructLayout(LayoutKind.Sequential)] private struct ActivationParameters { public int Type; public uint ProcessId; public int Mode; }
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public IntPtr Data; }
        [StructLayout(LayoutKind.Sequential)] private struct BlobVariant { public ushort Type, Reserved1, Reserved2, Reserved3; public Blob Blob; }
        [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, IntPtr parameters,
            IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
}
