using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MixCut.Infrastructure;
using MixCut.Models;
using MixCut.Services.Export;
using MixCut.Utilities;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>导出视图。对应 macOS 版 ExportView（单方案 + 批量导出）。</summary>
public partial class ExportView : UserControl, IProjectView
{
    private readonly SchemeViewModel _schemeVM;
    private readonly ExportService _exportService;
    private readonly Services.Dubbing.DubExportService _dubExport;
    private readonly AppSettings _settings;
    private readonly Services.Dubbing.VocalSeparationService _vocalSep;
    private readonly Services.Bgm.BgmLibraryService _bgmLibrary;
    private readonly Action<NavigationItem>? _navigate;
    private string? _lastOutputDir;

    /// <summary>#22：BGM 下拉当前对应的库条目（index 0 = 「保留原 BGM」）。</summary>
    private List<Services.Bgm.BgmItem> _bgmItems = new();
    private bool _suppressBgmEvent;

    /// <summary>QW-11：批量导出的取消令牌。ExportService 早就支持 CancellationToken，
    /// 但 view 从来没传过 —— 现在接上，「取消」按钮 / ESC 能真正中断长任务。</summary>
    private CancellationTokenSource? _exportCts;
    /// <summary>#11：导出进行中 —— 切项目/刷新时不许重启用导出按钮（防后台还在导时重入再点导出）。</summary>
    private bool _isExporting;

    /// <summary>用户勾选的待导出方案 ID 集合。LoadProject 时默认全选当前项目所有方案。</summary>
    private readonly HashSet<Guid> _selectedSchemeIds = new();

    public ExportView(SchemeViewModel schemeVM, ExportService exportService,
        Services.Dubbing.DubExportService dubExport, AppSettings settings,
        Services.Dubbing.VocalSeparationService vocalSep,
        Services.Bgm.BgmLibraryService bgmLibrary,
        Action<NavigationItem>? navigate = null)
    {
        _schemeVM = schemeVM;
        _exportService = exportService;
        _dubExport = dubExport;
        _settings = settings;
        _vocalSep = vocalSep;
        _bgmLibrary = bgmLibrary;
        _navigate = navigate;
        InitializeComponent();

        foreach (var r in Enum.GetValues<ExportResolution>())
        {
            ResolutionCombo.Items.Add(r.Label());
        }
        foreach (var c in Enum.GetValues<ExportCodec>())
        {
            CodecCombo.Items.Add(c.Label());
        }
        foreach (var q in Enum.GetValues<ExportQuality>())
        {
            QualityCombo.Items.Add(q.Label());
        }
        // #5：跨会话记忆上次的分辨率/编码/质量选择（clamp 防枚举增减越界）；默认 1080p/首编码器/高质量。
        ResolutionCombo.SelectedIndex = Math.Clamp(_settings.LastExportResolution, 0, ResolutionCombo.Items.Count - 1);
        CodecCombo.SelectedIndex = Math.Clamp(_settings.LastExportCodec, 0, CodecCombo.Items.Count - 1);
        QualityCombo.SelectedIndex = Math.Clamp(_settings.LastExportQuality, 0, QualityCombo.Items.Count - 1);

        // 显示硬件加速探测结果（NVIDIA / Intel / AMD / MF / 无）
        HardwareStatusText.Text = "硬件加速：" + HardwareEncoderProbe.HardwareDescription;
        HardwareStatusText.Foreground = HardwareEncoderProbe.HasAnyHardware
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57))     // 绿：有硬件
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC0, 0x6F, 0x00));    // 橙：仅软件
        UpdateQualityHint();

        // #22：BGM 列表初始化 + 每次进入导出页刷新（用户可能刚在「BGM 库」上传/删除了音乐）。
        _ = RefreshBgmListAsync();
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true) await RefreshBgmListAsync();
        };
    }

    // ---- 背景音乐（issue #22）----

    /// <summary>当前生效的 BGM 路径；null = 保留原 BGM。以 AppSettings 为单一真源（全局状态，切项目不重置）。</summary>
    private string? CurrentBgmPath =>
        string.IsNullOrEmpty(_settings.SelectedBgmPath) ? null : _settings.SelectedBgmPath;

    /// <summary>
    /// 刷新 BGM 下拉：重扫库 + 校验已选路径还存在（可能刚在「BGM 库」删了）——
    /// 不存在则重置回「保留原 BGM」（issue #22 §3.2）。
    /// </summary>
    private async System.Threading.Tasks.Task RefreshBgmListAsync()
    {
        try
        {
            _bgmItems = (await _bgmLibrary.ListAsync()).ToList();
            _suppressBgmEvent = true;
            BgmCombo.Items.Clear();
            BgmCombo.Items.Add("保留原 BGM");
            foreach (var it in _bgmItems)
            {
                BgmCombo.Items.Add($"{it.FileName}（{Utilities.FrameTime.HumanDuration(it.DurationSeconds)}）");
            }

            var stored = _settings.SelectedBgmPath;
            var idx = 0;
            if (!string.IsNullOrEmpty(stored))
            {
                var found = _bgmItems.FindIndex(
                    i => string.Equals(i.FullPath, stored, StringComparison.OrdinalIgnoreCase));
                if (found >= 0 && File.Exists(stored))
                {
                    idx = found + 1;
                }
                else
                {
                    _settings.SelectedBgmPath = string.Empty;   // 文件没了 → 重置
                }
            }
            BgmCombo.SelectedIndex = idx;
            _suppressBgmEvent = false;
            BgmVolumeSlider.Value = _settings.BgmVolumePercent;
            UpdateBgmUiState();
        }
        catch (Exception ex)
        {
            _suppressBgmEvent = false;
            Serilog.Log.Warning(ex, "[BgmDiag] 刷新导出页 BGM 下拉失败");
        }
    }

    private void OnBgmChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressBgmEvent) return;
        var idx = BgmCombo.SelectedIndex;
        _settings.SelectedBgmPath = idx > 0 && idx - 1 < _bgmItems.Count
            ? _bgmItems[idx - 1].FullPath
            : string.Empty;
        UpdateBgmUiState();
    }

    private void OnBgmVolumeChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BgmVolumeLabel is null) return;   // XAML 初始化期间的首次触发
        var v = (int)Math.Round(e.NewValue);
        BgmVolumeLabel.Text = v + "%";
        _settings.BgmVolumePercent = v;
    }

    /// <summary>选中 BGM 后显示音量滑杆 + 行为说明；库为空时显示引导文案。</summary>
    private void UpdateBgmUiState()
    {
        var selected = BgmCombo.SelectedIndex > 0;
        BgmVolumePanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (selected)
        {
            BgmHintText.Text = "将去除各分镜原 BGM、只保留口播，再铺上所选音乐"
                + "（比成片长则截断，短则循环，结尾 1 秒淡出）。首次会对相关视频做人声分离，耗时较长。";
            BgmHintText.Visibility = Visibility.Visible;
        }
        else if (_bgmItems.Count == 0)
        {
            BgmHintText.Text = "BGM 库还没有音乐。到侧边栏「BGM 库」上传后，可在这里选一条替换成片背景音乐。";
            BgmHintText.Visibility = Visibility.Visible;
        }
        else
        {
            BgmHintText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>#15：空态「去生成方案」→ 跳到混剪方案页（消除死胡同）。</summary>
    private void OnGoGenerateSchemes(object sender, RoutedEventArgs e) => _navigate?.Invoke(NavigationItem.Schemes);

    /// <summary>编码器/质量/分辨率变化时刷新质量提示（码率 + 文件大小估算），并记忆选择（#5 跨会话）。</summary>
    private void OnConfigChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ResolutionCombo.SelectedIndex >= 0) _settings.LastExportResolution = ResolutionCombo.SelectedIndex;
        if (CodecCombo.SelectedIndex >= 0) _settings.LastExportCodec = CodecCombo.SelectedIndex;
        if (QualityCombo.SelectedIndex >= 0) _settings.LastExportQuality = QualityCombo.SelectedIndex;
        UpdateQualityHint();
    }

    private void UpdateQualityHint()
    {
        if (QualityHintText is null) return;
        var cfg = BuildConfig();
        QualityHintText.Text = cfg.QualityHint;
        // 配置变了 → 预估大小也要刷新
        RefreshEstimatedSize();
    }

    public void LoadProject(Project project)
    {
        _schemeVM.LoadSchemes(project);

        // 默认全选当前项目所有方案（对齐 Mac v0.3.0 默认全选语义）
        // 重置 _selectedSchemeIds 保证上个项目的选择状态不会泄露过来
        _selectedSchemeIds.Clear();
        foreach (var strategy in _schemeVM.Strategies)
        {
            foreach (var scheme in strategy.Schemes)
            {
                _selectedSchemeIds.Add(scheme.Id);
            }
        }

        RefreshOverview();
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    /// <summary>按当前 ExportConfig + 总时长刷新预估大小，对齐 Mac ExportView 概览第四项。</summary>
    private void RefreshEstimatedSize()
    {
        if (EstimatedSizeText is null) return;
        var totalDuration = _schemeVM.Schemes.Sum(s => s.EstimatedDuration);
        if (totalDuration <= 0)
        {
            EstimatedSizeText.Text = "—";
            return;
        }
        var sizeMB = BuildConfig().EstimatedTotalSizeMB(totalDuration);
        EstimatedSizeText.Text = sizeMB >= 1024
            ? (sizeMB / 1024).ToString("F1", CultureInfo.InvariantCulture) + " GB"
            : sizeMB.ToString("F0", CultureInfo.InvariantCulture) + " MB";
    }

    private void RefreshOverview()
    {
        var schemes = _schemeVM.Schemes;
        StrategyCountText.Text = _schemeVM.Strategies.Count.ToString(CultureInfo.InvariantCulture);
        SchemeCountText.Text = schemes.Count.ToString(CultureInfo.InvariantCulture);
        var totalDuration = schemes.Sum(s => s.EstimatedDuration);
        TotalDurationText.Text = Utilities.FrameTime.HumanDuration(totalDuration);
        RefreshEstimatedSize();

        var hasAny = schemes.Count > 0;
        NoSchemeHint.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;

        // v0.6.0 起单方案导出区块移除（筛选导出已覆盖此场景：选 1 个等同于原「单方案导出」）。
        // ExportButton.IsEnabled 由 UpdateExportButtonText 根据 _selectedSchemeIds 计数管理
    }

    // ---- 批量导出选择面板（v0.3.0：嵌入式策略 checkbox 列表 + 三态半选）----

    /// <summary>渲染策略 / 方案 checkbox 树。每次切项目 / 全选反选清空时调用。</summary>
    private void RefreshSelectionPanel()
    {
        // 保留滚动位置：勾选一个框会整树重建，避免每次勾选跳回顶部。
        var savedOffset = StrategyTreeScroll?.VerticalOffset ?? 0;

        StrategyTree.Items.Clear();
        var strategies = _schemeVM.Strategies;
        if (strategies.Count == 0)
        {
            SelectionPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SelectionPanel.Visibility = Visibility.Visible;

        foreach (var strategy in strategies)
        {
            StrategyTree.Items.Add(BuildStrategyGroup(strategy));
        }

        UpdateSelectedCountLabel();

        if (savedOffset > 0)
        {
            Dispatcher.BeginInvoke(new Action(() => StrategyTreeScroll?.ScrollToVerticalOffset(savedOffset)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>构建单个策略的折叠组：策略三态 checkbox + 子方案 checkbox 列表。</summary>
    private UIElement BuildStrategyGroup(MixStrategy strategy)
    {
        var sp = new StackPanel();

        var strategyCheck = new CheckBox
        {
            IsThreeState = true,
            Tag = strategy,
            Margin = new Thickness(0, 4, 0, 4),
            Content = new TextBlock
            {
                Text = strategy.Name,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)),
            },
        };
        strategyCheck.IsChecked = ComputeStrategyTriState(strategy);
        strategyCheck.Click += OnStrategyCheckClick;
        sp.Children.Add(strategyCheck);

        var childPanel = new StackPanel { Margin = new Thickness(22, 0, 0, 6) };
        foreach (var scheme in strategy.OrderedSchemes)
        {
            childPanel.Children.Add(BuildSchemeCheck(scheme, strategy));
        }
        sp.Children.Add(childPanel);

        return sp;
    }

    private UIElement BuildSchemeCheck(MixScheme scheme, MixStrategy parent)
    {
        var cb = new CheckBox
        {
            Tag = (scheme, parent),
            IsChecked = _selectedSchemeIds.Contains(scheme.Id),
            Margin = new Thickness(0, 2, 0, 2),
        };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock
        {
            Text = scheme.Name, FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
        });
        if (scheme.IsManuallyEdited)
        {
            label.Children.Add(new TextBlock
            {
                Text = "·已修改", FontSize = 9,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80)),
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        }
        cb.Content = label;
        cb.Click += OnSchemeCheckClick;
        return cb;
    }

    /// <summary>三态计算：全选 → true；全空 → false；部分 → null。</summary>
    private bool? ComputeStrategyTriState(MixStrategy strategy)
    {
        if (strategy.Schemes.Count == 0) return false;
        var selectedInStrategy = strategy.Schemes.Count(s => _selectedSchemeIds.Contains(s.Id));
        if (selectedInStrategy == 0) return false;
        if (selectedInStrategy == strategy.Schemes.Count) return true;
        return null;
    }

    private void OnStrategyCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not MixStrategy strategy) return;
        // WPF IsThreeState=true 默认点击循环：true → false → null → true
        // 我们要简化为：半选(null) → 全选(true)；全选(true) → 全空(false)；全空(false) → 全选(true)
        // 此时 cb.IsChecked 已经是 WPF 帮我们切到下一帧的值，需要根据上一帧推断目标态。
        // 简化策略：只要不是全选就置全选，否则清空。
        var newState = ComputeStrategyTriState(strategy) != true; // 上一帧非全选 → 全选；上一帧全选 → 清空
        cb.IsChecked = newState;

        if (newState)
        {
            foreach (var s in strategy.Schemes) _selectedSchemeIds.Add(s.Id);
        }
        else
        {
            foreach (var s in strategy.Schemes) _selectedSchemeIds.Remove(s.Id);
        }
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    private void OnSchemeCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.Tag is not ValueTuple<MixScheme, MixStrategy> tup) return;
        var (scheme, _) = tup;
        if (cb.IsChecked == true)
        {
            _selectedSchemeIds.Add(scheme.Id);
        }
        else
        {
            _selectedSchemeIds.Remove(scheme.Id);
        }
        RefreshSelectionPanel(); // 刷父三态
        UpdateExportButtonText();
    }

    private void OnSelectAllSchemesClick(object sender, RoutedEventArgs e)
    {
        _selectedSchemeIds.Clear();
        foreach (var strategy in _schemeVM.Strategies)
            foreach (var s in strategy.Schemes) _selectedSchemeIds.Add(s.Id);
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    private void OnInvertSchemesClick(object sender, RoutedEventArgs e)
    {
        var allIds = _schemeVM.Strategies.SelectMany(st => st.Schemes).Select(s => s.Id).ToHashSet();
        var newSel = allIds.Except(_selectedSchemeIds).ToList();
        _selectedSchemeIds.Clear();
        foreach (var id in newSel) _selectedSchemeIds.Add(id);
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    private void OnClearSchemesClick(object sender, RoutedEventArgs e)
    {
        _selectedSchemeIds.Clear();
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    private void UpdateSelectedCountLabel()
    {
        var total = _schemeVM.Strategies.Sum(st => st.Schemes.Count);
        SelectedCountLabel.Text = $"已选 {_selectedSchemeIds.Count}/{total}";
    }

    /// <summary>
    /// 更新唯一导出按钮的文案 / 可用性 / 说明（对齐 macOS 单按钮设计）。
    /// 条数 = 有配音改写版时按「原声 + 各改写版」排列组合的总条数；没有配音改写版时 = 选中方案数
    /// （每个分镜只有「原声」1 档 → 组合数恒 1 → 每方案 1 条，正是原「普通导出」）。
    /// </summary>
    private void UpdateExportButtonText()
    {
        if (ExportButton is null) return;
        var selected = _schemeVM.Schemes.Where(s => _selectedSchemeIds.Contains(s.Id)).ToList();
        if (selected.Count == 0)
        {
            ExportButton.Content = "请先选择方案";
            ExportButton.IsEnabled = false;
            ExportButton.ToolTip = "请先勾选要导出的方案";
            if (ExportHintText is not null) ExportHintText.Text = string.Empty;
            UpdateSelectedCountLabel();
            return;
        }

        var anyDub = HasDubVariants(selected);
        var total = anyDub
            ? selected.Sum(s => Math.Min(
                Services.Dubbing.SchemeComboPlanner.FeasibleCount(s),
                Services.Dubbing.SchemeComboPlanner.MaxCombos))
            : selected.Count;

        ExportButton.Content = $"📦  导出（共 {total} 条）";
        ExportButton.IsEnabled = !_isExporting;   // #11：导出中不重启用（防重入）
        ExportButton.ToolTip = anyDub
            ? "按每个分镜的「原声 + 各改写版」全部排列组合，串行逐条生成差异化配音视频"
            : "每个选中的方案导出 1 条成片（分镜用原声），串行逐条生成";
        if (ExportHintText is not null)
        {
            ExportHintText.Text = anyDub
                ? $"选中 {selected.Count} 个方案 · 检测到配音改写版，将按「原声 + 各改写版」排列组合生成 {total} 条差异化视频"
                : $"选中 {selected.Count} 个方案 · 各生成 1 条成片（分镜用原声）；想要多条不同配音的版本，先去分镜库给分镜做配音改写";
        }
        UpdateSelectedCountLabel();
    }

    /// <summary>选中方案里是否存在可参与组合的配音改写版（决定走组合导出还是普通导出）。</summary>
    private static bool HasDubVariants(IEnumerable<MixScheme> selected) =>
        selected.Any(s => s.OrderedSegments.Any(
            ss => ss.Segment is { IsVoiceLocked: false } seg && seg.EffectiveDubVariants.Count > 0));

    /// <summary>
    /// 唯一导出入口（对齐 macOS 单按钮）：内部按「有没有配音改写版」分流到原有两条已验证的路径 ——
    /// 有 → 配音组合导出（排列组合逐条出片）；没有 → 普通方案导出（每方案 1 条原声）。
    /// 用户无需在两个按钮间选择；行为与原来各自的按钮完全一致。
    /// </summary>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_isExporting) return;
        var selected = _schemeVM.Schemes.Where(s => _selectedSchemeIds.Contains(s.Id)).ToList();
        if (selected.Count == 0) return;

        // #22：导出开始前再校验一次所选 BGM 文件还在（可能被外部删了）。
        if (CurrentBgmPath is { } bgm && !File.Exists(bgm))
        {
            Shared.MixCutDialog.Error(
                Window.GetWindow(this),
                "背景音乐文件不存在",
                "所选背景音乐文件已不存在，请到「BGM 库」确认后重新选择。");
            await RefreshBgmListAsync();
            return;
        }

        if (HasDubVariants(selected))
        {
            await ExportDubCombosAsync();
        }
        else
        {
            await ExportSchemesAsync();
        }
    }

    private ExportConfig BuildConfig() => new()
    {
        Resolution = (ExportResolution)Math.Max(0, ResolutionCombo.SelectedIndex),
        Codec = (ExportCodec)Math.Max(0, CodecCombo.SelectedIndex),
        Quality = (ExportQuality)Math.Max(0, QualityCombo.SelectedIndex),
        // #22：null = 保留原 BGM（现有行为逐字节不变）
        BgmPath = CurrentBgmPath,
        BgmVolume = _settings.BgmVolumePercent / 100.0,
    };

    // ---- 批量导出（v0.6.0 起单方案区块已删除，统一走筛选导出） ----

    /// <summary>普通方案导出（每方案 1 条，分镜用原声）。由 <see cref="OnExportClick"/> 在无配音改写版时调用。</summary>
    private async System.Threading.Tasks.Task ExportSchemesAsync()
    {
        // 防重入（权威守卫，不只靠按钮 IsEnabled）：正在导出（含被配音组合导出占用）时忽略，
        // 否则两批导出会共享并互相 Dispose 同一个 _exportCts，令在跑的 ffmpeg 撞 ObjectDisposedException 崩溃。
        if (_isExporting) return;
        // 用户在异步导出过程中可能切项目 / 改选择，snapshot 一份避免被并发改写。
        var snapshotIds = new HashSet<Guid>(_selectedSchemeIds);
        var schemes = _schemeVM.Schemes.Where(s => snapshotIds.Contains(s.Id)).ToList();
        if (schemes.Count == 0)
        {
            return;
        }

        // #22：选了 BGM 时先确认（说明替换行为 + 首次分离耗时），普通导出原本没有确认弹窗、行为不变。
        var bgmMode = CurrentBgmPath is not null;
        if (bgmMode)
        {
            var ok = Shared.MixCutDialog.Confirm(
                Window.GetWindow(this),
                "将替换成片背景音乐",
                "所有成片将去除各分镜原 BGM、只保留口播，再铺上所选音乐"
                + "（比成片长则截断，短则循环，结尾 1 秒淡出）。\n"
                + "首次会对相关视频做人声分离，耗时较长。",
                confirmText: "继续导出", cancelText: "取消");
            if (!ok) return;
        }

        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        // QW-3：跨会话记忆上次导出目录，免去每次重新点开 5 层目录。
        if (!string.IsNullOrEmpty(_settings.LastExportDirForSchemes)
            && Directory.Exists(_settings.LastExportDirForSchemes))
        {
            dialog.InitialDirectory = _settings.LastExportDirForSchemes;
        }
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var outputDir = dialog.FolderName;
        _lastOutputDir = outputDir;
        _settings.LastExportDirForSchemes = outputDir;   // QW-3：记住本次选择，下次默认定位到这里

        // 准备所有导出任务：过滤掉无效方案 + 计算文件名。
        var config = BuildConfig();
        var tasks = new List<(MixScheme Scheme, ExportInput Input, string OutputPath)>();
        var skipped = 0;
        var segmentSkipped = 0;
        for (var i = 0; i < schemes.Count; i++)
        {
            var scheme = schemes[i];
            var input = ExportInput.FromScheme(scheme, useVocalsAudio: bgmMode);
            if (input is null)
            {
                skipped++;
                continue;
            }
            segmentSkipped += input.SkippedCount; // 方案内源文件丢失的分镜数，累计后告知用户
            // 命名：策略名_变体序号_方案名.mp4
            var strategyName = SanitizeFileName(scheme.Strategy?.Name ?? "未分组");
            var schemeName = SanitizeFileName(scheme.Name);
            var fileName = $"{strategyName}_{scheme.VariationIndex:D2}_{schemeName}.mp4";
            tasks.Add((scheme, input, Path.Combine(outputDir, fileName)));
        }

        if (tasks.Count == 0)
        {
            Shared.MixCutDialog.Error(
                Window.GetWindow(this),
                "没有可导出的方案",
                "选中的方案都用不了 —— 它们引用的视频文件已被移动、重命名或删除。\n\n"
                + "请回到「素材导入」确认原始视频还在原位置，或重新导入素材后再生成方案。");
            return;
        }

        if (segmentSkipped > 0)
        {
            Components.ToastService.Show(
                $"有 {segmentSkipped} 个分镜的源文件已丢失，导出时已自动跳过", Components.ToastStyle.Warning);
        }

        // QW-4：导出前检查文件冲突。目录里已存在同名 .mp4（上次导出 / 别的项目）会被静默覆盖，
        // 用户数小时的渲染结果可能瞬间消失。列出冲突文件让用户确认后再继续。
        var conflicts = tasks.Where(t => File.Exists(t.OutputPath)).ToList();
        if (conflicts.Count > 0)
        {
            var preview = string.Join("\n",
                conflicts.Take(5).Select(c => "• " + Path.GetFileName(c.OutputPath)));
            if (conflicts.Count > 5)
            {
                preview += $"\n…还有 {conflicts.Count - 5} 个";
            }
            // 覆盖可能毁掉用户几小时的渲染成果 —— 按破坏性操作对待（红色主按钮 + 默认焦点在取消）。
            var confirmed = Shared.MixCutDialog.Confirm(
                Window.GetWindow(this),
                $"覆盖已存在的 {conflicts.Count} 个文件？",
                $"这些文件会被新导出的内容替换：\n\n{preview}",
                confirmText: $"覆盖 {conflicts.Count} 个", cancelText: "取消", destructive: true, icon: "⚠");
            if (!confirmed)
            {
                return;
            }
        }

        await RunSchemeBatchWithPrecheckAsync(tasks, config, outputDir, skipped);
    }

    // ---- #22：BGM 模式的人声分离预检 ----

    /// <summary>预检的分离对象：原视频路径 + 内容哈希 + 展示名。</summary>
    private sealed record VocalsSource(string VideoPath, string Hash, string Name);

    /// <summary>从方案集合收集全部源视频（去重）。</summary>
    private static List<VocalsSource> CollectVocalsSources(IEnumerable<MixScheme> schemes)
    {
        var seen = new Dictionary<string, VocalsSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in schemes)
        {
            foreach (var ss in scheme.OrderedSegments)
            {
                var video = ss.Segment?.Video;
                if (video is null) continue;
                var key = string.IsNullOrEmpty(video.ContentHash) ? "path:" + video.LocalPath : video.ContentHash;
                if (!seen.ContainsKey(key))
                {
                    seen[key] = new VocalsSource(video.LocalPath, video.ContentHash ?? string.Empty, video.Name);
                }
            }
        }
        return seen.Values.ToList();
    }

    /// <summary>从配音导出任务收集需要 vocals 的源视频（specs 里的 VocalsSlice，去重）。重试路径用。</summary>
    private static List<VocalsSource> CollectVocalsSourcesFromDubTasks(
        IEnumerable<(string Name, Services.Dubbing.DubExportInput Input, string Item3)> tasks)
    {
        var seen = new Dictionary<string, VocalsSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
        {
            foreach (var spec in t.Input.Segments)
            {
                if (spec.Vocals is not { } vs) continue;
                var key = string.IsNullOrEmpty(vs.SourceVideoHash) ? "path:" + vs.SourceVideoPath : vs.SourceVideoHash;
                if (!seen.ContainsKey(key))
                {
                    seen[key] = new VocalsSource(
                        vs.SourceVideoPath, vs.SourceVideoHash, Path.GetFileName(vs.SourceVideoPath));
                }
            }
        }
        return seen.Values.ToList();
    }

    /// <summary>
    /// 逐个确保源视频的 vocals.wav 存在（已分离秒回缓存；未分离现场跑 demucs，进度「人声分离 i/n：视频名」）。
    /// 返回失败清单（hash/path key → 人话原因）+ 是否被取消。
    /// ⚠ 取消时调用方必须直接收尾返回，禁止带着「部分视频未检查」的状态继续组装（issue #22 红线）。
    /// </summary>
    private async System.Threading.Tasks.Task<(Dictionary<string, string> Failed, bool Canceled)>
        EnsureVocalsAsync(IReadOnlyList<VocalsSource> sources, CancellationToken token)
    {
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sources.Count; i++)
        {
            if (token.IsCancellationRequested) return (failed, true);
            var s = sources[i];
            var key = string.IsNullOrEmpty(s.Hash) ? "path:" + s.VideoPath : s.Hash;
            ProgressStatusText.Text = $"人声分离 {i + 1}/{sources.Count}：{s.Name}";
            ProgressDetailText.Text = string.Empty;
            ProgressBar.Value = (double)i / Math.Max(1, sources.Count);

            if (string.IsNullOrEmpty(s.Hash))
            {
                failed[key] = "无法识别视频文件指纹，请到「素材导入」重新导入该视频";
                continue;
            }
            if (string.IsNullOrEmpty(s.VideoPath) || !File.Exists(s.VideoPath))
            {
                failed[key] = "视频文件已不存在（被移动、重命名或删除）";
                continue;
            }
            try
            {
                var idx = i;
                await _vocalSep.SeparateAsync(
                    s.VideoPath, s.Hash,
                    onProgress: new Progress<string>(txt => ProgressDetailText.Text = txt),
                    onPercent: new Progress<double>(p =>
                    {
                        var frac = p > 1.5 ? p / 100.0 : p;   // 兼容 0-1 / 0-100 两种刻度
                        ProgressBar.Value = (idx + Math.Clamp(frac, 0, 1)) / Math.Max(1, sources.Count);
                    }),
                    ct: token);
            }
            catch (OperationCanceledException)
            {
                return (failed, true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[BgmDiag] 预检人声分离失败 video={Video}", s.Name);
                failed[key] = ex is Services.Dubbing.DubException
                    ? ex.Message
                    : MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex);
            }
        }
        return (failed, false);
    }

    /// <summary>
    /// 方案批量导出的 BGM 预检外壳：没选 BGM 直接透传（行为不变）；选了则先逐视频保证 vocals.wav，
    /// 分离失败的视频 → 涉及它的方案整体进失败清单（不导、不静默跳过），其余照常导出。
    /// 「只重试失败的」也走本外壳 —— 重试会重新预检（给分离失败一个自愈机会）。
    /// </summary>
    private async System.Threading.Tasks.Task RunSchemeBatchWithPrecheckAsync(
        List<(MixScheme Scheme, ExportInput Input, string OutputPath)> tasks,
        ExportConfig config, string outputDir, int skipped = 0)
    {
        if (config.BgmPath is null || tasks.Count == 0)
        {
            await RunSchemeBatchAsync(tasks, config, outputDir, skipped);
            return;
        }
        if (_isExporting) return;

        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        _isExporting = true;
        ExportButton.IsEnabled = false;
        CancelExportButton.IsEnabled = true;
        CompletePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressSection.Visibility = Visibility.Visible;
        ProgressTitle.Text = "替换背景音乐前的准备（人声分离）";

        (Dictionary<string, string> Failed, bool Canceled) pre;
        try
        {
            pre = await EnsureVocalsAsync(CollectVocalsSources(tasks.Select(t => t.Scheme)), token);
        }
        finally
        {
            _isExporting = false;   // 后续 RunSchemeBatchAsync 自己重新置位
        }
        if (pre.Canceled)
        {
            // 红线：预检中途取消 → 干净收尾返回，绝不带着「部分视频未检查」继续组装任务。
            ProgressSection.Visibility = Visibility.Collapsed;
            CancelExportButton.IsEnabled = false;
            UpdateExportButtonText();
            Components.ToastService.Show("已取消导出", Components.ToastStyle.Warning);
            return;
        }

        var runnable = new List<(MixScheme Scheme, ExportInput Input, string OutputPath)>();
        var preFailedTasks = new List<(MixScheme Scheme, ExportInput Input, string OutputPath)>();
        var preErrors = new List<string>();
        foreach (var t in tasks)
        {
            var bad = CollectVocalsSources(new[] { t.Scheme })
                .FirstOrDefault(s => pre.Failed.ContainsKey(
                    string.IsNullOrEmpty(s.Hash) ? "path:" + s.VideoPath : s.Hash));
            if (bad is null)
            {
                runnable.Add(t);
            }
            else
            {
                preFailedTasks.Add(t);
                var reason = pre.Failed[string.IsNullOrEmpty(bad.Hash) ? "path:" + bad.VideoPath : bad.Hash];
                preErrors.Add(
                    $"{t.Scheme.Name}: 视频「{bad.Name}」人声分离失败：{reason}。无法去除原 BGM，该方案未导出");
            }
        }

        await RunSchemeBatchAsync(runnable, config, outputDir, skipped, preErrors, preFailedTasks);
    }

    /// <summary>
    /// 执行一批方案导出（并发调度 + 进度 + 结果面板）。
    ///
    /// 单独抽出来是为了支持「只重试失败的 N 个」—— 失败后可以拿失败子集再调一次本方法，
    /// 而不必让用户把选方案、选目录、确认覆盖整个流程重走一遍（也避免把已成功的重复导一遍）。
    /// </summary>
    private async System.Threading.Tasks.Task RunSchemeBatchAsync(
        List<(MixScheme Scheme, ExportInput Input, string OutputPath)> tasks,
        ExportConfig config, string outputDir, int skipped = 0,
        List<string>? preErrors = null,
        List<(MixScheme Scheme, ExportInput Input, string OutputPath)>? preFailedTasks = null)
    {
        // #14（对齐 macOS v0.7.x）：所有导出一律串行，concurrency 恒为 1（一条一条导，避免占满机器）。
        var concurrency = Infrastructure.ConcurrencyPolicy.MaxExportConcurrency(tasks.Count);
        Serilog.Log.Information(
            "[ExportConcurrency] tasks={Tasks} concurrency={Concurrency} 说明={Explain}",
            tasks.Count, concurrency,
            Infrastructure.ConcurrencyPolicy.ExplainExportFormula());

        CompletePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressSection.Visibility = Visibility.Visible;
        ProgressTitle.Text = $"串行导出（共 {tasks.Count} 个 · 一条一条导）";
        ExportButton.IsEnabled = false;   // 导出期间禁用（防重入）

        // QW-11：每次导出新建取消令牌，「取消」按钮 / ESC 触发后整批 ffmpeg 立即收手。
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        CancelExportButton.IsEnabled = true;
        _isExporting = true;

        var success = 0;
        // #22：BGM 预检失败的方案作为「已失败」带入 —— 出现在失败清单里且可被「只重试失败的」重跑
        // （重试走 RunSchemeBatchWithPrecheckAsync 会重新预检分离）。
        var errors = new List<string>(preErrors ?? (IEnumerable<string>)Array.Empty<string>());
        // 失败任务本身也要留下来，否则「只重试失败的」无从下手（原来只存了错误文案字符串）。
        var failedTasks = new List<(MixScheme Scheme, ExportInput Input, string OutputPath)>(
            preFailedTasks ?? (IEnumerable<(MixScheme, ExportInput, string)>)Array.Empty<(MixScheme, ExportInput, string)>());
        var totalAll = tasks.Count + failedTasks.Count;   // 结果统计含预检失败的
        var completed = 0;
        var canceled = false;
        var currentTaskNames = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        var exportSw = System.Diagnostics.Stopwatch.StartNew();

        using var semaphore = new SemaphoreSlim(concurrency);
        var exportJobs = tasks.Select(async (task, slot) =>
        {
            await semaphore.WaitAsync(token);
            try
            {
                currentTaskNames[slot] = task.Scheme.Name;
                UpdateConcurrentProgress();

                await _exportService.ExportAsync(task.Input, task.OutputPath, config,
                    p => UpdateConcurrentProgress(p.Progress), token);
                Interlocked.Increment(ref success);
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消 —— 不计入失败，单独走「已取消」分支。
                canceled = true;
            }
            catch (Exception ex)
            {
                // 翻译成人话 + 可操作建议；原始 exit/stderr 已在 [ffmpeg-fail] 日志。
                var friendly = Services.Export.ExportErrorMessage.ToFriendly(ex);
                lock (errors)
                {
                    errors.Add($"{task.Scheme.Name}: {friendly}");
                    failedTasks.Add(task);
                }
            }
            finally
            {
                currentTaskNames.TryRemove(slot, out _);
                Interlocked.Increment(ref completed);
                UpdateConcurrentProgress();
                semaphore.Release();
            }
        });

        // semaphore.WaitAsync(token) 在取消后会抛 OperationCanceledException，
        // 用 Task.WhenAll 收集时整体也会抛，统一在这里兜成「已取消」状态。
        try
        {
            await Task.WhenAll(exportJobs);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        ProgressSection.Visibility = Visibility.Collapsed;
        CancelExportButton.IsEnabled = false;
        _isExporting = false;
        UpdateExportButtonText(); // ExportButton 文案/状态按当前选择数计算

        if (canceled)
        {
            Components.ToastService.Show(
                $"已取消导出（已完成 {success}/{tasks.Count} 个）", Components.ToastStyle.Warning);
        }
        else if (errors.Count == 0)
        {
            CompletePanel.Visibility = Visibility.Visible;
            CompleteText.Text = $"共导出 {success} 个视频" +
                                (skipped > 0 ? $"（跳过 {skipped} 个无效方案）" : string.Empty) +
                                $"\n输出目录：{outputDir}";
            Components.ToastService.Show($"✓ 批量导出完成 {success} 个", Components.ToastStyle.Success);
        }
        else
        {
            ShowResultPanel(
                success, totalAll, "个", errors, outputDir,
                // #22：重试走预检外壳 —— 分离失败的方案重试时会重新分离，而不是原样再失败一遍。
                retry: failedTasks.Count == 0 ? null : () => RunSchemeBatchWithPrecheckAsync(failedTasks, config, outputDir));
            Components.ToastService.Show(
                success > 0
                    ? $"⚠ 部分失败：成功 {success}/{totalAll}"
                    : "导出全部失败",
                success > 0 ? Components.ToastStyle.Warning : Components.ToastStyle.Error);
        }

        void UpdateConcurrentProgress(double sub = 0)
        {
            var done = completed;
            var inProgress = string.Join("、", currentTaskNames.Values.Take(2));
            if (currentTaskNames.Count > 2)
            {
                inProgress += $" 等 {currentTaskNames.Count} 个";
            }
            // 串行导出：总进度 = (已完成条数 + 当前这条 ffmpeg 的百分比) / 总条数 —— 大文件编码时进度条平滑推进，不再假死跳格。
            var sc = Math.Clamp(sub, 0, 1);
            var frac = done >= tasks.Count ? 1.0 : (done + sc) / tasks.Count;
            var eta = EstimateEta(exportSw.Elapsed, frac);
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = frac;
                ProgressStatusText.Text = $"串行导出中… {done}/{tasks.Count}" + eta;
                ProgressDetailText.Text = string.IsNullOrEmpty(inProgress)
                    ? string.Empty : "进行中：" + inProgress;
            });
        }
    }

    /// <summary>失败重试动作（由结果面板的「只重试失败的 N 个」按钮触发）。</summary>
    private Func<System.Threading.Tasks.Task>? _retryFailedAction;

    /// <summary>结果面板对应的输出目录（「打开输出目录」按钮用）。</summary>
    private string? _resultOutputDir;

    /// <summary>
    /// 渲染导出结果面板（有失败时）。
    ///
    /// 标题按结果分级 —— 原本写死「导出失败」，5 个里成功 3 个也这么说，
    /// 用户会以为一个都没出来，转头去重导全部（把已成功的又导一遍）。
    /// </summary>
    private void ShowResultPanel(
        int success, int total, string unit, List<string> errors, string outputDir,
        Func<System.Threading.Tasks.Task>? retry)
    {
        var partial = success > 0;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorPanel.Background = new SolidColorBrush(partial
            ? Color.FromRgb(0xFD, 0xF3, 0xE2)      // 琥珀：部分完成
            : Color.FromRgb(0xFD, 0xE2, 0xE2));    // 红：全失败
        ErrorPanel.BorderBrush = new SolidColorBrush(partial
            ? Color.FromRgb(0xF5, 0xD7, 0xA8) : Color.FromRgb(0xF5, 0xB7, 0xB7));
        ErrorBadge.Background = new SolidColorBrush(partial
            ? Color.FromRgb(0xC0, 0x6F, 0x00) : Color.FromRgb(0xD3, 0x3A, 0x3A));
        ErrorBadgeIcon.Text = partial ? "!" : "✕";

        var fore = new SolidColorBrush(partial
            ? Color.FromRgb(0x7A, 0x58, 0x00) : Color.FromRgb(0xA1, 0x26, 0x26));
        ErrorTitleText.Foreground = fore;
        ErrorText.Foreground = fore;
        RetryFailedButton.Foreground = fore;
        RetryFailedButton.BorderBrush = fore;
        OpenOutputAfterErrorButton.Foreground = fore;

        var failedCount = total - success;
        ErrorTitleText.Text = partial
            ? $"部分完成：{success}/{total} {unit}已导出，{failedCount} {unit}失败"
            : $"导出失败（{total} {unit}全部未完成）";
        ErrorText.Text = string.Join("\n", errors.Take(5))
                         + (errors.Count > 5 ? $"\n…还有 {errors.Count - 5} 个错误" : string.Empty);

        _retryFailedAction = retry;
        _resultOutputDir = outputDir;
        RetryFailedButton.Content = $"只重试失败的 {failedCount} {unit}";
        RetryFailedButton.Visibility = retry is null ? Visibility.Collapsed : Visibility.Visible;
        // 成功过一部分才有必要给「打开输出目录」—— 全失败时目录里什么都没有。
        OpenOutputAfterErrorButton.Visibility = partial ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRetryFailedClick(object sender, RoutedEventArgs e)
    {
        var retry = _retryFailedAction;
        if (retry is null || _isExporting) return;
        _retryFailedAction = null;
        ErrorPanel.Visibility = Visibility.Collapsed;
        try
        {
            await retry();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[ExportDiag] 重试失败任务时出错");
            Components.ToastService.Show(
                "重试没能开始：" + MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex),
                Components.ToastStyle.Error);
        }
    }

    private void OnOpenOutputAfterErrorClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_resultOutputDir)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _resultOutputDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[ExportDiag] 打开输出目录失败");
            Components.ToastService.Show(
                "打不开输出目录，你可以手动到这个位置查看：" + _resultOutputDir,
                Components.ToastStyle.Warning);
        }
    }

    /// <summary>按已用时长 + 已完成比例估算剩余时间，返回「 · 预计剩余 mm:ss」；进度过小时不估（不准）。</summary>
    private static string EstimateEta(TimeSpan elapsed, double fraction)
    {
        if (fraction <= 0.03 || fraction >= 1.0 || elapsed.TotalSeconds < 2) return string.Empty;
        var remainSec = elapsed.TotalSeconds * (1 - fraction) / fraction;
        if (remainSec < 1 || remainSec > 24 * 3600) return string.Empty;
        var ts = TimeSpan.FromSeconds(remainSec);
        var text = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes}:{ts.Seconds:D2}";
        return $" · 预计剩余 {text}";
    }

    // ---- 配音组合导出（v0.5.0）：跨选中方案笛卡尔积展开成 N 条，逐条出片 ----

    /// <summary>配音组合导出（按「原声 + 各改写版」排列组合逐条出片）。由 <see cref="OnExportClick"/> 在有配音改写版时调用。</summary>
    private async System.Threading.Tasks.Task ExportDubCombosAsync()
    {
        if (_isExporting) return;   // 防重入：与方案导出并发会互相 Dispose _exportCts 致崩（见 OnExportAllClick 注释）
        var snapshotIds = new HashSet<Guid>(_selectedSchemeIds);
        var schemes = _schemeVM.Schemes.Where(s => snapshotIds.Contains(s.Id)).ToList();
        if (schemes.Count == 0) return;

        // 展开每个方案的全部配音组合（笛卡尔积，每方案封顶 MaxCombos）。
        var bgmMode = CurrentBgmPath is not null;   // #22：BGM 模式 → 原声段用 vocals、配音段不混 bgm.wav
        var jobs = new List<(string Name, Services.Dubbing.DubExportInput Input, string FileBase)>();
        var truncatedSchemes = 0;
        foreach (var scheme in schemes)
        {
            var plan = Services.Dubbing.SchemeComboPlanner.Build(scheme);
            if (plan.Truncated) truncatedSchemes++;
            var strategyName = SanitizeFileName(scheme.Strategy?.Name ?? "未分组");
            var schemeName = SanitizeFileName(scheme.Name);
            foreach (var combo in plan.Combos)
            {
                var input = Services.Dubbing.DubExportInput.From(scheme, combo.Choices, useVocalsAudio: bgmMode);
                if (input is null) continue;
                jobs.Add(($"{scheme.Name} {combo.NameSuffix}", input, $"{strategyName}_{schemeName}{SanitizeFileName(combo.NameSuffix)}"));
            }
        }

        if (jobs.Count == 0)
        {
            Shared.MixCutDialog.Error(
                Window.GetWindow(this),
                "还没有配音组合可以导出",
                "选中的方案里没有任何配音变体，排列组合后为空。\n\n"
                + "请先到「分镜库」选中分镜，用「克隆并改写配音」生成几版配音，再回到这里导出组合。");
            return;
        }

        // 确认弹窗（PRD §7.2）：先告知将生成多少条；#22 选了 BGM 时说明替换行为 + 首次分离耗时。
        var truncNote = truncatedSchemes > 0 ? $"\n（有 {truncatedSchemes} 个方案组合数超上限，已按每方案前 {Services.Dubbing.SchemeComboPlanner.MaxCombos} 条截取）" : "";
        var bgmNote = bgmMode
            ? "\n已选择替换背景音乐：成片将去除原 BGM、只保留口播，再铺上所选音乐（长截断/短循环/结尾 1 秒淡出）；"
              + "首次会对相关视频做人声分离，耗时较长。"
            : "";
        var confirmed = Shared.MixCutDialog.Confirm(
            Window.GetWindow(this),
            $"将从 {schemes.Count} 个方案生成 {jobs.Count} 条视频",
            "画面保持不变，按每个分镜的「原声 + 各改写版」做全排列组合。\n"
            + $"生成过程串行进行（一条一条导，不占满机器），期间可随时取消。{truncNote}{bgmNote}",
            confirmText: $"生成 {jobs.Count} 条", cancelText: "取消");
        if (!confirmed) return;

        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        if (!string.IsNullOrEmpty(_settings.LastExportDirForSchemes) && Directory.Exists(_settings.LastExportDirForSchemes))
        {
            dialog.InitialDirectory = _settings.LastExportDirForSchemes;
        }
        if (dialog.ShowDialog() != true) return;
        var outputDir = dialog.FolderName;
        _lastOutputDir = outputDir;
        _settings.LastExportDirForSchemes = outputDir;

        var config = BuildConfig();
        // 落地输出路径 + 文件名冲突检查。
        var tasks = jobs.Select(j => (j.Name, j.Input, Path.Combine(outputDir, j.FileBase + ".mp4"))).ToList();
        var conflicts = tasks.Where(t => File.Exists(t.Item3)).ToList();
        if (conflicts.Count > 0)
        {
            var preview = string.Join("\n", conflicts.Take(5).Select(c => "• " + Path.GetFileName(c.Item3)));
            if (conflicts.Count > 5) preview += $"\n…还有 {conflicts.Count - 5} 个";
            // 覆盖会毁掉之前的渲染成果 —— 按破坏性操作对待（红色主按钮 + 默认焦点在取消）。
            if (!Shared.MixCutDialog.Confirm(
                    Window.GetWindow(this),
                    $"覆盖已存在的 {conflicts.Count} 个文件？",
                    $"输出目录里已有同名文件，它们会被这次导出的内容替换：\n\n{preview}",
                    confirmText: $"覆盖 {conflicts.Count} 个", cancelText: "取消", destructive: true, icon: "⚠"))
            {
                return;
            }
        }

        await RunDubBatchWithPrecheckAsync(tasks, config, outputDir);
    }

    /// <summary>
    /// 配音组合导出的 BGM 预检外壳（对齐 <see cref="RunSchemeBatchWithPrecheckAsync"/>）：
    /// 分离失败的视频 → 涉及它的全部组合进失败清单（逐视频聚合成一条人话），其余照常导出；
    /// 预检取消 → 干净收尾；重试重新预检。
    /// </summary>
    private async System.Threading.Tasks.Task RunDubBatchWithPrecheckAsync(
        List<(string Name, MixCut.Services.Dubbing.DubExportInput Input, string Item3)> tasks,
        ExportConfig config, string outputDir)
    {
        if (config.BgmPath is null || tasks.Count == 0)
        {
            await RunDubBatchAsync(tasks, config, outputDir);
            return;
        }
        if (_isExporting) return;

        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        _isExporting = true;
        ExportButton.IsEnabled = false;
        CancelExportButton.IsEnabled = true;
        CompletePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressSection.Visibility = Visibility.Visible;
        ProgressTitle.Text = "替换背景音乐前的准备（人声分离）";

        (Dictionary<string, string> Failed, bool Canceled) pre;
        try
        {
            pre = await EnsureVocalsAsync(CollectVocalsSourcesFromDubTasks(tasks), token);
        }
        finally
        {
            _isExporting = false;
        }
        if (pre.Canceled)
        {
            ProgressSection.Visibility = Visibility.Collapsed;
            CancelExportButton.IsEnabled = false;
            UpdateExportButtonText();
            Components.ToastService.Show("已取消导出", Components.ToastStyle.Warning);
            return;
        }

        string KeyOf(Services.Dubbing.VocalsSlice vs) =>
            string.IsNullOrEmpty(vs.SourceVideoHash) ? "path:" + vs.SourceVideoPath : vs.SourceVideoHash;

        var runnable = new List<(string Name, MixCut.Services.Dubbing.DubExportInput Input, string Item3)>();
        var preFailedTasks = new List<(string Name, MixCut.Services.Dubbing.DubExportInput Input, string Item3)>();
        // 逐视频聚合失败文案：「视频 X 分离失败 → 涉及 N 条组合均未导出」，不逐条刷屏。
        var failCountByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failNameByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
        {
            var badKey = t.Input.Segments
                .Where(s => s.Vocals is not null)
                .Select(s => KeyOf(s.Vocals!))
                .FirstOrDefault(k => pre.Failed.ContainsKey(k));
            if (badKey is null)
            {
                runnable.Add(t);
            }
            else
            {
                preFailedTasks.Add(t);
                failCountByKey[badKey] = failCountByKey.TryGetValue(badKey, out var n) ? n + 1 : 1;
                if (!failNameByKey.ContainsKey(badKey))
                {
                    var vs = t.Input.Segments.First(s => s.Vocals is not null && KeyOf(s.Vocals!) == badKey).Vocals!;
                    failNameByKey[badKey] = Path.GetFileName(vs.SourceVideoPath);
                }
            }
        }
        var preErrors = failCountByKey.Select(kv =>
            $"视频「{failNameByKey[kv.Key]}」人声分离失败：{pre.Failed[kv.Key]}。" +
            $"无法去除原 BGM，涉及的 {kv.Value} 条组合均未导出").ToList();

        await RunDubBatchAsync(runnable, config, outputDir, preErrors, preFailedTasks);
    }

    /// <summary>执行一批配音组合导出。抽出来同样是为了支持「只重试失败的 N 条」。</summary>
    private async System.Threading.Tasks.Task RunDubBatchAsync(
        List<(string Name, MixCut.Services.Dubbing.DubExportInput Input, string Item3)> tasks,
        ExportConfig config, string outputDir,
        List<string>? preErrors = null,
        List<(string Name, MixCut.Services.Dubbing.DubExportInput Input, string Item3)>? preFailedTasks = null)
    {
        // 缓存仅覆盖本次批次，成功、失败或取消离开方法时统一清理。
        using var renderCache = new MixCut.Services.Dubbing.DubExportService.BatchRenderCache();
        // #14（对齐 macOS v0.7.x）：配音组合导出同样一律串行，concurrency 恒为 1。
        var concurrency = Infrastructure.ConcurrencyPolicy.MaxExportConcurrency(tasks.Count);

        CompletePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressSection.Visibility = Visibility.Visible;
        ProgressTitle.Text = $"串行导出配音组合（共 {tasks.Count} 条 · 一条一条导）";
        ExportButton.IsEnabled = false;   // 导出期间禁用（防重入）

        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        CancelExportButton.IsEnabled = true;
        _isExporting = true;

        var success = 0;
        // #22：BGM 预检失败的组合作为「已失败」带入（重试走预检外壳会重新分离）。
        var errors = new List<string>(preErrors ?? (IEnumerable<string>)Array.Empty<string>());
        var failedTasks = new List<(string Name, MixCut.Services.Dubbing.DubExportInput Input, string Item3)>(
            preFailedTasks ?? (IEnumerable<(string, MixCut.Services.Dubbing.DubExportInput, string)>)
                Array.Empty<(string, MixCut.Services.Dubbing.DubExportInput, string)>());
        var totalAll = tasks.Count + failedTasks.Count;
        var completed = 0;
        var canceled = false;
        var current = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        var exportSw = System.Diagnostics.Stopwatch.StartNew();

        using var semaphore = new SemaphoreSlim(concurrency);
        var exportJobs = tasks.Select(async (task, slot) =>
        {
            await semaphore.WaitAsync(token);
            try
            {
                current[slot] = task.Name;
                Report();
                await _dubExport.ExportAsync(task.Input, task.Item3, config, p => Report(p.Progress), token, renderCache);
                Interlocked.Increment(ref success);
            }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception ex)
            {
                // §红线：ex.Message 对 FFmpegException 含 "exit {code}: {stderr}"，绝不能直给用户 —— 翻成人话。
                lock (errors)
                {
                    errors.Add($"{task.Name}: {MixCut.Services.Export.ExportErrorMessage.ToFriendly(ex)}");
                    failedTasks.Add(task);
                }
            }
            finally
            {
                current.TryRemove(slot, out _);
                Interlocked.Increment(ref completed);
                Report();
                semaphore.Release();
            }
        });

        try { await Task.WhenAll(exportJobs); }
        catch (OperationCanceledException) { canceled = true; }

        ProgressSection.Visibility = Visibility.Collapsed;
        CancelExportButton.IsEnabled = false;
        _isExporting = false;
        UpdateExportButtonText();

        if (canceled)
        {
            Components.ToastService.Show($"已取消（已完成 {success}/{tasks.Count} 条）", Components.ToastStyle.Warning);
        }
        else if (errors.Count == 0)
        {
            CompletePanel.Visibility = Visibility.Visible;
            CompleteText.Text = $"共导出 {success} 条配音视频\n输出目录：{outputDir}";
            Components.ToastService.Show($"✓ 配音导出完成 {success} 条", Components.ToastStyle.Success);
        }
        else
        {
            ShowResultPanel(
                success, totalAll, "条", errors, outputDir,
                // #22：重试走预检外壳 —— 分离失败的组合重试时会重新分离。
                retry: failedTasks.Count == 0 ? null : () => RunDubBatchWithPrecheckAsync(failedTasks, config, outputDir));
            Components.ToastService.Show(success > 0 ? $"⚠ 部分失败：成功 {success}/{totalAll}" : "配音导出全部失败",
                success > 0 ? Components.ToastStyle.Warning : Components.ToastStyle.Error);
        }

        void Report(double sub = 0)
        {
            var done = completed;
            var inProgress = string.Join("、", current.Values.Take(2));
            if (current.Count > 2) inProgress += $" 等 {current.Count} 个";
            var sc = Math.Clamp(sub, 0, 1);
            var frac = done >= tasks.Count ? 1.0 : (done + sc) / tasks.Count;
            var eta = EstimateEta(exportSw.Elapsed, frac);
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = frac;
                ProgressStatusText.Text = $"串行导出中… {done}/{tasks.Count}" + eta;
                ProgressDetailText.Text = string.IsNullOrEmpty(inProgress) ? "" : "进行中：" + inProgress;
            });
        }
    }

    /// <summary>QW-11：取消批量导出。按钮点击或 ESC 触发，整批正在跑的 ffmpeg 收到 token 后停。</summary>
    private void OnCancelExportClick(object sender, RoutedEventArgs e)
    {
        if (_exportCts is null || _exportCts.IsCancellationRequested)
        {
            return;
        }
        CancelExportButton.IsEnabled = false;
        ProgressStatusText.Text = "正在取消…";
        Serilog.Log.Information("[ExportCancel] 用户取消批量导出");
        _exportCts.Cancel();
    }

    private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastOutputDir) || !Directory.Exists(_lastOutputDir))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe", Arguments = _lastOutputDir, UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "MixCut" : name;
    }
}
