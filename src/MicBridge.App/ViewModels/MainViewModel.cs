using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using MicBridge.App.Infrastructure;
using MicBridge.App.Services;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using MicBridge.Audio.Interop;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace MicBridge.App.ViewModels;

public sealed record TrackItem(string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _deviceChangeTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly AudioDeviceNotifications _deviceNotifications;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly HardwareRouteCache _hardwareRouteCache = new();
    private CancellationTokenSource? _hardwareDetectionCancellation;
    private readonly AppSettings _settings;
    private readonly bool _saveSettings;
    private bool _suppressSettingsSave;
    private AudioDeviceInfo[] _allOutputs = [];
    private bool _showOtherOutputDevices, _hardwareLoopbackMode;
    private bool _initialDeviceRefresh = true;
    private string _cableReminder = "游戏麦克风选 CABLE Output，请勿禁用它。";
    private AudioDeviceInfo? _hardwareCaptureDevice;
    private string? _hardwareVerifiedOutputDeviceId;
    private long _lastPositionSecond = -1, _lastDurationSecond = -1;
    private string _lastPlaybackStatus = "", _lastSourcePlayText = "";
    private AudioDeviceInfo? _outputDevice, _microphoneDevice, _monitorDevice, _loopbackDevice;
    private ApplicationInfo? _selectedApplication;
    private TrackItem? _selectedTrack;
    private bool _isRunning, _isBusy, _monitorEnabled, _error, _wasPlaying, _disposed;
    private int _captureMode, _latency = 60, _sourceKind;
    private double _monitorVolume = 70, _seekSeconds;
    private string _status = "准备就绪。选择虚拟输出设备，然后启动混音。", _currentTrack = "添加一首音乐，开始你的声音现场";
    private string _transportText = "播放", _connectionText = "待连接", _timeText = "00:00 / 00:00", _limiterText = "峰值保护待命";
    private double _duration, _outputMeter, _sourceMeter;
    public ChannelViewModel Music { get; }
    public ChannelViewModel ApplicationChannel { get; }
    public ChannelViewModel Microphone { get; }
    public ChannelViewModel Master { get; }
    public ChannelViewModel SourceChannel => IsLocalSource ? Music : ApplicationChannel;
    public bool MicrophoneGateEnabled { get => _engine.MicrophoneProcessing.GateEnabled; set { _engine.MicrophoneProcessing.GateEnabled = value; Notify(); } }
    public bool MicrophoneHighPassEnabled { get => _engine.MicrophoneProcessing.HighPassEnabled; set { _engine.MicrophoneProcessing.HighPassEnabled = value; Notify(); } }
    public double MicrophoneGateThreshold { get => _engine.MicrophoneProcessing.ThresholdDb; set { _engine.MicrophoneProcessing.ThresholdDb = (float)value; Notify(); } }
    public string AudioFormatText => "48 kHz · 32-bit float · 立体声（来源自动重采样）";
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> HardwareCaptureDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> MicrophoneDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> MonitorDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> LoopbackDevices { get; } = [];
    public ObservableCollection<ApplicationInfo> Applications { get; } = [];
    public ObservableCollection<TrackItem> Playlist { get; } = [];
    public string[] CaptureModes { get; } = ["关闭程序声音采集", "指定进程（含子进程）", "整个播放设备（兼容模式）"];
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) { if (value) _timer.Start(); else { _timer.Stop(); Tick(); } Notify(nameof(IsStopped)); Notify(nameof(StartText)); Notify(nameof(SourcePlayText)); Notify(nameof(PlaybackStatus)); } } }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { Notify(nameof(IsStopped)); Notify(nameof(StartText)); Notify(nameof(CanChangeSource)); Notify(nameof(SourcePlayText)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsStopped => !IsRunning && !IsBusy;
    public bool HasError { get => _error; private set => Set(ref _error, value); }
    public string StartText => IsBusy ? "正在连接…" : IsRunning ? "停止混音" : "启动混音";
    public string VersionText => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} · OPEN SOURCE";
    public int SourceKind => _sourceKind;
    public bool IsLocalSource => SourceKind == 0;
    public bool IsApplicationSource => SourceKind == 1;
    public bool CanChangeSource => !IsBusy;
    public string SourceTitle => IsLocalSource ? SelectedTrack?.Name ?? "选择一首本地音乐" :
        IsDeviceMode ? LoopbackDevice?.Name ?? "选择播放设备" : SelectedApplication is null ? "选择一个程序进程" : $"{SelectedApplication.Name}.exe";
    public string SourceSubtitle => IsLocalSource ? "本地文件" : IsDeviceMode ? "播放设备声音" : SelectedApplication?.WindowTitle is { Length: > 0 } title ? title : "进程声音";
    public string ChooseSourceText => IsLocalSource ? "选择文件" : "选择进程";
    public string SourcePlayText => IsBusy ? "连接中…" : IsLocalSource ? TransportText : IsRunning ? "暂停采集" : "开始采集";
    public string PlaybackStatus
    {
        get
        {
            if (Master.Muted) return "全部静音";
            if (!IsRunning) return IsLocalSource ? "准备播放" : "待采集";
            if (MixBalance >= 100) return Microphone.Muted || !Microphone.Enabled ? "人声已静音" : "只输出人声";
            var source = IsLocalSource ? Music : ApplicationChannel;
            if (!source.Enabled) return "音源未加入混音";
            if (source.Muted) return "音源已静音";
            return IsLocalSource ? _engine.Player.IsPlaying ? "正在播放" : "音乐已暂停" : SourceMeter > 1 ? "正在采集" : "等待进程发声";
        }
    }
    public double SourceMeter { get => _sourceMeter; private set => Set(ref _sourceMeter, value); }
    public double MixBalance
    {
        get => _engine.VoiceBalancePercent;
        set
        {
            _engine.VoiceBalancePercent = Platform.IsFinite(value) ? (float)value : 50;
            Notify(); Notify(nameof(MixBalanceText));
        }
    }
    public string MixBalanceText => MixBalance <= 0 ? "只放音乐" : MixBalance >= 100 ? "只放人声" :
        Math.Abs(MixBalance - 50) < 0.5 ? "音乐 + 人声" : MixBalance < 50 ? "音乐更多" : "人声更多";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string CurrentTrack { get => _currentTrack; private set => Set(ref _currentTrack, value); }
    public string TransportText { get => _transportText; private set => Set(ref _transportText, value); }
    public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }
    public string LimiterText { get => _limiterText; private set => Set(ref _limiterText, value); }
    public double OutputMeter { get => _outputMeter; private set => Set(ref _outputMeter, value); }
    public double Duration { get => _duration; private set { Set(ref _duration, value); Notify(nameof(SeekMaximum)); Notify(nameof(HasTrack)); } }
    public double SeekMaximum => Math.Max(1, Duration);
    public bool HasTrack => _engine.Player.FilePath is not null;
    public double SeekSeconds { get => _seekSeconds; set { if (Set(ref _seekSeconds, value)) _engine.Player.Seek(TimeSpan.FromSeconds(value)); } }
    public bool IsSeeking { get; set; }
    public bool Loop { get => _engine.Player.Loop; set { _engine.Player.Loop = value; Notify(); } }
    public TrackItem? SelectedTrack { get => _selectedTrack; set { if (Set(ref _selectedTrack, value)) { Notify(nameof(SourceTitle)); CommandManager.InvalidateRequerySuggested(); } } }
    public AudioDeviceInfo? OutputDevice { get => _outputDevice; set { Set(ref _outputDevice, value); Notify(nameof(OutputHint)); } }
    public AudioDeviceInfo? MicrophoneDevice { get => _microphoneDevice; set => Set(ref _microphoneDevice, value); }
    public AudioDeviceInfo? MonitorDevice { get => _monitorDevice; set => Set(ref _monitorDevice, value); }
    public AudioDeviceInfo? LoopbackDevice { get => _loopbackDevice; set { Set(ref _loopbackDevice, value); Notify(nameof(SourceTitle)); } }
    public ApplicationInfo? SelectedApplication { get => _selectedApplication; set { if (Set(ref _selectedApplication, value)) { Notify(nameof(SelectedProcessText)); Notify(nameof(SourceTitle)); Notify(nameof(SourceSubtitle)); CommandManager.InvalidateRequerySuggested(); } } }
    public string SelectedProcessText => SelectedApplication?.ToString() ?? "尚未选择进程";
    public bool IsProcessCaptureSupported => ProcessLoopbackCapture.IsSupported;
    public string ProcessCaptureHint => IsProcessCaptureSupported ? "仅采集所选进程及其子进程。停止混音后可切换来源。" : "此系统不支持进程采集，请使用整个播放设备的兼容模式。";
    public int CaptureMode { get => _captureMode; set { Set(ref _captureMode, value); Notify(nameof(IsProcessMode)); Notify(nameof(IsDeviceMode)); Notify(nameof(SourceTitle)); Notify(nameof(SourceSubtitle)); } }
    public bool IsProcessMode => CaptureMode == 1;
    public bool IsDeviceMode => CaptureMode == 2;
    public int Latency { get => _latency; set => Set(ref _latency, Platform.Clamp(value, 20, 200)); }
    public bool MonitorEnabled { get => _monitorEnabled; set => Set(ref _monitorEnabled, value); }
    public double MonitorVolume { get => _monitorVolume; set { value = Platform.IsFinite(value) ? Platform.Clamp(value, 0, 100) : 70; Set(ref _monitorVolume, value); _engine.MonitorVolume = (float)(value / 100); } }
    public string OutputCaption => HardwareLoopbackMode ? "03   输出到立体声混音" : "03   输出到虚拟麦克风";
    public bool IsVirtualOutputMode => !HardwareLoopbackMode;
    public string OutputHint => HardwareLoopbackMode ? HardwareRouteHint : OutputDevice is null ? "未选择虚拟设备。安装驱动后点击刷新设备。" :
        OutputDevice.IsVirtual ? "游戏中选择对应的录音设备。VB-CABLE 对应 CABLE Output。" : "当前是普通播放设备，可用于试听；游戏中需要虚拟线缆对应的麦克风。";
    public string VirtualDeviceStatus { get; private set; } = "正在读取设备…";
    public string CableReminder => HardwareLoopbackMode ? HardwareCaptureDevice is null ? "高级设置中选择声卡的立体声混音。" :
        HardwareCaptureDevice.State != DeviceState.Active ? "立体声混音已禁用，请在 Windows 录音设备中启用。" : "游戏麦克风选立体声混音；该声卡上的其他声音也会传入。" : _cableReminder;
    public bool HasHardwareLoopbackDevice => HardwareCaptureDevices.Count > 0;
    public bool ShouldOfferDriverInstallation => !HasInstalledVirtualDriver && !HasHardwareLoopbackDevice;
    public bool HardwareLoopbackMode
    {
        get => _hardwareLoopbackMode;
        set
        {
            if (!IsStopped || !Set(ref _hardwareLoopbackMode, value)) return;
            UpdateOutputList(null); NotifyOutputRoute(); SaveSettings();
        }
    }
    public AudioDeviceInfo? HardwareCaptureDevice
    {
        get => _hardwareCaptureDevice;
        set { if (!Set(ref _hardwareCaptureDevice, value)) return; if (HardwareLoopbackMode) UpdateOutputList(OutputDevice?.Id); NotifyOutputRoute(); }
    }
    public string HardwareRouteHint => HardwareCaptureDevice is null ? "声卡未提供立体声混音；此电脑需要虚拟线缆。" :
        HardwareCaptureDevice.State != DeviceState.Active ? "已发现立体声混音，但处于禁用状态。点击‘打开录音设备’，右键显示已禁用的设备，启用后刷新。" :
        "免装额外驱动。输出选择同声卡的播放设备，游戏麦克风选择上方录音设备。具体扬声器/耳机是否可录回取决于声卡。";
    private void NotifyOutputRoute()
    {
        foreach (string name in new[] { nameof(OutputCaption), nameof(IsVirtualOutputMode), nameof(OutputHint), nameof(CableReminder), nameof(HardwareRouteHint), nameof(HasHardwareLoopbackDevice), nameof(ShouldOfferDriverInstallation) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public bool HasInstalledVirtualDriver { get; private set; }
    public bool HasVirtualMicrophone { get; private set; }
    public bool ShowOtherOutputDevices
    {
        get => _showOtherOutputDevices;
        set { if (!IsStopped || !Set(ref _showOtherOutputDevices, value)) return; UpdateOutputList(OutputDevice?.Id); }
    }
    internal bool DisplayTimerEnabled => _timer.IsEnabled;
    internal bool LocalSourcePlaying => _engine.Player.IsPlaying;
    internal TimeSpan LocalSourcePosition => _engine.Player.Position;
    internal void SetDisplayActive(bool active) => _timer.Interval = TimeSpan.FromMilliseconds(active ? 60 : 250);
    public ICommand StartCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SelectProcessCommand { get; }
    public ICommand ChooseLocalCommand { get; }
    public ICommand ChooseApplicationCommand { get; }
    public ICommand ChooseSourceCommand { get; }
    public ICommand SourcePlayCommand { get; }
    public ICommand SourceStopCommand { get; }
    public ICommand AdvancedSettingsCommand { get; }
    public ICommand AddFilesCommand { get; }
    public ICommand RemoveTrackCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopTrackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand MuteAllCommand { get; }
    public ICommand DriverGuideCommand { get; }
    public ICommand InstallDriverCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenAudioSettingsCommand { get; }
    public ICommand OpenRecordingDevicesCommand { get; }
    public ICommand DetectHardwareOutputCommand { get; }
    public ICommand CancelHardwareDetectionCommand { get; }
    public bool IsDetectingHardware => _hardwareDetectionCancellation is not null;
    internal int HardwareDetectionCount { get; private set; }

    public MainViewModel(bool loadSettings = true)
    {
        _saveSettings = loadSettings;
        _settings = loadSettings ? SettingsStore.Load() : new AppSettings();
        _hardwareLoopbackMode = _settings.HardwareLoopbackMode;
        _hardwareVerifiedOutputDeviceId = _settings.HardwareVerifiedOutputDeviceId;
        Music = new(_engine.Music); ApplicationChannel = new(_engine.Application); Microphone = new(_engine.Microphone); Master = new(_engine.Master);
        RestoreChannel(Music, _settings.Music); RestoreChannel(ApplicationChannel, _settings.Application);
        RestoreChannel(Microphone, _settings.Microphone); RestoreChannel(Master, _settings.Master);
        CaptureMode = Platform.Clamp(_settings.CaptureMode, 0, 2); Latency = _settings.LatencyMs;
        _sourceKind = Platform.Clamp(_settings.SourceKind ?? (CaptureMode == 0 ? 0 : 1), 0, 1);
        MixBalance = _settings.MixBalance;
        MonitorEnabled = _settings.MonitorEnabled; MonitorVolume = _settings.MonitorVolume; Loop = _settings.Loop;
        MicrophoneGateEnabled = _settings.MicrophoneGateEnabled; MicrophoneHighPassEnabled = _settings.MicrophoneHighPassEnabled;
        MicrophoneGateThreshold = _settings.MicrophoneGateThreshold;
        foreach (string path in _settings.Playlist.Where(File.Exists)) Playlist.Add(new(path));
        SelectedTrack = Playlist.FirstOrDefault();
        StartCommand = new AsyncCommand(ToggleRoutingAsync, ReportError, () => !IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, ReportError, () => IsStopped);
        SelectProcessCommand = new AsyncCommand(SelectProcessAsync, ReportError, () => !IsBusy && IsProcessCaptureSupported);
        ChooseLocalCommand = new AsyncCommand(() => ChangeSourceKindAsync(0), ReportError, () => !IsBusy);
        ChooseApplicationCommand = new AsyncCommand(() => ChangeSourceKindAsync(1), ReportError, () => !IsBusy);
        ChooseSourceCommand = new AsyncCommand(ChooseSourceAsync, ReportError, () => !IsBusy);
        SourcePlayCommand = new AsyncCommand(ToggleSourcePlaybackAsync, ReportError,
            () => !IsBusy && (IsLocalSource ? Playlist.Count > 0 : IsRunning || CaptureMode == 2 && LoopbackDevice is not null || SelectedApplication is not null));
        SourceStopCommand = new AsyncCommand(StopSourceAsync, ReportError, () => !IsBusy && (IsRunning || IsLocalSource && HasTrack));
        AdvancedSettingsCommand = new RelayCommand(OpenAdvancedSettings);
        AddFilesCommand = new RelayCommand(AddFiles);
        RemoveTrackCommand = new RelayCommand(RemoveTrack, () => SelectedTrack is not null);
        PlayCommand = new AsyncCommand(TogglePlaybackAsync, ReportError, () => Playlist.Count > 0 && !IsBusy);
        StopTrackCommand = new RelayCommand(() => { _engine.Player.Stop(); _wasPlaying = false; Tick(); });
        NextCommand = new AsyncCommand(() => MoveTrackAsync(1), ReportError, () => Playlist.Count > 0 && !IsBusy);
        PreviousCommand = new AsyncCommand(() => MoveTrackAsync(-1), ReportError, () => Playlist.Count > 0 && !IsBusy);
        MuteAllCommand = new RelayCommand(() => { Master.Muted = !Master.Muted; SetStatus(Master.Muted ? "全部静音已开启。" : "全部静音已关闭。"); Notify(nameof(PlaybackStatus)); });
        DriverGuideCommand = new RelayCommand(() => OpenUri("https://vb-audio.com/Cable/"));
        InstallDriverCommand = new RelayCommand(OpenDriverSetup, () => IsStopped && !HasInstalledVirtualDriver);
        UninstallCommand = new RelayCommand(OpenUninstall, () => IsStopped);
        OpenAudioSettingsCommand = new RelayCommand(() => OpenUri("ms-settings:sound"));
        OpenRecordingDevicesCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,1") { UseShellExecute = true }));
        DetectHardwareOutputCommand = new AsyncCommand(DetectHardwareOutputAsync, ReportError, () => IsStopped && HardwareLoopbackMode && HardwareCaptureDevice?.State == DeviceState.Active);
        CancelHardwareDetectionCommand = new RelayCommand(() => _hardwareDetectionCancellation?.Cancel(), () => IsDetectingHardware);
        _engine.Failed += OnEngineFailed;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Tick();
        _deviceChangeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _deviceChangeTimer.Tick += async (_, _) =>
        {
            _deviceChangeTimer.Stop(); if (_disposed) return;
            if (IsBusy) { _deviceChangeTimer.Start(); return; }
            try { if (IsStopped) await RefreshAsync(); else await CheckRunningDevicesAsync(); }
            catch (Exception error) { ReportError(error); }
        };
        _deviceNotifications = new AudioDeviceNotifications();
        _deviceNotifications.Changed += OnDevicesChanged;
    }
    private void OnDevicesChanged()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _hardwareRouteCache.Invalidate();
        _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) { _deviceChangeTimer.Stop(); _deviceChangeTimer.Start(); } }), DispatcherPriority.Background);
    }
    private async Task CheckRunningDevicesAsync()
    {
        var data = await Task.Run(() => (Render: DeviceCatalog.GetDevices(DataFlow.Render, DeviceState.Active | DeviceState.Disabled), Capture: DeviceCatalog.GetDevices(DataFlow.Capture, DeviceState.Active | DeviceState.Disabled), DefaultRender: DeviceCatalog.GetDefaultDevice(DataFlow.Render)));
        if (_disposed || !IsRunning || IsBusy) return;
        _cableReminder = DeviceCatalog.CableReminder(data.Render, data.Capture, data.DefaultRender);
        if (HardwareCaptureDevice is not null) _hardwareCaptureDevice = data.Capture.FirstOrDefault(d => d.Id == HardwareCaptureDevice.Id) ?? HardwareCaptureDevice with { State = DeviceState.NotPresent };
        NotifyOutputRoute();
        bool captureMissing = HardwareLoopbackMode ? HardwareCaptureDevice?.State != DeviceState.Active : OutputDevice is not null && DeviceCatalog.IsStandardCableInput(OutputDevice) &&
            !data.Capture.Any(d => DeviceCatalog.IsBundledCable(d) && d.Name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase) && d.State == DeviceState.Active);
        if (captureMissing || !data.Render.Any(d => d.Id == OutputDevice?.Id && d.State == DeviceState.Active))
        {
            await StopRoutingIfRunningAsync(); await RefreshAsync();
            HasError = true; Status = captureMissing ? CableReminder : "输出设备不可用，混音已停止。请重新选择设备。";
        }
    }
    private async Task DetectHardwareOutputAsync()
    {
        if (HardwareCaptureDevice is null) return;
        IsBusy = true;
        try
        {
            var output = await VerifyHardwareOutputAsync(HardwareCaptureDevice, preferSelected: false);
            SetStatus($"录回检测通过，已选择 {output.Name}。游戏麦克风请选择 {HardwareCaptureDevice.Name}。"); SaveSettings();
        }
        catch (OperationCanceledException) when (!_disposed) { SetStatus("录回检测已取消。"); }
        finally { if (!_disposed) IsBusy = false; }
    }
    private async Task<AudioDeviceInfo> VerifyHardwareOutputAsync(AudioDeviceInfo capture, bool preferSelected)
    {
        var selected = OutputDevice;
        var candidates = OutputDevices.OrderByDescending(d => preferSelected && d.Id == selected?.Id).ToArray();
        int revision = _hardwareRouteCache.Revision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _hardwareDetectionCancellation = cancellation;
        Notify(nameof(IsDetectingHardware)); CommandManager.InvalidateRequerySuggested();
        try
        {
            SetStatus("正在检测录回路径，会播放短暂的低音量提示音…"); HardwareDetectionCount++;
            var output = await Task.Run(() => HardwareLoopbackProbe.FindOutputAsync(capture, candidates, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (output is null) throw new InvalidOperationException("未检测到可录回的输出。请检查声卡输出音量、立体声混音音量，或改用虚拟线缆模式。");
            if (!_hardwareRouteCache.Confirm(capture.Id, output.Id, revision))
                throw new InvalidOperationException("检测期间音频设备发生变化，请等待设备刷新后重新播放。");
            _hardwareVerifiedOutputDeviceId = output.Id; OutputDevice = OutputDevices.First(d => d.Id == output.Id);
            return output;
        }
        finally
        {
            _hardwareDetectionCancellation = null;
            if (!_disposed) { Notify(nameof(IsDetectingHardware)); CommandManager.InvalidateRequerySuggested(); }
        }
    }
    public async Task RefreshAsync()
    {
        if (!IsStopped) return;
        IsBusy = true;
        string? output = OutputDevice?.Id ?? _settings.OutputDeviceId, mic = MicrophoneDevice?.Id ?? _settings.MicrophoneDeviceId;
        string? monitor = MonitorDevice?.Id ?? _settings.MonitorDeviceId, loopback = LoopbackDevice?.Id ?? _settings.LoopbackDeviceId;
        var previousApplication = SelectedApplication;
        try
        {
            var data = await Task.Run(() => (Render: DeviceCatalog.GetDevices(DataFlow.Render, DeviceState.Active | DeviceState.Disabled), Capture: DeviceCatalog.GetDevices(DataFlow.Capture, DeviceState.Active | DeviceState.Disabled), DefaultRender: DeviceCatalog.GetDefaultDevice(DataFlow.Render)));
            _allOutputs = data.Render.Where(d => d.State == DeviceState.Active).ToArray();
            string? hardware = HardwareCaptureDevice?.Id ?? _settings.HardwareCaptureDeviceId;
            Replace(HardwareCaptureDevices, data.Capture.Where(DeviceCatalog.IsHardwareLoopback));
            HardwareCaptureDevice = HardwareCaptureDevices.FirstOrDefault(d => d.Id == hardware) ?? HardwareCaptureDevices.FirstOrDefault(d => d.State == DeviceState.Active) ?? HardwareCaptureDevices.FirstOrDefault();
            if (_initialDeviceRefresh && !data.Render.Any(d => d.IsVirtual) && _settings.OutputDeviceId is null && HardwareCaptureDevice?.State == DeviceState.Active) _hardwareLoopbackMode = true;
            _initialDeviceRefresh = false;
            UpdateOutputList(output);
            Replace(MicrophoneDevices, data.Capture.Where(d => d.State == DeviceState.Active && !d.IsVirtual && !DeviceCatalog.IsHardwareLoopback(d)));
            Replace(MonitorDevices, _allOutputs.Where(d => !d.IsVirtual)); Replace(LoopbackDevices, _allOutputs);
            MicrophoneDevice = MicrophoneDevices.FirstOrDefault(d => d.Id == mic) ?? MicrophoneDevices.FirstOrDefault(d => !d.IsVirtual);
            MonitorDevice = MonitorDevices.FirstOrDefault(d => d.Id == monitor) ?? MonitorDevices.FirstOrDefault();
            LoopbackDevice = LoopbackDevices.FirstOrDefault(d => d.Id == loopback) ?? LoopbackDevices.FirstOrDefault(d => !d.IsVirtual);
            if (previousApplication is not null)
            {
                var current = DeviceCatalog.GetApplication(previousApplication.ProcessId);
                SelectedApplication = current is not null && (previousApplication.StartTimeUtcTicks is null || current.StartTimeUtcTicks == previousApplication.StartTimeUtcTicks) ? current : null;
            }
            HasInstalledVirtualDriver = data.Render.Any(d => d.IsVirtual) || data.Capture.Any(d => d.IsVirtual);
            HasVirtualMicrophone = _allOutputs.Any(d => d.IsVirtual) && data.Capture.Any(d => d.IsVirtual && d.State == DeviceState.Active);
            _cableReminder = DeviceCatalog.CableReminder(data.Render, data.Capture, data.DefaultRender); NotifyOutputRoute(); Notify(nameof(HardwareLoopbackMode)); Notify(nameof(HasInstalledVirtualDriver));
            Notify(nameof(HasVirtualMicrophone));
            VirtualDeviceStatus = HasVirtualMicrophone ? "已发现虚拟音频设备" : HasInstalledVirtualDriver ? CableReminder : "尚未检测到虚拟麦克风，可使用内置驱动安装"; Notify(nameof(VirtualDeviceStatus));
            SetStatus(HardwareLoopbackMode ? HardwareRouteHint : HasInstalledVirtualDriver && !HasVirtualMicrophone ? CableReminder : OutputDevice is not null ? "选择音频来源，点击播放或启动混音。" : HasHardwareLoopbackDevice ? "高级设置可启用声卡立体声混音模式，免装额外驱动。" : "首次使用：在右上选择输出设备。");
        }
        finally { IsBusy = false; }
    }
    private void UpdateOutputList(string? selectedId)
    {
        var preferred = _allOutputs.Where(DeviceCatalog.IsStandardCableInput).ToArray();
        var devices = HardwareLoopbackMode ? _allOutputs.Where(d => !d.IsVirtual && HardwareCaptureDevice is not null && DeviceCatalog.IsSameAdapter(d, HardwareCaptureDevice)).ToArray() :
            ShowOtherOutputDevices ? _allOutputs : preferred.Length > 0 ? preferred : _allOutputs.Where(d => d.IsVirtual && !DeviceCatalog.IsBundledCable(d)).ToArray();
        Replace(OutputDevices, devices);
        OutputDevice = OutputDevices.FirstOrDefault(d => d.Id == selectedId) ?? (HardwareLoopbackMode ? OutputDevices.FirstOrDefault(d => d.Id == _hardwareVerifiedOutputDeviceId)
            ?? OutputDevices.FirstOrDefault(d => d.Name.Contains("扬声器") || d.Name.Contains("Speakers", StringComparison.OrdinalIgnoreCase)) : null) ?? OutputDevices.FirstOrDefault();
    }
    private async Task SelectProcessAsync()
    {
        var picker = new ProcessPickerWindow(SelectedApplication?.ProcessId) { Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(window => window.IsActive) ?? System.Windows.Application.Current.MainWindow };
        if (picker.ShowDialog() != true || picker.SelectedProcess is null) return;
        await StopRoutingIfRunningAsync(); UseProcessSource(picker.SelectedProcess); SaveSettings();
    }
    private void OpenAdvancedSettings()
    {
        var settings = new AdvancedSettingsWindow(this) { Owner = System.Windows.Application.Current.MainWindow };
        settings.ShowDialog(); SaveSettings();
    }
    internal void OpenDriverSetup()
    {
        if (!IsStopped) return;
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(window => window.IsActive)
            ?? System.Windows.Application.Current.MainWindow;
        new DriverSetupWindow(this) { Owner = owner }.ShowDialog();
    }
    private void OpenUninstall()
    {
        if (!IsStopped) return;
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(window => window.IsActive)
            ?? System.Windows.Application.Current.MainWindow;
        new UninstallWindow(this) { Owner = owner }.ShowDialog();
    }
    internal void SuppressSettingsSave() => _suppressSettingsSave = true;
    private async Task ChooseSourceAsync()
    {
        if (!IsLocalSource) { await SelectProcessAsync(); return; }
        var dialog = new OpenFileDialog { Title = "选择本地音乐", Filter = "音频文件|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.wma;*.aiff;*.aif|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        await UseLocalFileAsync(dialog.FileName);
    }
    public async Task UseLocalFileAsync(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到这首音乐。", path);
        await StopRoutingIfRunningAsync();
        AddPaths([path]); SelectedTrack = Playlist.First(track => string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase));
        ApplySourceKind(0); LoadTrack(SelectedTrack); Tick(); SaveSettings(); SetStatus("音乐已选择，点击播放。");
    }
    public async Task ChangeSourceKindAsync(int kind)
    {
        kind = Platform.Clamp(kind, 0, 1);
        if (kind == SourceKind || IsBusy) return;
        await StopRoutingIfRunningAsync(); ApplySourceKind(kind); SaveSettings();
        SetStatus(IsLocalSource ? "选择本地文件后播放。" : "选择进程后启动混音。");
    }
    private void ApplySourceKind(int kind)
    {
        _sourceKind = kind; Music.Enabled = kind == 0; ApplicationChannel.Enabled = kind == 1;
        CaptureMode = kind == 0 ? 0 : CaptureMode == 2 ? 2 : 1;
        foreach (string name in new[] { nameof(SourceKind), nameof(IsLocalSource), nameof(IsApplicationSource), nameof(SourceTitle), nameof(SourceSubtitle), nameof(ChooseSourceText), nameof(SourcePlayText), nameof(PlaybackStatus), nameof(SourceChannel) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private async Task StopRoutingIfRunningAsync()
    {
        if (IsRunning) await ToggleRoutingAsync();
    }
    public Task ToggleSourcePlaybackAsync() => IsLocalSource ? TogglePlaybackAsync() : ToggleRoutingAsync();
    public async Task StopSourceAsync()
    {
        await StopRoutingIfRunningAsync(); _engine.Player.Stop(); _wasPlaying = false; Tick(); SetStatus("播放已停止。");
    }
    public void UseProcessSource(ApplicationInfo process)
    {
        if (!IsStopped) throw new InvalidOperationException("请先停止混音，再切换进程声音源。");
        var current = DeviceCatalog.GetApplication(process.ProcessId);
        if (!process.CanCapture || current is null || process.StartTimeUtcTicks is long start && current.StartTimeUtcTicks != start)
            throw new InvalidOperationException("这个进程已退出或重启。请重新选择进程声音源。");
        SelectedApplication = process;
        ApplySourceKind(1); CaptureMode = 1;
        SetStatus($"已选择 {process.Name}，点击启动混音。");
    }
    private async Task StartRoutingAsync()
    {
        if (OutputDevice is null) throw new InvalidOperationException("请在右上选择输出设备。");
        if (HardwareLoopbackMode)
        {
            var currentCapture = DeviceCatalog.GetDevices(DataFlow.Capture, DeviceState.Active | DeviceState.Disabled).FirstOrDefault(d => d.Id == HardwareCaptureDevice?.Id);
            DeviceCatalog.ValidateHardwareRoute(currentCapture, OutputDevice, MonitorEnabled ? MonitorDevice : null);
        }
        else if (DeviceCatalog.IsStandardCableInput(OutputDevice))
        {
            var captures = DeviceCatalog.GetDevices(DataFlow.Capture, DeviceState.Active | DeviceState.Disabled);
            if (!captures.Any(d => DeviceCatalog.IsBundledCable(d) && d.Name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase) && d.State == DeviceState.Active))
                throw new InvalidOperationException("CABLE Output 不可用。请在 Windows 录音设备中启用它；不要禁用游戏所需的虚拟麦克风。");
        }
        if (CaptureMode == 1 && !ProcessLoopbackCapture.IsSupported) throw new PlatformNotSupportedException("当前系统不支持指定应用采集，请使用播放设备兼容模式。");
        if (CaptureMode == 1 && SelectedApplication is null) throw new InvalidOperationException("请先选择要采集的应用。");
        if (CaptureMode == 1 && SelectedApplication is not null)
        {
            var current = DeviceCatalog.GetApplication(SelectedApplication.ProcessId);
            if (current is null || SelectedApplication.StartTimeUtcTicks is long start && current.StartTimeUtcTicks != start)
                throw new InvalidOperationException("所选进程已退出或重启。请重新选择进程声音源。");
        }
        if (MonitorEnabled && MonitorDevice is null) throw new InvalidOperationException("请选择耳机或音箱作为监听设备。");
        if (Microphone.Enabled && MixBalance > 0 && MicrophoneDevice is null) throw new InvalidOperationException("没有可用的麦克风。请在左上选择设备，或关闭麦克风通道的‘启用’。");
        IsBusy = true;
        try
        {
            if (HardwareLoopbackMode && HardwareCaptureDevice is not null)
            {
                if (!_hardwareRouteCache.Contains(HardwareCaptureDevice.Id, OutputDevice.Id))
                    await VerifyHardwareOutputAsync(HardwareCaptureDevice, preferSelected: true);
                HardwareLoopbackProbe.ValidateLevels(HardwareCaptureDevice, OutputDevice!);
            }
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            await _engine.StartAsync(new(OutputDevice.Id, MicrophoneDevice?.Id, (ApplicationCaptureMode)CaptureMode,
                SelectedApplication?.ProcessId ?? 0, LoopbackDevice?.Id, MonitorEnabled ? MonitorDevice?.Id : null, Latency), _lifetimeCancellation.Token);
            if (_disposed) throw new OperationCanceledException();
            IsRunning = true; ConnectionText = "正在混音";
            SetStatus(HardwareLoopbackMode ? "正在向声卡输出混音；游戏麦克风选择立体声混音。" : OutputDevice.IsVirtual ? "音频正在发送到虚拟麦克风。" : "正在通过当前输出设备播放。");
            SaveSettings();
        }
        catch (OperationCanceledException) { if (!_disposed) SetStatus("播放前的录回检测已取消。"); throw; }
        finally { if (!_disposed) IsBusy = false; }
    }
    private async Task ToggleRoutingAsync()
    {
        if (IsRunning)
        {
            IsBusy = true;
            try { await _engine.StopAsync(); IsRunning = false; _wasPlaying = false; ConnectionText = "待连接"; SetStatus("混音已停止，音乐已暂停。"); }
            finally { IsBusy = false; }
        }
        else await StartRoutingAsync();
    }
    private async Task TogglePlaybackAsync()
    {
        if (_engine.Player.IsPlaying) { _engine.Player.Pause(); _wasPlaying = false; Tick(); SetStatus(Microphone.Enabled && !Microphone.Muted && MixBalance > 0 ? "音乐已暂停，人声继续混入。" : "音乐已暂停。"); return; }
        SelectedTrack ??= Playlist.FirstOrDefault(); if (SelectedTrack is null) return;
        if (_engine.Player.FilePath != SelectedTrack.Path) LoadTrack(SelectedTrack);
        if (!IsRunning) await StartRoutingAsync();
        _engine.Player.Play(); _wasPlaying = true; Tick();
    }
    public async Task PlaySelectedAsync()
    {
        if (SelectedTrack is null || IsBusy) return;
        LoadTrack(SelectedTrack);
        if (!IsRunning) await StartRoutingAsync();
        _engine.Player.Play(); _wasPlaying = true; Tick();
    }
    private void LoadTrack(TrackItem track)
    {
        _engine.Player.Load(track.Path); CurrentTrack = track.Name; Duration = _engine.Player.Duration.TotalSeconds;
        Set(ref _seekSeconds, 0, nameof(SeekSeconds));
    }
    private async Task MoveTrackAsync(int direction)
    {
        if (Playlist.Count == 0) return;
        bool resume = _engine.Player.IsPlaying;
        int index = Playlist.IndexOf(SelectedTrack ?? Playlist[0]);
        SelectedTrack = Playlist[(index + direction + Playlist.Count) % Playlist.Count]; LoadTrack(SelectedTrack);
        if (resume) { if (!IsRunning) await StartRoutingAsync(); _engine.Player.Play(); _wasPlaying = true; }
        else _wasPlaying = false;
        Tick();
    }
    private void AddFiles()
    {
        var dialog = new OpenFileDialog { Title = "添加本地音乐", Multiselect = true, Filter = "音频文件|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.wma;*.aiff;*.aif|所有文件|*.*" };
        if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames);
    }
    public void AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (!File.Exists(path) || Playlist.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            Playlist.Add(new(System.IO.Path.GetFullPath(path))); added++;
        }
        SelectedTrack ??= Playlist.FirstOrDefault(); SetStatus($"已添加 {added} 首音乐。双击歌曲或点击播放。");
        CommandManager.InvalidateRequerySuggested(); SaveSettings();
    }
    private void RemoveTrack()
    {
        if (SelectedTrack is null) return;
        if (SelectedTrack.Path == _engine.Player.FilePath)
        {
            _engine.Player.Unload(); _wasPlaying = false;
            CurrentTrack = "添加一首音乐，开始你的声音现场"; Duration = 0;
        }
        Playlist.Remove(SelectedTrack); SelectedTrack = Playlist.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested(); SaveSettings();
    }
    private void Tick()
    {
        if (_disposed) return;
        Music.Tick(IsRunning); ApplicationChannel.Tick(IsRunning); Microphone.Tick(IsRunning); Master.Tick(IsRunning, _engine.OutputPeak);
        SourceMeter = IsLocalSource ? Music.Meter : ApplicationChannel.Meter;
        if (_lastSourcePlayText != SourcePlayText) { _lastSourcePlayText = SourcePlayText; Notify(nameof(SourcePlayText)); }
        if (_lastPlaybackStatus != PlaybackStatus) { _lastPlaybackStatus = PlaybackStatus; Notify(nameof(PlaybackStatus)); }
        float peak = _engine.OutputPeak;
        double db = peak > 0.00001 ? 20 * Math.Log10(peak) : -60;
        OutputMeter = IsRunning ? Math.Max(Platform.Clamp((db + 60) / 60 * 100, 0, 100), OutputMeter * 0.8) : 0;
        LimiterText = _engine.LimiterReductionDb > 0.2 ? $"峰值保护 · 衰减 {_engine.LimiterReductionDb:0.0} dB" : "峰值保护已就绪";
        TransportText = _engine.Player.IsPlaying ? "暂停" : "播放";
        var position = _engine.Player.Position;
        var duration = _engine.Player.Duration;
        if (_lastPositionSecond != (long)position.TotalSeconds || _lastDurationSecond != (long)duration.TotalSeconds)
        {
            _lastPositionSecond = (long)position.TotalSeconds; _lastDurationSecond = (long)duration.TotalSeconds;
            TimeText = $"{FormatTime(position)} / {FormatTime(duration)}";
        }
        if (!IsSeeking && (!IsRunning || Math.Abs(_seekSeconds - position.TotalSeconds) >= 0.1)) Set(ref _seekSeconds, position.TotalSeconds, nameof(SeekSeconds));
        if (_wasPlaying && !_engine.Player.IsPlaying && IsRunning && !IsBusy)
        {
            _wasPlaying = false;
            int index = Playlist.ToList().FindIndex(t => t.Path == _engine.Player.FilePath);
            if (index >= 0 && index + 1 < Playlist.Count)
            {
                SelectedTrack = Playlist[index + 1];
                try { LoadTrack(SelectedTrack); _engine.Player.Play(); _wasPlaying = true; } catch (Exception ex) { ReportError(ex); }
            }
        }
    }
    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    private void SetStatus(string text) { HasError = false; Status = text; }
    public void ReportError(Exception ex) { if (_disposed || ex is OperationCanceledException) return; SettingsStore.Log(ex); HasError = true; Status = ex.Message; }
    private void OnEngineFailed(Exception ex)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            if (_disposed || !IsRunning) return;
            await _engine.StopAsync(); IsRunning = false; _wasPlaying = false; ConnectionText = "连接已中断";
            ReportError(new IOException("音频连接已中断。刷新设备或重新选择程序后重试。" + ex.Message, ex));
        });
    }
    private void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (Exception ex) { ReportError(ex); }
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private static void RestoreChannel(ChannelViewModel channel, ChannelSettings settings)
    {
        channel.GainMode = settings.GainMode || settings.Volume > 100;
        channel.Volume = settings.Volume; channel.Gain = settings.Gain; channel.Enabled = settings.Enabled; channel.Muted = settings.Muted;
    }
    private static ChannelSettings Snapshot(ChannelViewModel channel) => new(channel.Volume, channel.Gain, channel.Enabled, channel.Muted, channel.GainMode);
    public void SaveSettings()
    {
        if (!_saveSettings || _suppressSettingsSave) return;
        try
        {
            SettingsStore.Save(new AppSettings
            {
                OutputDeviceId = OutputDevice?.Id, MicrophoneDeviceId = MicrophoneDevice?.Id, MonitorDeviceId = MonitorDevice?.Id,
                HardwareLoopbackMode = HardwareLoopbackMode, HardwareCaptureDeviceId = HardwareCaptureDevice?.Id,
                HardwareVerifiedOutputDeviceId = _hardwareVerifiedOutputDeviceId,
                LoopbackDeviceId = LoopbackDevice?.Id, CaptureMode = CaptureMode, LatencyMs = Latency, SourceKind = SourceKind, MixBalance = MixBalance,
                MonitorEnabled = MonitorEnabled, MonitorVolume = MonitorVolume, Loop = Loop,
                MicrophoneGateEnabled = MicrophoneGateEnabled, MicrophoneHighPassEnabled = MicrophoneHighPassEnabled, MicrophoneGateThreshold = MicrophoneGateThreshold,
                Playlist = Playlist.Select(t => t.Path).ToList(), Music = Snapshot(Music),
                Application = Snapshot(ApplicationChannel), Microphone = Snapshot(Microphone), Master = Snapshot(Master)
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportError(ex); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _deviceChangeTimer.Stop(); _deviceNotifications.Changed -= OnDevicesChanged; _deviceNotifications.Dispose();
        _lifetimeCancellation.Cancel();
        SaveSettings(); _engine.Failed -= OnEngineFailed; _engine.Dispose();
    }
}
