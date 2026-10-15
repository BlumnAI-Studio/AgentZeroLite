using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Music;
using Agent.Common.Vision;
using Agent.Common.Voice;
using Agent.Common.Voice.Diarization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// One on-device model the plugins use: where it lives, whether it is there, and a download.
/// The downloaders are ZeroCommon's (the ones WPF's Music / Vision / Diarization tabs call), so
/// a model fetched by either host is found by both — they share %LOCALAPPDATA%\AgentZeroLite\models.
/// </summary>
public sealed partial class OnDeviceModelRow : ObservableObject
{
    private readonly Func<bool> _probe;
    private readonly Func<IProgress<ModelDownloadStatus>, CancellationToken, Task<bool>> _download;
    private CancellationTokenSource? _cts;

    public OnDeviceModelRow(string name, string usedBy, string size, string location,
        Func<bool> isPresent, Func<IProgress<ModelDownloadStatus>, CancellationToken, Task<bool>> download)
    {
        Name = name;
        UsedBy = usedBy;
        Size = size;
        Location = location;
        _probe = isPresent;
        _download = download;
        Refresh();
    }

    public string Name { get; }
    public string UsedBy { get; }
    public string Size { get; }
    public string Location { get; }

    [ObservableProperty] private bool _isPresent;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _percentKnown;

    public bool CanDownload => !IsDownloading;
    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(CanDownload));

    public void Refresh()
    {
        try { IsPresent = _probe(); }
        catch { IsPresent = false; }
        if (!IsDownloading) State = IsPresent ? "✓ installed" : "not installed";
    }

    [RelayCommand]
    private async Task Download()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        PercentKnown = false;
        Percent = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<ModelDownloadStatus>(s =>
        {
            State = string.IsNullOrEmpty(s.Detail) ? s.Caption : $"{s.Caption} — {s.Detail}";
            PercentKnown = s.PercentComplete is not null;
            Percent = s.PercentComplete ?? 0;
        });
        try
        {
            var ok = await _download(progress, _cts.Token);
            IsDownloading = false;
            Refresh();
            if (!ok && !IsPresent) State = "download failed — see the log";
        }
        catch (OperationCanceledException)
        {
            IsDownloading = false;
            Refresh();
            State = "cancelled";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Models] {Name} download failed: {ex.GetType().Name}: {ex.Message}");
            IsDownloading = false;
            Refresh();
            State = "download failed: " + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}

/// <summary>
/// Settings → Models: the on-device models behind the plugins' voice-note speaker labels
/// (Sherpa), live music (AST AudioSet) and vision (Florence-2) — the WPF host's Music,
/// Vision and Diarization download tabs, converted. Whisper and Supertonic stay on the
/// Voice page, where they already were.
/// </summary>
public sealed partial class OnDeviceModelsViewModel : ObservableObject
{
    public ObservableCollection<OnDeviceModelRow> Models { get; } = new();

    [ObservableProperty] private bool _speakerLabels;

    public OnDeviceModelsViewModel()
    {
        Models.Add(new OnDeviceModelRow(
            "AST AudioSet", "Agent Band live music (music.*) — 527 sound classes", "~347 MB",
            MusicSettingsStore.DefaultModelDirectory,
            () =>
            {
                var s = MusicSettingsStore.Load();
                return File.Exists(MusicSettingsStore.ResolveModelPath(s)) && File.Exists(MusicSettingsStore.ResolveLabelsPath(s));
            },
            AstModelDownloader.DownloadAsync));
        Models.Add(new OnDeviceModelRow(
            "Florence-2", "Agent Band vision (vision.analyze, mp3 cover gender)", "~1.1 GB",
            VisionSettingsStore.DefaultModelDirectory,
            () => VisionSettingsStore.IsModelPresent(VisionSettingsStore.ResolveModelDir(VisionSettingsStore.Load())),
            VisionModelDownloader.DownloadAsync));
        Models.Add(new OnDeviceModelRow(
            "Sherpa diarization", "Voice Note speaker labels — pyannote segmentation 3.0 + 3D-Speaker embedding", "~46 MB",
            DiarizationSettingsStore.DefaultModelDirectory,
            () =>
            {
                var s = DiarizationSettingsStore.Load();
                return File.Exists(DiarizationSettingsStore.ResolveSegmentationPath(s)) && File.Exists(DiarizationSettingsStore.ResolveEmbeddingPath(s));
            },
            SherpaDiarizationModelDownloader.DownloadAsync));

        try { _speakerLabels = DiarizationSettingsStore.Load().Provider != DiarizationProviderNames.Off; }
        catch (Exception ex) { AppLogger.Log($"[Models] diarization settings unreadable: {ex.Message}"); }
    }

    // Shared with WPF's Diarization tab (diarization-settings.json): the note pipeline reads it per utterance.
    partial void OnSpeakerLabelsChanged(bool value)
    {
        try
        {
            var s = DiarizationSettingsStore.Load();
            s.Provider = value ? DiarizationProviderNames.SherpaPyannote3D : DiarizationProviderNames.Off;
            DiarizationSettingsStore.Save(s);
        }
        catch (Exception ex) { AppLogger.Log($"[Models] diarization setting not saved: {ex.Message}"); }
    }

    [RelayCommand]
    private void Refresh()
    {
        foreach (var m in Models) m.Refresh();
    }
}
