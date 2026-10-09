using MicBridge.App.Infrastructure;
using MicBridge.Audio.Mixing;

namespace MicBridge.App.ViewModels;

public sealed class ChannelViewModel(MixerChannel channel) : ObservableObject
{
    private double _meter;
    private double _inputMeter;
    private bool _gainMode;
    private string _level = "−∞ dB";
    private double _lastLevelDb = double.NaN;
    public double Volume { get => Math.Round(channel.Volume * 100, 3); set { channel.Volume = (float)(Platform.IsFinite(value) ? Platform.Clamp(value, 0, VolumeMaximum) / 100 : 0); Notify(); Notify(nameof(VolumeText)); } }
    public bool GainMode
    {
        get => _gainMode;
        set { if (Set(ref _gainMode, value)) { if (!value && Volume > 100) Volume = 100; Notify(nameof(VolumeMaximum)); } }
    }
    public double VolumeMaximum => GainMode ? 300 : 100;
    public double Gain { get => channel.GainDb; set { channel.GainDb = (float)value; Notify(); Notify(nameof(GainText)); } }
    public bool Enabled { get => channel.Enabled; set { channel.Enabled = value; Notify(); } }
    public bool Muted { get => channel.Muted; set { channel.Muted = value; Notify(); } }
    public string VolumeText => $"{Volume:0}%";
    public string GainText => $"{Gain:+0.0;-0.0;0.0} dB";
    public double Meter { get => _meter; private set => Set(ref _meter, value); }
    public double InputMeter { get => _inputMeter; private set => Set(ref _inputMeter, value); }
    public string Level { get => _level; private set => Set(ref _level, value); }
    public void Tick(bool running, float? outputPeak = null)
    {
        float peak = running ? outputPeak ?? channel.Peak : 0;
        float inputPeak = running ? channel.InputPeak : 0;
        double db = peak > 0.00001f ? 20 * Math.Log10(peak) : -60;
        Meter = running ? Math.Max(Platform.Clamp((db + 60) / 60 * 100, 0, 100), Meter * 0.8) : 0;
        double inputDb = inputPeak > 0.00001f ? 20 * Math.Log10(inputPeak) : -60;
        InputMeter = running ? Math.Max(Platform.Clamp((inputDb + 60) / 60 * 100, 0, 100), InputMeter * 0.8) : 0;
        double displayDb = peak > 0.00001f ? Math.Round(db, 1) : double.NegativeInfinity;
        if (_lastLevelDb != displayDb) { _lastLevelDb = displayDb; Level = peak > 0.00001f ? $"{displayDb:0.0} dBFS" : "−∞ dBFS"; }
    }
}
