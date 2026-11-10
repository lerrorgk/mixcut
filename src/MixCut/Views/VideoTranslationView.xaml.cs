using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MixCut.Infrastructure;
using MixCut.Services.Dubbing;
using MixCut.Services.VideoProcessing;
using MixCut.Services.VideoTranslation;
using MixCut.Utilities;
using MixCut.ViewModels;
using MixCut.Views.Components;

namespace MixCut.Views;

/// <summary>整片字幕翻译工作台：独立草稿，不读取或写入混剪项目与分镜。</summary>
public partial class VideoTranslationView : UserControl
{
    private readonly VideoTranslationService _service;
    private readonly FFmpegRunner _ffmpeg;
    private VideoTranslationDocument? _document;
    private readonly ObservableCollection<TranslationCueRow> _rows = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationEnded;
    private CancellationTokenSource? _frameOperation;
    private Task _saveTask = Task.CompletedTask;
    private bool _loading = true;
    private bool _dirty;
    private string? _lastOutput;
    private double _lastOutputDuration;
    private bool Busy => _operation is not null;

    public VideoTranslationView(VideoTranslationService service, FFmpegRunner ffmpeg)
    {
        _service = service; _ffmpeg = ffmpeg;
        InitializeComponent();
        LanguagePicker.ItemsSource = TranslationService.Languages.Select(l => new LanguageOption(l.Label, l.Name)).ToArray();
        LanguagePicker.SelectedIndex = 0;
        CueList.ItemsSource = _rows;
        MaskOverlay.CommitCommand = new RelayCommand(() => { if (!Busy && !_loading) { ReadOptions(); MarkDirty(); } });
        _saveTimer.Tick += OnSaveTick;
        _frameTimer.Tick += OnFrameTick;
        Loaded += (_, _) => { UpdateAvailability(); if (_dirty) _saveTimer.Start(); };
        Unloaded += OnUnloaded;
        _loading = false;
        UpdateAvailability();
    }

    private async Task RunOperationAsync(Func<CancellationToken, IProgress<TranslationProgress>, Task> action)
    {
        if (Busy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationEnded = completion;
        _saveTimer.Stop(); _frameTimer.Stop(); _frameOperation?.Cancel();
        UpdateAvailability();
        SetStatus("处理中…", Brushes.DodgerBlue);
        Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true;
        var progress = new Progress<TranslationProgress>(p =>
        {
            if (!ReferenceEquals(_operation, operation) || operation.IsCancellationRequested) return;
            SetStatus(p.Text, Brushes.DodgerBlue);
            Progress.IsIndeterminate = p.Fraction < 0;
            if (p.Fraction >= 0) Progress.Value = Math.Clamp(p.Fraction, 0, 1);
        });
        try
        {
            await FlushSaveAsync();
            await action(operation.Token, progress);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { SetStatus("已取消。已完成的识别或校对保留，可以继续翻译或导出。", Brushes.DarkOrange); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (_saveTask.IsFaulted) _saveTask = Task.CompletedTask;
            if (_document is not null) RefreshRows();
            _operation = null;
            _operationEnded = null;
            completion.TrySetResult();
            Progress.Visibility = Visibility.Collapsed;
            UpdateAvailability();
            if (_dirty) _saveTimer.Start();
        }
    }

    public bool NeedsCloseSave => Busy || _dirty || !_saveTask.IsCompleted;

    public async Task PrepareToCloseAsync()
    {
        IsEnabled = false;
        try
        {
            var pending = _operationEnded?.Task;
            _operation?.Cancel();
            SetStatus("正在保存字幕草稿后关闭…", Brushes.DarkOrange);
            if (pending is not null) await pending;
            if (_document is not null) _dirty = true;
            await FlushSaveAsync();
        }
        finally { IsEnabled = true; }
    }

    private async Task OpenAsync(string path)
    {
        await RunOperationAsync(async (ct, progress) =>
        {
            var doc = await _service.OpenAsync(path, progress, ct);
            ct.ThrowIfCancellationRequested();
            _document = doc; _dirty = false;
            _lastOutput = null; OpenButton.Visibility = Visibility.Collapsed;
            _loading = true;
            try
            {
                FileInfoText.Text = $"{doc.Name} · {FrameTime.HumanDuration(doc.Duration)} · {doc.Width} × {doc.Height}";
                PreviewCanvas.Height = 260.0 * doc.Height / Math.Max(1, doc.Width);
                PreviewImage.Source = await ThumbnailCache.Shared.LoadAsync(doc.ThumbnailPath);
                FrameSlider.Maximum = Math.Max(0.01, doc.Duration - 1.0 / Math.Max(1, doc.Fps));
                FrameSlider.Value = 0;
                MaskOverlay.Rect = doc.MaskRect;
                BlurCheck.IsChecked = doc.BlurOriginalSubtitles;
                MaskOverlay.ShowMaskBox = doc.BlurOriginalSubtitles;
                FontSlider.Value = doc.FontRatio;
                LanguagePicker.SelectedItem = LanguagePicker.Items.Cast<LanguageOption>().FirstOrDefault(l => l.Name == doc.TargetLanguage) ?? LanguagePicker.Items[0];
            }
            finally { _loading = false; }
            SetStatus(doc.Cues.Count > 0 ? "已恢复本视频草稿，可继续校对、翻译或导出。" : "已读取完整视频。选择目标语言，点击“识别并翻译全片”。", Brushes.DarkGreen);
            SaveStatus.Text = doc.Cues.Count > 0 ? "已恢复保存的识别与校对" : "";
            Serilog.Log.Information("[VideoTranslationDiag] 工作台 loaded cues={Count} projectRequired=false", doc.Cues.Count);
        });
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Busy) return;
            var dialog = new OpenFileDialog { Title = "选择要翻译的完整视频", Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv|所有文件|*.*", Multiselect = false };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) await OpenAsync(dialog.FileName);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private bool ConfirmTranslation() => Shared.MixCutDialog.Confirm(Window.GetWindow(this), "翻译整片字幕？",
        "将使用设置中当前配置的 AI 模型翻译整片字幕，可能消耗该服务的 API 额度。\n识别在本机完成；译文会保存为可校对草稿。",
        confirmText: "开始翻译", cancelText: "取消", icon: "💬");

    private async void OnProcess(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Busy || _document is null || !ConfirmTranslation()) return;
            await RunOperationAsync(async (ct, progress) =>
            {
                var doc = _document!; ReadOptions();
                if (doc.Cues.Count == 0) await _service.RecognizeAsync(doc, progress, ct);
                RefreshRows();
                await _service.TranslateAsync(doc, progress, ct);
                SetStatus("✓ 整片字幕已翻译，可直接校对并导出翻译视频。", Brushes.DarkGreen);
                ToastService.Show("整片字幕翻译完成", ToastStyle.Success);
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnTranslate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Busy || _document is null || !ConfirmTranslation()) return;
            await RunOperationAsync(async (ct, progress) =>
            {
                ReadOptions();
                await _service.TranslateAsync(_document!, progress, ct);
                SetStatus("✓ 全部字幕已翻译，可校对后导出。", Brushes.DarkGreen);
                ToastService.Show("全部字幕翻译完成", ToastStyle.Success);
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnRecognize(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Busy || _document is null) return;
            if (_document.Cues.Count > 0 && !Shared.MixCutDialog.Confirm(Window.GetWindow(this), "重新识别整片？",
                "重新识别会替换本视频现有字幕草稿及校对。原视频不会改动。", confirmText: "重新识别", cancelText: "保留校对", icon: "💬")) return;
            await RunOperationAsync(async (ct, progress) =>
            {
                await _service.RecognizeAsync(_document!, progress, ct);
                SetStatus("✓ 整片识别完成。可校对原文，再点击“翻译全部字幕”。", Brushes.DarkGreen);
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Busy || _document is null) return;
            var dialog = new SaveFileDialog { Title = "导出翻译视频", Filter = "MP4 视频|*.mp4", DefaultExt = ".mp4", AddExtension = true,
                FileName = Path.GetFileNameWithoutExtension(_document.Name) + "_翻译.mp4" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var output = dialog.FileName;
            await RunOperationAsync(async (ct, progress) =>
            {
                ReadOptions();
                await _service.SaveAsync(_document!, ct);
                await _service.ExportAsync(_document!, output, progress, ct);
                _lastOutput = output; _lastOutputDuration = _document!.Duration;
                OpenButton.Visibility = Visibility.Visible;
                SetStatus("✓ 翻译视频已导出：" + output, Brushes.DarkGreen);
                ToastService.Show("翻译视频导出成功", ToastStyle.Success);
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        string? preview = null;
        try
        {
            if (Busy || _document is null) return;
            await RunOperationAsync(async (ct, progress) =>
            {
                ReadOptions();
                preview = Path.Combine(FileHelper.TempDirectory, $"translation-preview-{Guid.NewGuid():N}.mp4");
                await _service.ExportAsync(_document!, preview, progress, ct);
                ct.ThrowIfCancellationRequested();
                SetStatus("✓ 处理效果已生成，可播放查看字幕（成片完全无声）。", Brushes.DarkGreen);

            });
            if (preview is not null && File.Exists(preview) && IsLoaded && _document is not null)
                ShowPlayer(preview, _document.Duration, "翻译效果预览");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (preview is not null) { try { File.Delete(preview); } catch { /* 本次预览临时文件 */ } } }
    }

    private void ReadOptions()
    {
        if (_document is not { } doc) return;
        doc.MaskRect = MaskOverlay.Rect.Clamped();
        doc.BlurOriginalSubtitles = BlurCheck.IsChecked == true;
        doc.FontRatio = FontSlider.Value;
        if (LanguagePicker.SelectedItem is LanguageOption language) doc.TargetLanguage = language.Name;
        MaskOverlay.ShowMaskBox = doc.BlurOriginalSubtitles;
    }

    private void RefreshRows()
    {
        if (_document is not { } doc) return;
        if (_rows.Count == doc.Cues.Count && _rows.Select(r => r.Cue).SequenceEqual(doc.Cues))
        { foreach (var row in _rows) row.Refresh(); }
        else
        {
            _loading = true;
            try { _rows.Clear(); foreach (var cue in doc.Cues) _rows.Add(new TranslationCueRow(cue, MarkDirty)); }
            finally { _loading = false; }
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_document is not { } doc) return;
        var translated = doc.Cues.Count(c => !string.IsNullOrWhiteSpace(c.TranslatedText));
        CueSummary.Text = $"整片 {doc.Cues.Count} 句 · 已有译文 {translated} 句。修改会自动保存；修改原文后请重新翻译。"
            + (doc.TranslatedLanguage.Length > 0 && doc.TranslatedLanguage != doc.TargetLanguage ? " 目标语言已更改，请重新翻译。" : "");
    }

    private void UpdateAvailability()
    {
        if (!IsInitialized) return;
        var hasDocument = _document is not null;
        var hasCues = _document?.Cues.Count > 0;
        var ready = hasCues && _document!.TranslatedLanguage == _document.TargetLanguage
            && _document.Cues.All(c => !string.IsNullOrWhiteSpace(c.TranslatedText));
        ImportButton.IsEnabled = !Busy;
        Workspace.IsEnabled = hasDocument && !Busy;
        ProcessButton.Content = hasCues ? "翻译全片（复用识别）" : "识别并翻译全片";
        TranslateButton.IsEnabled = hasCues && !Busy;
        ExportButton.IsEnabled = ready && !Busy;
        PreviewButton.IsEnabled = ready && !Busy;
        SaveButton.IsEnabled = hasDocument && !Busy;
        OpenButton.IsEnabled = !Busy;
        CancelButton.Visibility = Busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkDirty()
    {
        if (_loading || _document is null) return;
        _dirty = true; SaveStatus.Text = "校对有更改，等待自动保存…";
        _saveTimer.Stop(); if (!Busy) _saveTimer.Start();
        UpdateSummary(); UpdateAvailability();
    }

    private async Task FlushSaveAsync()
    {
        _saveTimer.Stop();
        await _saveTask;
        if (!_dirty || _document is null) return;
        ReadOptions();
        _dirty = false;
        try { _saveTask = _service.SaveAsync(_document); await _saveTask; SaveStatus.Text = "✓ 校对已保存"; }
        catch { _dirty = true; throw; }
    }

    private async void OnSaveTick(object? sender, EventArgs e)
    {
        try { _saveTimer.Stop(); if (!Busy) await FlushSaveAsync(); }
        catch (Exception ex) { _saveTask = Task.CompletedTask; ShowError(ex); SaveStatus.Text = "保存失败，请点击“保存校对”重试。"; }
    }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try { if (Busy) return; await FlushSaveAsync(); ToastService.Show("校对已保存", ToastStyle.Success); }
        catch (Exception ex) { _saveTask = Task.CompletedTask; ShowError(ex); }
    }
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try { _frameTimer.Stop(); _frameOperation?.Cancel(); if (!Busy) await FlushSaveAsync(); }
        catch (Exception ex) { _saveTask = Task.CompletedTask; ShowError(ex); }
    }
    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    { try { if (!_loading && !Busy) { ReadOptions(); MarkDirty(); } } catch (Exception ex) { ShowError(ex); } }
    private void OnFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!_loading && !Busy) { ReadOptions(); MarkDirty(); } }
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    { if (!_loading && !Busy) { ReadOptions(); MarkDirty(); } }
    private void OnCancel(object sender, RoutedEventArgs e)
    { _operation?.Cancel(); SetStatus("正在取消，请稍候…", Brushes.DarkOrange); }
    private void OnFrameChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!_loading && !Busy && IsLoaded) { _frameOperation?.Cancel(); _frameTimer.Stop(); _frameTimer.Start(); } }
    private void OnCueSelected(object sender, SelectionChangedEventArgs e)
    { if (!_loading && !Busy && CueList.SelectedItem is TranslationCueRow row) FrameSlider.Value = row.Cue.Start; }

    private async void OnFrameTick(object? sender, EventArgs e)
    {
        string? image = null;
        using var operation = new CancellationTokenSource();
        try
        {
            _frameTimer.Stop();
            if (_document is not { } doc || Busy || !IsLoaded) return;
            _frameOperation?.Cancel(); _frameOperation = operation;
            image = Path.Combine(FileHelper.TempDirectory, $"translation-frame-{Guid.NewGuid():N}.jpg");
            await _ffmpeg.RunAsync(new[] { "-y", "-ss", FrameSlider.Value.ToString("F3", CultureInfo.InvariantCulture), "-i", doc.SourcePath,
                "-frames:v", "1", "-vf", "scale=540:-2", image }, cancellationToken: operation.Token);
            var bitmap = await ThumbnailCache.Shared.LoadAsync(image);
            if (!operation.IsCancellationRequested && ReferenceEquals(doc, _document) && IsLoaded) PreviewImage.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (operation.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_frameOperation, operation)) _frameOperation = null;
            if (image is not null) { try { File.Delete(image); } catch { /* 本次抽帧 */ } }
        }
    }

    private void OnPlayOriginal(object sender, RoutedEventArgs e)
    { try { if (_document is { } doc) ShowPlayer(doc.SourcePath, doc.Duration, "完整原片预览"); } catch (Exception ex) { ShowError(ex); } }
    private void OnOpenOutput(object sender, RoutedEventArgs e)
    { try { if (_lastOutput is not null && File.Exists(_lastOutput)) ShowPlayer(_lastOutput, _lastOutputDuration, "翻译成片预览"); else throw new DubException("导出文件已移动，请重新导出。"); } catch (Exception ex) { ShowError(ex); } }
    private void ShowPlayer(string path, double duration, string title)
    {
        var player = new InlineVideoPlayer(); player.SetVideo(path, null, duration);
        var window = new Window { Title = title, Owner = Window.GetWindow(this), Width = 640, Height = 760, Content = player,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Loaded += (_, _) => player.Play();
        window.Closed += (_, _) => player.StopPlayback();
        window.ShowDialog();
    }
    private void OnDragOver(object sender, DragEventArgs e)
    { e.Effects = !Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            e.Handled = true; if (Busy) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1) { ToastService.Show("请一次选择一个完整视频", ToastStyle.Info); return; }
            await OpenAsync(files[0]);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void SetStatus(string text, Brush color) { StatusText.Text = text; StatusText.Foreground = color; }
    private void ShowError(Exception ex)
    {
        Serilog.Log.Error(ex, "[VideoTranslationDiag] 工作台操作失败");
        SetStatus(ExceptionTranslator.ToUserMessage(ex), Brushes.Firebrick);
        ToastService.Show(StatusText.Text, ToastStyle.Error);
    }
    private sealed record LanguageOption(string Label, string Name);
}

public sealed class TranslationCueRow : ObservableObject
{
    private readonly Action _changed;
    public TranslationCue Cue { get; }
    public TranslationCueRow(TranslationCue cue, Action changed) { Cue = cue; _changed = changed; }
    public string TimeLabel => $"{Cue.Id} · {FrameTime.HumanDuration(Cue.Start)} – {FrameTime.HumanDuration(Cue.End)}";
    public string OriginalText
    {
        get => Cue.OriginalText;
        set
        {
            if (value == Cue.OriginalText) return;
            Cue.OriginalText = value; Cue.TranslatedText = "";
            OnPropertyChanged(); OnPropertyChanged(nameof(TranslatedText)); _changed();
        }
    }
    public string TranslatedText
    {
        get => Cue.TranslatedText;
        set { if (value == Cue.TranslatedText) return; Cue.TranslatedText = value; OnPropertyChanged(); _changed(); }
    }
    public void Refresh() { OnPropertyChanged(nameof(OriginalText)); OnPropertyChanged(nameof(TranslatedText)); }
}
