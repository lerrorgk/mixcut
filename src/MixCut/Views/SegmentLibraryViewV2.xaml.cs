using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using MixCut.Models;
using MixCut.ViewModels;
using MixCut.ViewModels.Cards;

namespace MixCut.Views;

/// <summary>
/// SegmentLibraryView V2 —— MVVM 数据驱动版。
/// 关键设计：DataTemplate 默认轻量（只放 Image + 占位），hover 才动态挂 InlineVideoPlayer。
/// 这样 100 张卡片不会有 100 个 MediaElement 同时存在。
/// </summary>
public partial class SegmentLibraryViewV2 : UserControl, IProjectView
{
    private readonly SegmentLibraryViewModel _vm;
    private readonly Services.Export.VariantBatchExportService _variantExport;
    private readonly Utilities.AppSettings _settings;
    private readonly IServiceProvider _services;
    private Project? _currentProject;

    /// <summary>空态「去导入素材」用的导航回调（由 MainWindow 注入）。</summary>
    private readonly Action<NavigationItem>? _navigateTo;

    public SegmentLibraryViewV2(
        SegmentLibraryViewModel vm,
        Services.Export.VariantBatchExportService variantExport,
        Utilities.AppSettings settings,
        IServiceProvider services,
        Action<NavigationItem>? navigateTo = null)
    {
        _vm = vm;
        _variantExport = variantExport;
        _settings = settings;
        _services = services;
        _navigateTo = navigateTo;
        InitializeComponent();
        DataContext = _vm;

        BuildTypeChips();
        _vm.SelectionChanged += OnSelectionChanged;
        // 右键单删 / Ctrl+Z 恢复后，VM 广播此事件，View 刷新统计 / 类型 chip / 空态（VM 已自行 RebuildGroups）。
        _vm.SegmentsStructurallyChanged += OnSegmentsStructurallyChanged;
        // 调 IN/OUT 帧后播放境界窗口（对齐 Mac，见 OnBoundaryPreviewRequested）。
        _vm.BoundaryPreviewRequested += OnBoundaryPreviewRequested;
        // #12：卡片右键「分镜头替换」→ VM 抛事件 → 这里开工作区窗口。
        _vm.ShotEditRequested += OnShotEditRequested;
        // #18：卡片右键「拆分分镜」→ VM 抛事件 → 这里开拆分窗口。
        _vm.SplitRequested += OnSplitRequested;

        Focusable = true;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>#12：打开「分镜头替换」全屏工作区；关闭后把替换画面同步回卡片。</summary>
    private async void OnShotEditRequested(Segment segment)
    {
        try
        {
            var vm = _services.GetRequiredService<ShotEditViewModel>();
            var win = new ShotEditWindow(vm, segment, _settings) { Owner = Window.GetWindow(this) };
            win.ShowDialog();
            await _vm.ReloadReplacedPictureAsync(segment);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[SegLibDiag] 打开分镜头替换工作区失败 seg={Seg}", segment.Id);
            Components.ToastService.Show(
                "打不开分镜头替换工作区。可能是这个分镜的源视频已被移动或删除，请到「素材导入」确认文件还在原位置。",
                Components.ToastStyle.Error);
        }
    }

    /// <summary>重载 + 重建分组 + 刷新统计（绕过 LoadProject 的「同项目 return」守卫）。上传/拆分后复用。</summary>
    private void RefreshSegmentLibrary()
    {
        if (_currentProject is null) return;
        _vm.LoadSegments(_currentProject);
        _vm.RebuildGroups();
        BuildTypeChips();
        UpdateStats();
        UpdateEmptyState();
    }

    /// <summary>
    /// #17：上传自建分镜 —— 多选 mp4/mov（单条 ≤15s）。占位卡先出现并显示 loading（识别中→打标中），
    /// 处理完就地变就绪卡（对齐 §商用丝滑标准 §1 进度反馈：不让用户对着不动的界面干等）。
    /// async void 事件处理器：全 body try/catch，异常不逃逸。
    /// </summary>
    private async void OnUploadUserSegment(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择成品分镜（单条 ≤15 秒，可多选）",
                Filter = "视频文件 (*.mp4;*.mov)|*.mp4;*.mov|所有文件 (*.*)|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;

            var files = dlg.FileNames;
            var projectId = _currentProject.Id;
            var service = _services.GetRequiredService<Services.UserSegments.UserSegmentImportService>();

            UploadUserSegmentButton.IsEnabled = false;
            Serilog.Log.Information("[UserSegUpload] 开始上传 {N} 个文件", files.Length);
            var okCount = 0;
            var skipped = new List<string>();
            for (var i = 0; i < files.Length; i++)
            {
                var fileName = System.IO.Path.GetFileName(files[i]);
                var placeholderId = Guid.Empty;
                var r = await service.ImportOneAsync(files[i], projectId,
                    // 占位落库 → 立即显示 loading 占位卡。
                    onSegmentCreated: segId => Dispatcher.Invoke(() =>
                    {
                        placeholderId = segId;
                        _vm.SetProcessing(segId, "识别中…");
                        RefreshSegmentLibrary();
                    }),
                    // 阶段推进 → 更新占位卡文案（"识别中"→"打标中"）。
                    onStage: stage => Dispatcher.Invoke(() =>
                    {
                        if (placeholderId != Guid.Empty)
                            _vm.SetProcessing(placeholderId, stage.EndsWith("…") ? stage : stage + "…");
                    }));

                // 该文件完成：清占位 loading（SetProcessing 是增量路径，直接改对应卡片，不必全量重建）。
                if (placeholderId != Guid.Empty) _vm.SetProcessing(placeholderId, null);
                if (r.Status == Services.UserSegments.UserSegmentImportStatus.Success) okCount++;
                else skipped.Add($"{fileName}：{r.Message}");
            }

            // 全量刷新只做一次（原本在循环里每个文件都刷一次）：RefreshSegmentLibrary 是
            // 「同步全查 DB + 重建所有分组卡片 + 重算统计/chips」的全家桶，选 10 个文件上传
            // 就是 20 次全量重建，上传过程中界面持续抽搐。
            RefreshSegmentLibrary();

            // §F：广播失效 Overview/Schemes 等缓存（自建分镜计入分镜数）。
            _services.GetService<ImportViewModel>()?.NotifySegmentsChanged();

            var msg = okCount > 0 ? $"已上传 {okCount} 个自建分镜" : "没有成功上传的分镜";
            if (skipped.Count > 0)
            {
                msg += $"，{skipped.Count} 个跳过（" + string.Join("；", skipped.Take(3)) +
                       (skipped.Count > 3 ? " …" : string.Empty) + "）";
            }
            Components.ToastService.Show(msg,
                okCount > 0 ? Components.ToastStyle.Success : Components.ToastStyle.Warning);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[UserSegUpload] 上传处理异常");
            Components.ToastService.Show(
                "上传没能完成。请确认选中的是完整的 mp4/mov 文件、且没有被其它程序占用，然后重试。",
                Components.ToastStyle.Error);
        }
        finally
        {
            UploadUserSegmentButton.IsEnabled = true;
        }
    }

    /// <summary>#18：拆分分镜 —— 前置拦截（已被方案引用则不允许）→ 帧级预览选点 → 切两段 + 重识别 → 刷新。</summary>
    private async void OnSplitRequested(Segment segment)
    {
        try
        {
            // 前置拦截：已被方案组合引用的分镜禁止拆分（避免悬空引用）。
            var refs = await _vm.CountSchemeReferencesAsync(segment.Id);
            if (refs > 0)
            {
                Components.ToastService.Show(
                    $"该分镜已被 {refs} 个方案使用，请先在方案里移除对它的使用再拆分",
                    Components.ToastStyle.Warning);
                return;
            }

            var win = new SplitSegmentWindow(segment) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() != true) return;

            Components.ToastService.Show("正在拆分 …", Components.ToastStyle.Info);
            var (ok, error, aId, bId) = await _vm.SplitSegmentAsync(segment.Id, win.CutFrame);
            if (!ok)
            {
                Components.ToastService.Show(error ?? "拆分失败", Components.ToastStyle.Warning);
                return;
            }

            // 台词已在 SplitSegmentAsync 内从原视频 ASR 字级时间戳本地提取好（无网络、无需 key），
            // 缩略图也已按新边界重抽 → 直接刷新即两段就位。
            RefreshSegmentLibrary();
            _services.GetService<ImportViewModel>()?.NotifySegmentsChanged();
            Components.ToastService.Show("已拆分为两段分镜", Components.ToastStyle.Success);
        }
        catch (Exception ex)
        {
            Components.ToastService.Show(
                "拆分没有完成，原分镜没有改动。" + MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex),
                Components.ToastStyle.Error);
        }
    }

    public void LoadProject(Project project)
    {
        // 这里曾经有一句 `if (_currentProject?.Id == project.Id) return;`，已删除。
        //
        // 它看起来是「同项目不重复加载」的性能保护，实际上是个数据不刷新的 bug：
        // 「同项目要不要重载」的决策**已经由 MainWindow.UpdateContent 的 _viewLastLoadedProjectId
        // 缓存统一负责**（导航切换跳过、数据变更时主动移除缓存条目以强制重载）。视图内再守一次，
        // 就把 OnSegmentsChanged 的缓存失效整个吃掉了 ——
        // 复现：进过一次分镜库 → 回素材导入拖新视频 → 分析完成 → 切回分镜库，新分镜不出现，
        // 必须切到别的项目再切回来或者重启。全项目 6 个 IProjectView 里只有这个视图加了这层守卫。
        var projectChanged = _currentProject?.Id != project.Id;
        _currentProject = project;
        if (projectChanged)
        {
            Components.InlineVideoPlayer.StopAll();
            _vm.SetSelectionMode(false);
            ResetFilterUi();
        }
        _vm.LoadSegments(project);
        BuildTypeChips();
        _vm.RebuildGroups(preserveExistingGroups: !projectChanged);
        Serilog.Log.Information("[RefreshDiag] 素材库刷新 projectChanged={Changed} selected={Selected}",
            projectChanged, _vm.SelectedSegmentIds.Count);
        UpdateStats();
        UpdateSelectionToolbar();
        UpdateEmptyState();
        // 已有 thumbnail 立即走 cache
        var paths = _vm.FilteredSegments
            .Select(s => s.ThumbnailPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
        foreach (var p in paths)
        {
            _ = Infrastructure.ThumbnailCache.Shared.LoadAsync(p);
        }

        // 后台补生成缺失的 segment thumbnail（历史数据修复）
        _ = _vm.RepairMissingThumbnailsAsync();

        // 自验证：3 秒后写一行诊断汇总到日志（path / cached / missing 数）
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(3000);
            var total = paths.Count;
            var cached = paths.Count(p => Infrastructure.ThumbnailCache.Shared.PeekImage(p) is not null);
            var fileExists = paths.Count(p => System.IO.File.Exists(p));
            Serilog.Log.Information(
                "[ThumbDiag] project={Pid} totalSegments={Total} thumbPaths={Paths} fileExists={Exist} cachedNow={Cached} missing={Miss}",
                project.Id, _vm.FilteredSegments.Count, total, fileExists, cached, total - cached);
        });
    }

    // ============ 工具栏 ============

    /// <summary>
    /// 搜索防抖计时器。每敲一个字都跑一遍 ApplyFilter + RebuildGroups（几十上百张卡全部重建）
    /// 会让打字明显发涩 —— 尤其中文输入法逐字上屏时每个候选都触发一次。180ms 内的连续输入合并成一次，
    /// 既感觉不到延迟，又把重建次数从「每个字符一次」降到「每次停顿一次」。
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _searchDebounce;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        // 清除按钮的显隐是纯视觉、无成本 → 立即响应，不进防抖，否则按钮会慢半拍。
        ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Collapsed : Visibility.Visible;

        _searchDebounce?.Stop();
        _searchDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        _vm.Filter.SearchText = SearchBox.Text;
        _vm.ApplyFilter();
        _vm.RebuildGroups();
        UpdateStats();
        UpdateEmptyState();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        // 点「✕」是明确意图（不是打字中途），立即恢复全部结果，不等防抖那 180ms。
        OnSearchDebounceTick(null, EventArgs.Empty);
    }

    /// <summary>台词进入编辑态时自动聚焦 TextBox 并全选，用户直接改（对齐 mac textEditorFocused）。</summary>
    private void TranscriptEditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
        {
            // 布局/可见切换后再聚焦，否则 Focus 可能落空。
            tb.Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.CaretIndex = tb.Text.Length;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _vm.SortByQuality = SortCombo.SelectedIndex == 1;
        _vm.ApplyFilter();
        _vm.RebuildGroups();
    }

    private void OnResetFilter(object sender, RoutedEventArgs e)
    {
        ResetFilterUi();
        BuildTypeChips();
        _vm.RebuildGroups();
        UpdateStats();
        UpdateEmptyState();
    }

    /// <summary>
    /// 清空筛选条件与对应控件（搜索词 / 类型 chip / 排序）。
    /// 切项目与「重置筛选」共用 —— 切项目时不清会把上个项目的搜索词带过去过滤新项目。
    /// 注意要先摘掉 SelectionChanged，否则改 SortCombo 会反过来触发一次多余的重建。
    /// </summary>
    private void ResetFilterUi()
    {
        _vm.ResetFilter();
        _searchDebounce?.Stop();
        SearchBox.Text = string.Empty;
        SortCombo.SelectionChanged -= OnSortChanged;
        SortCombo.SelectedIndex = 0;
        SortCombo.SelectionChanged += OnSortChanged;
    }

    // ============ 多选 ============

    private void OnToggleSelectionMode(object sender, RoutedEventArgs e)
    {
        // 进多选态时点击语义变成「勾选」，正在播的视频会跟接下来的批量操作抢注意力 → 先停。
        if (!_vm.IsSelectionMode) Components.InlineVideoPlayer.StopAll();
        _vm.SetSelectionMode(!_vm.IsSelectionMode);
        _vm.SyncSelectionModeToCards();
        UpdateSelectionToolbar();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        _vm.SelectAllVisible();
        _vm.SyncCheckedToCards();
    }

    private void OnInvertSelection(object sender, RoutedEventArgs e)
    {
        _vm.InvertSelectionVisible();
        _vm.SyncCheckedToCards();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        _vm.ClearSelection();
        _vm.SyncCheckedToCards();
    }

    private void OnBatchExport(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSegmentIds.Count == 0) return;
        // 展开成「原版 + 各已生成配音变体」任务（VM 内重载 dubs），对齐 mac 变体批量导出。
        var jobs = _vm.BuildVariantExportJobs();
        var dialog = new BatchExportDialog(_variantExport, _settings, jobs)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    private void OnBatchDelete(object sender, RoutedEventArgs e)
    {
        var count = _vm.SelectedSegmentIds.Count;
        if (count == 0) return;
        var confirmed = Shared.MixCutDialog.Confirm(
            Window.GetWindow(this),
            $"删除选中的 {count} 个分镜？",
            "删除后可以按 Ctrl+Z 撤销，或点提示条上的「撤销」。",
            confirmText: $"删除 {count} 个", cancelText: "取消", destructive: true, icon: "🗑");
        if (!confirmed) return;
        // P0-16：删除失败（DB 保存异常）时不谎报成功，给人话提示 + 重试入口。
        var deleted = _vm.DeleteSelectedSegments();
        if (deleted is null)
        {
            Components.ToastService.Show("删除失败，请重试", Components.ToastStyle.Error);
            return;
        }
        RefreshAfterSegmentChange();

        // P0-10：压入撤销栈，Ctrl+Z 可一键恢复被删分镜。
        if (deleted.Count > 0)
        {
            Infrastructure.UndoStack.UndoManager.Shared.Push(
                new Infrastructure.UndoStack.DelegateUndoAction(
                    $"删除 {deleted.Count} 个分镜",
                    () =>
                    {
                        var n = _vm.RestoreSegments(deleted);
                        RefreshAfterSegmentChange();
                        Components.ToastService.Show(
                            n > 0 ? $"已恢复 {n} 个分镜" : "恢复失败，请重试",
                            n > 0 ? Components.ToastStyle.Success : Components.ToastStyle.Error);
                    }));
        }
        Components.ToastService.Show($"已删除 {count} 个分镜", Components.ToastStyle.Warning,
            "撤销", () => Infrastructure.UndoStack.UndoManager.Shared.Undo());
    }

    /// <summary>分镜增删后统一刷新分组/类型 chip/统计/工具栏/空态（删除与撤销恢复共用）。</summary>
    private void RefreshAfterSegmentChange()
    {
        _vm.RebuildGroups();
        BuildTypeChips();
        UpdateStats();
        UpdateSelectionToolbar();
        UpdateEmptyState();
    }

    private void OnSelectionChanged()
    {
        Dispatcher.Invoke(() =>
        {
            UpdateSelectionToolbar();
            _vm.SyncCheckedToCards();
        });
    }

    /// <summary>
    /// VM 触发的结构性变更（右键单删 / Ctrl+Z 恢复）后刷新 View 侧 chrome。
    /// VM 已自行 RebuildGroups（卡片增减已生效），这里只补统计 / 类型 chip / 工具栏 / 空态，避免重复 RebuildGroups。
    /// </summary>
    private void OnSegmentsStructurallyChanged()
    {
        Dispatcher.Invoke(() =>
        {
            BuildTypeChips();
            UpdateStats();
            UpdateSelectionToolbar();
            UpdateEmptyState();
        });
    }

    private void UpdateSelectionToolbar()
    {
        if (_vm.IsSelectionMode)
        {
            SelectionToolbar.Visibility = Visibility.Visible;
            SelectionModeButton.Content = "✕ 退出多选";
        }
        else
        {
            SelectionToolbar.Visibility = Visibility.Collapsed;
            SelectionModeButton.Content = "☑ 多选";
        }
        var n = _vm.SelectedSegmentIds.Count;
        SelectionCountText.Text = $"已选 {n}";
        BatchExportButton.IsEnabled = n > 0;
        BatchDeleteButton.IsEnabled = n > 0;
        CombineSchemeButton.IsEnabled = n >= 2;
    }

    private async void OnCombineSchemeClick(object sender, RoutedEventArgs e)
    {
        // async void 必须包 try/catch（CLAUDE.md §UI 标准）
        try
        {
            var selected = _vm.SelectedSegmentsInOrder;
            if (selected.Count < 2)
            {
                return;
            }

            var sheet = new SegmentLibrary.ArrangeOrderSheet(selected)
            {
                Owner = Window.GetWindow(this),
            };
            if (sheet.ShowDialog() != true || sheet.Result is null)
            {
                return;
            }

            if (_currentProject is not Project project
                || Window.GetWindow(this) is not MainWindow mainWin)
            {
                return;
            }

            Components.ToastService.Show("正在生成自定义方案…", Components.ToastStyle.Info);

            var schemeVm = mainWin.SchemeViewModel;
            schemeVm.LoadSchemes(project);

            var scheme = await schemeVm.CreateCustomSchemeAsync(sheet.Result, project);
            if (scheme is not null)
            {
                Components.ToastService.Show(
                    $"已生成方案「{scheme.Name}」", Components.ToastStyle.Success);
                mainWin.NavigateToSchemesAndSelect(scheme);
                _vm.SetSelectionMode(false);
                _vm.SyncSelectionModeToCards();
                UpdateSelectionToolbar();
            }
            else
            {
                Components.ToastService.Show(
                    "生成失败，请检查 AI Key 或网络", Components.ToastStyle.Warning);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[SegmentLibraryViewV2.OnCombineSchemeClick] 异常: {Message}", ex.Message);
            // §红线：ex.Message 可能含原始异常文本 —— 翻成人话（AI 异常本就是中文，FFmpeg/其它走兜底）。
            Components.ToastService.Show($"组合失败: {MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex)}", Components.ToastStyle.Warning);
        }
    }

    // ============ 类型 chip ============

    private void BuildTypeChips()
    {
        var counts = _vm.CountByType();
        var chips = new List<UIElement>();
        foreach (var type in SemanticTypeExtensions.All)
        {
            var isSelected = _vm.Filter.SemanticTypes.Contains(type);
            var count = counts.GetValueOrDefault(type, 0);
            var isEmpty = count == 0;
            var color = (Color)ColorConverter.ConvertFromString(type.ToColorHex());
            var displayColor = isEmpty ? Color.FromRgb(0x99, 0x99, 0x99) : color;

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock
            {
                Text = type.ToLabel(),
                FontSize = 11,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = new SolidColorBrush(isSelected ? Colors.White : displayColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0x40 : 0x20), displayColor.R, displayColor.G, displayColor.B)),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = count.ToString(CultureInfo.InvariantCulture),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(isSelected ? Colors.White : displayColor),
                },
            });

            var btn = new Button
            {
                Tag = type,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(9, 4, 9, 4),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0x66 : 0x26), displayColor.R, displayColor.G, displayColor.B)),
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0xC0 : 0x10), displayColor.R, displayColor.G, displayColor.B)),
                Content = stack,
                Opacity = isEmpty ? 0.35 : 1.0,
            };
            btn.Click += OnTypeChipClick;
            chips.Add(btn);
        }
        TypeChipList.ItemsSource = chips;
    }

    private void OnTypeChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SemanticType type })
        {
            if (!_vm.Filter.SemanticTypes.Add(type))
            {
                _vm.Filter.SemanticTypes.Remove(type);
            }
            _vm.ApplyFilter();
            _vm.RebuildGroups();
            BuildTypeChips();
            UpdateStats();
            UpdateEmptyState();
        }
    }

    private void UpdateStats()
    {
        var stats = _vm.Statistics();
        StatsText.Text =
            $"{_vm.FilteredSegments.Count} / {stats.Total} 个分镜 · 平均质量 " +
            stats.AverageQuality.ToString("F1", CultureInfo.InvariantCulture);
        ResetButton.Visibility =
            (_vm.Filter.SemanticTypes.Count > 0 || !string.IsNullOrEmpty(_vm.Filter.SearchText))
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEmptyState()
    {
        var isEmpty = _vm.FilteredSegments.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        GroupsScroller.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!isEmpty) return;

        // 空态必须区分三种处境，否则用户会误判。
        // 真实反馈：一次导入多个视频，分析还没跑完就切过来，看到「没有符合条件的分镜」
        // → 以为软件坏了。那时候其实只是还在分析。
        var analyzing = _services.GetService<ImportViewModel>()?.IsProcessing == true;
        var totalInProject = _vm.AllSegments.Count;

        EmptyProgress.Visibility = analyzing ? Visibility.Visible : Visibility.Collapsed;
        EmptyIcon.Visibility = analyzing ? Visibility.Collapsed : Visibility.Visible;

        if (analyzing)
        {
            EmptyTitle.Text = "正在分析视频…";
            EmptyHint.Text = "AI 正在切分镜头并标注类型，完成后分镜会自动出现在这里";
            EmptyActionButton.Visibility = Visibility.Collapsed;
        }
        else if (totalInProject > 0)
        {
            EmptyTitle.Text = "没有符合当前筛选的分镜";
            EmptyHint.Text = $"这个项目共有 {totalInProject} 个分镜，清空搜索或换个类型筛选就能看到";
            EmptyActionButton.Content = "重置筛选";
            EmptyActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyTitle.Text = "还没有分镜";
            EmptyHint.Text = "导入视频并完成 AI 分析后，分镜会出现在这里";
            EmptyActionButton.Content = "去导入素材";
            EmptyActionButton.Visibility = Visibility.Visible;
        }
    }

    private void OnEmptyActionClick(object sender, RoutedEventArgs e)
    {
        // 有分镜但被筛没了 → 重置筛选；一个都没有 → 去导入。
        if (_vm.AllSegments.Count > 0)
        {
            OnResetFilter(sender, e);
            return;
        }
        _navigateTo?.Invoke(NavigationItem.ImportMedia);
    }

    // ============ 卡片事件 + 懒加载播放器 ============

    /// <summary>向上遍历视觉树，判断点击是否落在交互控件内（按钮/文本框/滚动条/遮挡框）。</summary>
    private static bool IsInsideInteractive(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.CheckBox
                or System.Windows.Controls.TextBox
                or System.Windows.Controls.Primitives.ScrollBar
                or SegmentLibrary.SubtitleMaskOverlay)
                return true;
            // 点击卡片台词文字时 OriginalSource 是 Run（ContentElement，不是 Visual），
            // 直接喂 VisualTreeHelper.GetParent 会抛 InvalidOperationException（用户日志 2026-07-22 FTL）。
            // ContentElement 先走逻辑树爬回宿主 Visual（Run → TextBlock），再继续视觉树。
            child = child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return false;
    }

    /// <summary>
    /// 卡片点击（PreviewMouseLeftButtonDown 隧道阶段）→ 选中弹变体池。
    /// 用 Preview 因为 hover 自动播放后 InlineVideoPlayer 会盖住缩略图并吞掉冒泡点击，
    /// 隧道阶段 CardRoot 先收到，保证「hover 播放 + 点击选中」不冲突（对齐 mac tap 选中）。
    /// 不 set Handled：让按钮/遮挡框/播放器内部控件继续收到点击。
    /// </summary>
    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮/文本框/滚动条/遮挡框内 → 不选中，让那些控件自己处理。
        if (IsInsideInteractive(e.OriginalSource as DependencyObject))
            return;

        if (sender is FrameworkElement { Tag: SegmentCardViewModel card })
        {
            card.HandleCardClick();
        }
    }

    /// <summary>
    /// hover 中的 player 与 CardVM 时间字段的双向同步：调时间时实时刷新 player 的 segment 范围。
    /// 用 ConditionalWeakTable 把 handler 绑到 player 实例上，MouseLeave 时取消订阅。
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Components.InlineVideoPlayer, PropertyChangedEventHandler> _playerHandlers = new();

    /// <summary>
    /// 进卡：<b>只做高亮，不碰播放</b>。
    ///
    /// 这里曾经挂过「停留 0.35s 自动播放」的定时器，已按 CLAUDE.md §H 移除：鼠标扫过一排卡片时，
    /// 每张都会起停一次播放器，画面和声音连续乱闪，是体感最差的一处交互。播放统一由点击触发
    /// （整张缩略图都是点击区，见 <see cref="OnPlayOverlayClick"/>）—— 用户明确表达过要播才播。
    /// </summary>
    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SegmentCardViewModel card }) return;
        card.IsHovering = true;
    }

    /// <summary>
    /// 点击缩略图 / ▶ → 显式（重新）播放该分镜。补 hover 自动播放的盲区：
    /// 已在卡内、播完 / 调帧微调后 MouseEnter 不会再触发，用户点播放「没反应」（用户反馈：
    /// 调帧后必须移出空白处再移回才播）。StartHoverPlay 内已 guard「正在播则忽略」，重复点不抖。
    /// </summary>
    private void OnPlayOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (FindAncestorByName(fe, "CardRoot") is not FrameworkElement { Tag: SegmentCardViewModel card } cardRoot) return;
        if (!card.IsVideoFileAvailable) return;
        if (IsSelectionModeActive()) return; // 多选态点击用于勾选，不播放
        StartHoverPlay(card, cardRoot);
    }

    /// <summary>#12：点「切换画面」胶囊 → 在原画面 / AI 替换画面间切换（不触发播放）。</summary>
    private void OnTogglePictureClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // 不冒泡到卡片播放 / 选中
        if ((sender as FrameworkElement)?.DataContext is SegmentCardViewModel card
            && card.ToggleReplacedPictureCommand.CanExecute(null))
        {
            card.ToggleReplacedPictureCommand.Execute(null);
        }
    }

    /// <summary>
    /// 离卡：<b>只取消高亮，不停止播放</b>。
    ///
    /// 曾经在这里 StopHoverPlay，导致「点了播放，鼠标一移开就断」—— 用户想看完一个分镜
    /// 必须全程把鼠标按在卡片上。播放的生命周期改由播放器自己收口：播完、被别的卡抢占
    /// （InlineVideoPlayer 的全局唯一播放）、或用户点了别处，都会触发 Idle 还原缩略图。
    /// </summary>
    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SegmentCardViewModel card }) return;
        card.IsHovering = false;
    }

    private bool IsSelectionModeActive() =>
        DataContext is SegmentLibraryViewModel { IsSelectionMode: true };

    /// <summary>
    /// 找到承载指定 card 的 CardRoot Border。
    ///
    /// 走容器生成器（组 → 该组的卡片 ItemsControl → 卡片容器）而不是扫描整棵可视树：
    ///   · 快：原来每次调用都要递归遍历整个分镜库可视树（几万个 Visual），
    ///     调 IN/OUT 后的边界预览因此慢半拍；现在只在目标组内找。
    ///   · 正确：一旦开启虚拟化 + 容器回收，按 Name/Tag 扫树会命中**被回收后复用给别的卡**的容器，
    ///     导致预览播到错误的分镜上。容器生成器是官方的「item → 容器」映射，不受回收影响
    ///     （未实例化时返回 null，调用方本来就有 null 分支）。
    /// </summary>
    private FrameworkElement? FindCardRootFor(SegmentCardViewModel card)
    {
        var group = _vm.Groups.FirstOrDefault(g => g.Segments.Contains(card));
        if (group is not null
            && GroupsHost.ItemContainerGenerator.ContainerFromItem(group) is DependencyObject groupContainer)
        {
            // 组容器内只有一个承载分镜卡片的 ItemsControl。
            var cardsHost = FindDescendant<ItemsControl>(groupContainer);
            if (cardsHost?.ItemContainerGenerator.ContainerFromItem(card) is DependencyObject cardContainer
                && FindDescendantNamed(cardContainer, "CardRoot") is { } found)
            {
                return found;
            }
        }

        // 兜底：容器尚未生成（刚重建、还没走完布局）时退回扫描。
        foreach (var fe in EnumerateVisualChildren(this))
        {
            if (fe is FrameworkElement { Name: "CardRoot", Tag: SegmentCardViewModel c } cr && ReferenceEquals(c, card))
                return cr;
        }
        return null;
    }

    private static IEnumerable<FrameworkElement> EnumerateVisualChildren(DependencyObject root)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe) yield return fe;
            foreach (var deep in EnumerateVisualChildren(child)) yield return deep;
        }
    }

    /// <summary>境界窗口预览的播放时长（秒）：调 IN 从起点播这么久、调 OUT 播最后这么久。对齐 Mac「点加减播几秒看结果」。</summary>
    private const double BoundaryPreviewSeconds = 2.0;

    /// <summary>调 IN/OUT 后的境界预览播放 debounce（每卡一个）：连点微调只在停手 ~300ms 后播一次窗口，避免每帧重启 ffmpeg。</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SegmentCardViewModel, System.Windows.Threading.DispatcherTimer> _boundaryTimers = new();

    /// <summary>
    /// 在该卡 VideoHost 里懒创建（或复用）内联播放器，挂 Idle 还原逻辑（播完 / 停止 / 被抢占 → 拆播放器 + 还原 ▶/角标）。
    /// 不再挂「时间字段实时同步 SetSegment(整段)」监听 —— 调帧改由 <see cref="PlayBoundaryPreview"/> 播境界窗口取代
    /// （旧监听会与窗口预览抢 SetSegment 导致范围打架，见 §不要破坏已有功能）。
    /// </summary>
    private Components.InlineVideoPlayer EnsureCardPlayer(
        ContentControl videoHost, SegmentCardViewModel card)
    {
        if (videoHost.Content is Components.InlineVideoPlayer existing) return existing;
        var player = new Components.InlineVideoPlayer
        {
            AutoPlayOnHover = false,
            VideoStretch = System.Windows.Media.Stretch.UniformToFill,
        };
        player.Idle += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 仅当仍是本 player 才还原：避免旧 player 播完的 Idle 把刚换上的新预览的 ▶ 闪出来。
                if (videoHost.Content == player)
                {
                    TeardownHoverPlayer(videoHost, player, card);
                    // 还原 ▶ / 时长徽章：改 VM 状态而不是直接动控件，容器复用也不会串卡。
                    card.IsPlayingInline = false;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        };
        videoHost.Content = player;
        return player;
    }

    /// <summary>用宿主已布局尺寸预置解码目标（竖屏框），免去昂贵同步 UpdateLayout（数十卡 ~150-200ms）；刚塞进去的 player 自身 ActualWidth=0。</summary>
    private static void PrimePlayer(Components.InlineVideoPlayer player, ContentControl videoHost)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(videoHost);
        player.PrimeDecodeSize(
            (int)Math.Round(videoHost.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(videoHost.ActualHeight * dpi.DpiScaleY));
    }

    /// <summary>
    /// 点击 / hover 到时 → 在该卡 VideoHost 里懒创建内联播放器，播放<b>整段</b>（起点→终点）。
    /// 播完 / 停止 / 被其他卡抢占由 player.Idle 还原缩略图。
    /// </summary>
    private void StartHoverPlay(SegmentCardViewModel card, FrameworkElement cardRoot)
    {
        if (!card.IsVideoFileAvailable) return;

        var thumbGrid = FindChild<Grid>(cardRoot, "ThumbGrid");
        if (thumbGrid is null) return;
        var videoHost = FindChild<ContentControl>(thumbGrid, "VideoHost");
        if (videoHost is null) return;

        // 已经在播这张卡 → 忽略，避免重复 Open 抖动。
        if (videoHost.Content is Components.InlineVideoPlayer { IsPlaying: true })
        {
            return;
        }

        Serilog.Log.Information(
            "[SegPlayDiag] 点击/hover 播放 seq={Seq} startFrame={SF}",
            card.SequenceNumber, card.Segment.StartFrame);

        var player = EnsureCardPlayer(videoHost, card);
        card.IsPlayingInline = true;   // ▶ 与时长徽章随之隐藏（走 binding）
        PrimePlayer(player, videoHost);
        // #12：走 EffectivePicture —— 有 AI 替换画面则播替换视频（0..替换帧数），否则播原片段。未替换时返回原值。
        var ep = card.Segment.EffectivePicture;
        player.PlaySegment(ep.VideoPath, card.ThumbnailPath, ep.StartFrame, ep.EndFrame, ep.Fps);
    }

    /// <summary>
    /// VM 调 IN/OUT 帧后广播的境界预览请求 → debounce ~300ms 播放境界窗口（对齐 Mac：调开头从起点播几秒、
    /// 调结尾从末尾前几秒播到最后，便于查看调整结果）。连点微调合并成一次播放，避免每帧重启 ffmpeg
    /// （静止帧 <c>ScrubImage</c> 已给每点即时反馈，这里补动态）。
    /// </summary>
    private void OnBoundaryPreviewRequested(SegmentCardViewModel card, bool isStart)
    {
        if (!card.IsVideoFileAvailable) return;
        if (IsSelectionModeActive()) return; // 多选态不播（点击用于勾选）

        if (_boundaryTimers.TryGetValue(card, out var old)) { old.Stop(); _boundaryTimers.Remove(card); }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _boundaryTimers.Remove(card);
            PlayBoundaryPreview(card, isStart);
        };
        _boundaryTimers.AddOrUpdate(card, timer);
        timer.Start();
    }

    /// <summary>
    /// 播放境界窗口：isStart → [起点, 起点+N秒)；否则 → [末尾-N秒, 末尾)（EndFrame 不含，播到 EndFrame-1 冻结）。
    /// 复用 / 新建卡片播放器均走 <see cref="Components.InlineVideoPlayer.PlaySegment"/>（恒定一次 Open）。
    /// </summary>
    private void PlayBoundaryPreview(SegmentCardViewModel card, bool isStart)
    {
        if (!card.IsVideoFileAvailable) return;
        if (IsSelectionModeActive()) return;

        var cardRoot = FindCardRootFor(card);
        if (cardRoot is null) return;
        var thumbGrid = FindChild<Grid>(cardRoot, "ThumbGrid");
        if (thumbGrid is null) return;
        var videoHost = FindChild<ContentControl>(thumbGrid, "VideoHost");
        if (videoHost is null) return;

        var fps = card.Segment.EffectiveFps;
        if (fps <= 0) return; // 无 fps 无法帧窗预览（静止帧已给反馈）
        var segStart = Math.Max(0, card.Segment.StartFrame);
        var segEnd = card.Segment.EndFrame;
        if (segEnd <= segStart) return;

        var previewFrames = Math.Max(1, (int)Math.Round(BoundaryPreviewSeconds * fps));
        int winStart, winEnd;
        if (isStart) { winStart = segStart; winEnd = Math.Min(segEnd, segStart + previewFrames); }
        else { winEnd = segEnd; winStart = Math.Max(segStart, segEnd - previewFrames); }
        if (winEnd <= winStart) winEnd = winStart + 1;

        Serilog.Log.Information(
            "[BoundaryPreviewDiag] seq={Seq} isStart={IsStart} win=[{A},{B}) fps={Fps:F3}",
            card.SequenceNumber, isStart, winStart, winEnd, fps);

        var player = EnsureCardPlayer(videoHost, card);
        card.IsPlayingInline = true;   // ▶ 与时长徽章随之隐藏（走 binding）
        PrimePlayer(player, videoHost);
        player.PlaySegment(card.VideoLocalPath!, card.ThumbnailPath, winStart, winEnd, fps);
    }

    /// <summary>从子元素沿可视树向上查找指定 x:Name 的祖先。</summary>
    private static FrameworkElement? FindAncestorByName(DependencyObject? d, string name)
    {
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.Name == name) return fe;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>拆掉点击创建的内联播放器：解绑时间同步监听 + 从 VideoHost 移除（还原静态缩略图）。</summary>
    private void TeardownHoverPlayer(ContentControl videoHost, Components.InlineVideoPlayer player, SegmentCardViewModel card)
    {
        if (_playerHandlers.TryGetValue(player, out var handler))
        {
            card.PropertyChanged -= handler;
            _playerHandlers.Remove(player);
        }
        if (videoHost.Content == player)
        {
            videoHost.Content = null;
        }
    }

    // ============ 时间编辑框 commit ============

    private void OnStartLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: SegmentCardViewModel card } tb)
        {
            card.CommitStartCommand.Execute(tb.Text);
            // 回填规范化后的值（越界/非法输入会被 VM 拒绝，这里把显示拉回真实值）。单位是帧。
            tb.Text = card.StartFrameText;
        }
    }

    private void OnEndLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: SegmentCardViewModel card } tb)
        {
            card.CommitEndCommand.Execute(tb.Text);
            tb.Text = card.EndFrameText;
        }
    }

    private void OnStartKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnEndKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ============ QuickEdit ============

    private void OnQuickEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SegmentCardViewModel card } btn) return;

        var menu = new ContextMenu();
        foreach (var type in SemanticTypeExtensions.All)
        {
            var item = new MenuItem
            {
                Header = type.ToLabel(),
                IsCheckable = true,
                IsChecked = card.SemanticTypes.Contains(type),
            };
            item.Click += (_, _) => card.ToggleSemanticCommand.Execute(type);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var pos in PositionTypeExtensions.All)
        {
            var item = new MenuItem
            {
                Header = pos.ToLabel(),
                IsCheckable = true,
                IsChecked = card.PositionType == pos,
            };
            item.Click += (_, _) => card.UpdatePositionCommand.Execute(pos);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    // ============ 快捷键 ============

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;

        if (e.Key == Key.Escape && _vm.IsSelectionMode)
        {
            _vm.SetSelectionMode(false);
            _vm.SyncSelectionModeToCards();
            UpdateSelectionToolbar();
            e.Handled = true;
            return;
        }

        if (!_vm.IsSelectionMode) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.A:
                _vm.SelectAllVisible();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
            case Key.D:
                _vm.InvertSelectionVisible();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                _vm.ClearSelection();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
        }
    }

    // ============ Helper ============

    /// <summary>在 Visual Tree 里按 name 找子元素。用于 DataTemplate 实例化的元素查找。</summary>
    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) return t;
            var found = FindChild<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>在子树里找第一个指定类型的元素（不看名字）。用于从组容器里取承载卡片的 ItemsControl。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>在子树里按 x:Name 找元素（不限类型）。</summary>
    private static FrameworkElement? FindDescendantNamed(DependencyObject root, string name)
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            var found = FindDescendantNamed(child, name);
            if (found is not null) return found;
        }
        return null;
    }
}
