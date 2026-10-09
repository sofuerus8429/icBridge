using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using MicBridge.App.Infrastructure;
using MicBridge.Audio.Devices;

namespace MicBridge.App.ViewModels;

public sealed class ProcessPickerViewModel : ObservableObject
{
    private readonly ObservableCollection<ApplicationInfo> _processes = [];
    private readonly int? _initialProcessId;
    private string _searchText = "", _status = "正在读取进程…";
    private bool _onlyAudio, _isBusy;
    private ApplicationInfo? _selectedProcess;
    public ICollectionView Processes { get; }
    public ICommand RefreshCommand { get; }
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) RefreshFilter(); }
    }
    public bool OnlyAudio
    {
        get => _onlyAudio;
        set { if (Set(ref _onlyAudio, value)) RefreshFilter(); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) { Notify(nameof(CanAccept)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ApplicationInfo? SelectedProcess
    {
        get => _selectedProcess;
        set { if (Set(ref _selectedProcess, value)) { Notify(nameof(CanAccept)); Notify(nameof(SelectionText)); } }
    }
    public bool CanAccept => SelectedProcess?.CanCapture == true && !IsBusy;
    public string SelectionText => SelectedProcess is null ? "选中一项，然后点击“用作声音源”。" : !SelectedProcess.CanCapture ? "此进程不可作为采集源，请选择播放器进程。" : $"将采集 {SelectedProcess} 及其子进程。";

    public ProcessPickerViewModel(int? initialProcessId = null)
    {
        _initialProcessId = initialProcessId;
        Processes = CollectionViewSource.GetDefaultView(_processes);
        Processes.Filter = MatchesFilter;
        RefreshCommand = new AsyncCommand(RefreshAsync, ReportError, () => !IsBusy);
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        int? selectedId = SelectedProcess?.ProcessId ?? _initialProcessId;
        long? selectedStart = SelectedProcess?.StartTimeUtcTicks;
        try
        {
            var items = await Task.Run(() => DeviceCatalog.GetApplications(includeBackground: true));
            // ListCollectionView cannot process incremental collection events during DeferRefresh.
            _processes.Clear();
            foreach (var item in items) _processes.Add(item);
            SelectedProcess = _processes.FirstOrDefault(p => p.ProcessId == selectedId &&
                (selectedStart is null || p.StartTimeUtcTicks == selectedStart) && MatchesFilter(p));
            UpdateCount();
        }
        finally { IsBusy = false; }
    }

    private bool MatchesFilter(object value)
    {
        if (value is not ApplicationInfo process || OnlyAudio && !process.HasAudioSession) return false;
        string query = SearchText.Trim();
        return query.Length == 0 || process.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            process.WindowTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            process.ProcessId.ToString().Contains(query, StringComparison.Ordinal) ||
            $"{process.Name}.exe".Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    private void RefreshFilter()
    {
        Processes.Refresh();
        if (SelectedProcess is not null && !MatchesFilter(SelectedProcess)) SelectedProcess = null;
        UpdateCount();
    }
    private void UpdateCount() => Status = $"显示 {Processes.Cast<ApplicationInfo>().Count()} / {_processes.Count} 个进程。默认显示全部进程，包含系统和后台进程。";
    public void ReportError(Exception error) => Status = error.Message;
}
