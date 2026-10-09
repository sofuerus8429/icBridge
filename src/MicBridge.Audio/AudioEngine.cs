using System.Runtime.InteropServices;
using MicBridge.Audio.Interop;
using MicBridge.Audio.Mixing;
using MicBridge.Audio.Sources;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridge.Audio;

public enum ApplicationCaptureMode { None, Process, DeviceLoopback }
public sealed record RoutingOptions(string OutputDeviceId, string? MicrophoneDeviceId = null,
    ApplicationCaptureMode CaptureMode = ApplicationCaptureMode.None, int ProcessId = 0,
    string? LoopbackDeviceId = null, string? MonitorDeviceId = null, int LatencyMs = 60);

public sealed class AudioEngine : IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<MMDevice> _devices = [];
    private readonly List<WasapiCapture> _captures = [];
    private WasapiOut? _output, _monitor;
    private ProcessLoopbackCapture? _processCapture;
    private WasapiCapture? _microphoneCapture;
    private MMDevice? _microphoneDevice;
    private string? _microphoneDeviceId;
    private CaptureBuffer? _monitorBuffer;
    private PeakLimiter? _limiter;
    private int _running, _disposed;
    private float _voiceBalance = 50;
    public MixerChannel Music { get; } = new();
    public MixerChannel Application { get; } = new();
    public MixerChannel Microphone { get; } = new();
    public MixerChannel Master { get; } = new();
    public MicrophoneProcessingOptions MicrophoneProcessing { get; } = new();
    public FilePlaybackSource Player { get; } = new();
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public bool IsMicrophoneCapturing => Volatile.Read(ref _microphoneCapture) is not null;
    public bool ProcessCaptureUsesMultimediaScheduling => _processCapture?.UsesMultimediaScheduling == true;
    public float OutputPeak => _limiter?.Peak ?? 0;
    public float LimiterReductionDb => _limiter?.ReductionDb ?? 0;
    public float MonitorVolume { get; set; } = 0.7f;
    public float VoiceBalancePercent
    {
        get => Volatile.Read(ref _voiceBalance);
        set
        {
            float position = Platform.IsFinite(value) ? Platform.Clamp(value, 0, 100) : 50;
            Volatile.Write(ref _voiceBalance, position);
            // The center retains both channel levels; each half fades out the opposing source.
            Music.MixWeight = Application.MixWeight = Math.Min(1, (100 - position) / 50);
            Microphone.MixWeight = Math.Min(1, position / 50);
        }
    }
    public event Action<Exception>? Failed;

    public AudioEngine() { Music.SetSource(Player); Microphone.EnabledChanged += OnMicrophoneEnabledChanged; }

    public Task StartAsync(RoutingOptions options) => StartAsync(options, CancellationToken.None);
    public async Task StartAsync(RoutingOptions options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _lifecycle.WaitAsync(startup.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning) throw new InvalidOperationException("混音已经启动。");
            ValidateRouting(options);
            await Task.Run(async () =>
            {
                try
                {
                    startup.Token.ThrowIfCancellationRequested();
                    using var enumerator = new MMDeviceEnumerator();
                    var outputDevice = OpenDevice(enumerator, options.OutputDeviceId);
                    var mixer = new MixingSampleProvider(AudioFormat.MixFormat) { ReadFully = true };
                    mixer.AddMixerInput(Music);
                    mixer.AddMixerInput(Application);
                    mixer.AddMixerInput(Microphone);
                    Master.SetSource(mixer);
                    _limiter = new PeakLimiter(Master);
                    var tap = new MonitorTap(this, _limiter);
                    _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, Platform.Clamp(options.LatencyMs, 20, 200));
                    _output.PlaybackStopped += (_, e) => { if (IsRunning) Failed?.Invoke(e.Exception ?? new IOException("输出设备已停止。")); };
                    _output.Init(tap.ToWaveProvider());

                    startup.Token.ThrowIfCancellationRequested();
                    _microphoneDeviceId = options.MicrophoneDeviceId;
                    if (Microphone.Enabled && !string.IsNullOrEmpty(_microphoneDeviceId))
                        OpenMicrophone(enumerator);
                    if (options.CaptureMode == ApplicationCaptureMode.Process)
                    {
                        _processCapture = new ProcessLoopbackCapture(options.ProcessId);
                        _processCapture.Failed += ex => Failed?.Invoke(ex);
                        Application.SetSource(_processCapture.Buffer.Provider.ToSampleProvider());
                        await _processCapture.StartAsync(startup.Token).ConfigureAwait(false);
                    }
                    else if (options.CaptureMode == ApplicationCaptureMode.DeviceLoopback)
                        AddCapture(new WasapiLoopbackCapture(OpenDevice(enumerator, options.LoopbackDeviceId!)), Application);

                    if (!string.IsNullOrEmpty(options.MonitorDeviceId))
                    {
                        _monitorBuffer = new CaptureBuffer(AudioFormat.MixFormat);
                        _monitor = new WasapiOut(OpenDevice(enumerator, options.MonitorDeviceId!), AudioClientShareMode.Shared, false, 80);
                        _monitor.PlaybackStopped += (_, e) => { if (IsRunning) Failed?.Invoke(e.Exception ?? new IOException("监听设备已停止。")); };
                        _monitor.Init(new MonitorGain(this, _monitorBuffer.Provider.ToSampleProvider()).ToWaveProvider());
                    }
                    startup.Token.ThrowIfCancellationRequested();
                    foreach (var capture in _captures) capture.StartRecording();
                    Volatile.Write(ref _running, 1);
                    _output.Play();
                    _monitor?.Play();
                }
                catch { Cleanup(); throw; }
            }).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public static void ValidateRouting(RoutingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDeviceId)) throw new ArgumentException("请选择虚拟输出设备。");
        if (options.OutputDeviceId == options.MonitorDeviceId) throw new ArgumentException("监听设备与虚拟输出设备不能相同。");
        if (options.CaptureMode == ApplicationCaptureMode.Process && (options.ProcessId <= 0 || options.ProcessId == Platform.ProcessId))
            throw new ArgumentException("请选择需要采集的程序。");
        if (options.CaptureMode == ApplicationCaptureMode.DeviceLoopback)
        {
            if (string.IsNullOrWhiteSpace(options.LoopbackDeviceId)) throw new ArgumentException("请选择要采集声音的播放设备。");
            if (options.LoopbackDeviceId == options.OutputDeviceId || options.LoopbackDeviceId == options.MonitorDeviceId)
                throw new ArgumentException("不能采集混音输出或监听设备，否则会产生声音回路。请改用指定应用采集。");
        }
    }

    private MMDevice OpenDevice(MMDeviceEnumerator enumerator, string id)
    {
        var device = enumerator.GetDevice(id);
        _devices.Add(device);
        return device;
    }
    private void AddCapture(WasapiCapture capture, MixerChannel channel, bool processMicrophone = false)
    {
        _captures.Add(capture);
        var buffer = new CaptureBuffer(capture.WaveFormat);
        var source = AudioFormat.Normalize(buffer.Provider.ToSampleProvider());
        channel.SetSource(processMicrophone ? new MicrophoneProcessor(source, MicrophoneProcessing) : source);
        capture.DataAvailable += (_, e) => buffer.Write(e.Buffer, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => { if (IsRunning && (!processMicrophone || ReferenceEquals(_microphoneCapture, capture))) Failed?.Invoke(e.Exception ?? new IOException("采集设备已停止。")); };
    }
    private void OpenMicrophone(MMDeviceEnumerator enumerator)
    {
        _microphoneDevice = enumerator.GetDevice(_microphoneDeviceId!);
        _microphoneCapture = new WasapiCapture(_microphoneDevice);
        AddCapture(_microphoneCapture, Microphone, processMicrophone: true);
    }
    private void OnMicrophoneEnabledChanged(bool enabled) => _ = UpdateMicrophoneCaptureAsync();
    private async Task UpdateMicrophoneCaptureAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !IsRunning || string.IsNullOrEmpty(_microphoneDeviceId)) return;
            await Task.Run(() =>
            {
                if (Microphone.Enabled && _microphoneCapture is null)
                {
                    using var enumerator = new MMDeviceEnumerator(); OpenMicrophone(enumerator);
                    _microphoneCapture!.StartRecording();
                }
                else if (!Microphone.Enabled && _microphoneCapture is not null)
                {
                    var capture = _microphoneCapture; _microphoneCapture = null;
                    Microphone.SetSource(null); _captures.Remove(capture);
                    try { capture.StopRecording(); } finally { capture.Dispose(); _microphoneDevice?.Dispose(); _microphoneDevice = null; }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error) { Failed?.Invoke(error); }
        finally { _lifecycle.Release(); }
    }
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (Volatile.Read(ref _disposed) == 0) await Task.Run(Cleanup).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
    private void Cleanup()
    {
        Volatile.Write(ref _running, 0);
        Player.Pause();
        _output?.Dispose(); _output = null;
        _monitor?.Dispose(); _monitor = null;
        foreach (var capture in _captures) { try { capture.StopRecording(); } catch (COMException) { } capture.Dispose(); }
        _captures.Clear();
        _microphoneCapture = null; _microphoneDevice?.Dispose(); _microphoneDevice = null; _microphoneDeviceId = null;
        _processCapture?.Dispose(); _processCapture = null;
        Application.SetSource(null); Microphone.SetSource(null); Master.SetSource(null);
        foreach (var device in _devices) device.Dispose();
        _devices.Clear(); _monitorBuffer = null; _limiter = null;
    }
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(AudioEngine)); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel(); Microphone.EnabledChanged -= OnMicrophoneEnabledChanged; _lifecycle.Wait();
        try { Cleanup(); Player.Dispose(); } finally { _shutdown.Dispose(); _lifecycle.Release(); }
    }

    private sealed class MonitorTap(AudioEngine engine, ISampleProvider source) : ISampleProvider
    {
        private byte[] _bytes = [];
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (engine._monitorBuffer is not null)
            {
                if (_bytes.Length < read * 4) _bytes = new byte[read * 4];
                System.Buffer.BlockCopy(buffer, offset * 4, _bytes, 0, read * 4);
                engine._monitorBuffer.Write(_bytes, read * 4);
            }
            return read;
        }
    }
    private sealed class MonitorGain(AudioEngine engine, ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            float volume = Platform.Clamp(engine.MonitorVolume, 0, 1);
            for (int i = 0; i < read; i++) buffer[offset + i] *= volume;
            return read;
        }
    }
}
