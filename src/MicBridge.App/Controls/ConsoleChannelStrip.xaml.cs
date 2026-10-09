using System.Windows;
using System.Windows.Controls;

namespace MicBridge.App.Controls;

public partial class ConsoleChannelStrip : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ConsoleChannelStrip), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ConsoleChannelStrip), new PropertyMetadata(""));
    public static readonly DependencyProperty IsOutputProperty = DependencyProperty.Register(nameof(IsOutput), typeof(bool), typeof(ConsoleChannelStrip), new PropertyMetadata(false));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public bool IsOutput { get => (bool)GetValue(IsOutputProperty); set => SetValue(IsOutputProperty, value); }
    public ConsoleChannelStrip() => InitializeComponent();
}
