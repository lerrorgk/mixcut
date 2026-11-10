using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Models;
using MixCut.Services.ASR;
using MixCut.Services.Dubbing;
using MixCut.Services.Export;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.VideoTranslation;

/// <summary>
/// 独立整片视频翻译服务：拖进一个成片视频 → 识别整片字幕 → 批量翻译 → 导出（模糊旧字幕 + 烧新字幕 + 移除所有声音）。
/// <para>
/// 与混剪项目完全解耦：不建项目、不写 <c>mixcut.db</c>、不依赖分镜。草稿是一份独立 JSON（按源文件哈希命名），
/// 换源文件即换草稿，源文件没变时重开直接恢复上次进度。
/// </para>
/// <para>
/// 失败语义（避免用户被静默坑）：识别/翻译失败都<b>不破坏</b>已有可用状态；导出前置条件不满足
/// （没翻译、译文语言已过期、没字幕）一律抛 <see cref="DubException"/> 说清下一步怎么办，绝不静默回退。
/// </para>
/// </summary>
public sealed class VideoTranslationService
{
    private readonly FFmpegRunner _ffmpeg;
    private readonly ASRService _asr;
    private readonly TranslationService _translation;
    private readonly ILogger<VideoTranslationService> _logger;

    public VideoTranslationService(
        FFmpegRunner ffmpeg, ASRService asr, TranslationService translation,
        ILogger<VideoTranslationService> logger)
    {
        _ffmpeg = ffmpeg;
        _asr = asr;
        _translation = translation;
        _logger = logger;
    }

    /// <summary>草稿目录：<c>&lt;Root&gt;\VideoTranslations</c>（独立于混剪数据库与项目目录）。</summary>
    public static string DraftDirectory { get; } =
        CreateDir(Path.Combine(AppPaths.Root, "VideoTranslations"));

    private static string CreateDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string DraftPath(string sourceHash)
    {
        // hash 也会来自外部 JSON（草稿被手改/被换过），必须校验成纯十六进制，
        // 否则 `..\..\x` 之类的值能拼出草稿目录之外的路径。
        if (sourceHash.Length != 64 || !sourceHash.All(Uri.IsHexDigit))
            throw new DubException("翻译草稿已损坏（源文件哈希非法）。请删除后重新添加视频。");
        return Path.Combine(DraftDirectory, sourceHash + ".json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文直接可读（草稿是给用户排查/迁移用的，不追求体积）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- 打开 ----

    /// <summary>
    /// 打开一个视频：算哈希 → 探测参数 → 补缩略图 → 恢复同源草稿（源文件变了则重建空草稿）。
    /// 不做识别、不做翻译，保证拖进来立刻有界面。
    /// </summary>
    public async Task<VideoTranslationDocument> OpenAsync(
        string path, IProgress<TranslationProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new DubException("找不到这个视频文件，可能已被移动或删除。");

        path = Path.GetFullPath(path);
        progress?.Report(new TranslationProgress("正在读取视频信息…"));

        var hash = await FileHelper.ComputeSha256Async(path, ct);
        var (width, height, fps) = await ProbeAsync(path, ct);
        var duration = await _ffmpeg.ProbeDurationAsync(path, ct);

        // 没有视频流 / 尺寸为 0：后面所有画布计算都建立在 W×H 上，允许 0 只会得到 2×2 垃圾成片。
        if (width <= 0 || height <= 0)
        {
            throw new DubException(
                "这个文件里没有可用的画面轨道（可能是纯音频文件，或封装已损坏）。请换一个正常的视频文件。");
        }
        if (duration <= 0)
        {
            throw new DubException("没能读出这段视频的时长，文件可能已损坏或不完整。请重新导出原片后再试。");
        }

        progress?.Report(new TranslationProgress("正在准备缩略图…"));
        var thumbDir = CreateDir(Path.Combine(DraftDirectory, "thumbs"));
        var thumbPath = Path.Combine(thumbDir, $"{hash}.jpg");
        if (!File.Exists(thumbPath))
        {
            try
            {
                await _ffmpeg.GenerateThumbnailAsync(
                    path, thumbPath, time: Math.Min(1.0, Math.Max(0.1, duration / 4)), cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 缩略图只是列表观感，取不到不算失败（导出不依赖它）。
                _logger.LogWarning(ex, "[VideoTranslateDiag] 缩略图生成失败 path={Path}", path);
            }
        }

        VideoTranslationDocument? cached = null;
        var draftPath = DraftPath(hash);
        if (File.Exists(draftPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(draftPath, ct);
                cached = JsonSerializer.Deserialize<VideoTranslationDocument>(json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 草稿坏了不能让用户打不开视频：降级成新建，并保留坏文件便于排查。
                _logger.LogWarning(ex, "[VideoTranslateDiag] 草稿解析失败，改为新建 draft={Draft}", draftPath);
                cached = null;
            }
        }

        var doc = cached ?? new VideoTranslationDocument();
        doc.SourcePath = path;
        doc.SourceHash = hash;
        doc.Name = Path.GetFileNameWithoutExtension(path);
        doc.Width = width > 0 ? width : doc.Width;
        doc.Height = height > 0 ? height : doc.Height;
        doc.Fps = fps > 0 ? fps : doc.Fps;
        doc.Duration = duration > 0 ? duration : doc.Duration;
        if (File.Exists(thumbPath)) doc.ThumbnailPath = thumbPath;
        Normalize(doc);

        _logger.LogInformation(
            "[VideoTranslateDiag] 打开 path={Name} hash={Hash} {W}x{H} fps={Fps:F3} dur={Dur:F2}s cues={Cues} 恢复={Restored}",
            doc.Name, hash, doc.Width, doc.Height, doc.Fps, doc.Duration, doc.Cues.Count, cached is not null);

        await SaveAsync(doc, ct);
        return doc;
    }

    // ---- 识别 ----

    /// <summary>
    /// 整片语音识别 → 时间轴字幕。成功才覆盖 <see cref="VideoTranslationDocument.Cues"/>，
    /// 失败保留上一次可用结果（用户不必为一次网络/模型抖动丢掉已翻译的稿子）。
    /// </summary>
    public async Task RecognizeAsync(
        VideoTranslationDocument doc, IProgress<TranslationProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureSource(doc);

        // 整片入口不限定源语言：whisper 多语种自动检测（中/英/日混说的广告片也能识别）。
        const string language = "auto";

        progress?.Report(new TranslationProgress("准备 Whisper 模型…"));
        await _asr.DownloadModelIfNeededAsync(
            onProgress: p => progress?.Report(
                new TranslationProgress($"下载语音模型 {p.Percent * 100:F0}%", p.Percent)),
            cancellationToken: ct);

        var inner = new Progress<double>(p =>
            progress?.Report(new TranslationProgress($"语音识别中 {p * 100:F0}%", p)));
        var result = await _asr.TranscribeAsync(
            doc.SourcePath, language, doc.Duration, inner, ct);

        var cues = new List<TranslationCue>();
        var id = 1;
        foreach (var sentence in result.Sentences)
        {
            var text = (sentence.Text ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            var start = Math.Max(0, sentence.StartTime);
            var end = Math.Max(start + 0.05, sentence.EndTime);
            cues.Add(new TranslationCue { Id = id++, Start = start, End = end, OriginalText = text });
        }

        if (cues.Count == 0)
        {
            throw new DubException(
                "这段视频没识别出任何台词。可能是纯音乐/无人声，或者音轨有问题；" +
                "请确认视频本身有说话声后重试。");
        }

        doc.Cues = cues;
        // 台词全换了，旧译文必然对不上：立刻标记为「未翻译」，界面据此重新要求翻译，
        // 避免用户直接导出得到一堆时间轴对歪的旧译文。
        doc.TranslatedLanguage = string.Empty;

        _logger.LogInformation(
            "[VideoTranslateDiag] 识别完成 cues={Count} dur={Dur:F2}s 语言={Lang}",
            cues.Count, doc.Duration, result.Language);

        await SaveAsync(doc, ct);
        progress?.Report(new TranslationProgress($"识别完成，共 {cues.Count} 条字幕", 1));
    }

    // ---- 翻译 ----

    /// <summary>
    /// 批量翻译全部字幕。按 <c>Id</c> 回填、保留时间轴；任何一条失败整批不落地，
    /// 抛错给用户重试（不回退原文，也不留「一半新一半旧」的混合译文）。
    /// </summary>
    public async Task TranslateAsync(
        VideoTranslationDocument doc, IProgress<TranslationProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureSource(doc);
        if (doc.Cues.Count == 0) throw new DubException("请先识别台词，再翻译。");
        if (string.IsNullOrWhiteSpace(doc.TargetLanguage)) throw new DubException("请先选择目标语言。");

        // 有一条原文为空就明确报出是哪一条：静默跳过会让用户以为译完了，导出却缺这一句。
        var blank = doc.Cues.Where(c => string.IsNullOrWhiteSpace(c.OriginalText)).ToList();
        if (blank.Count > 0)
        {
            throw new DubException(
                $"第 {string.Join('、', blank.Take(8).Select(c => c.Id))}" +
                (blank.Count > 8 ? " 等" : "") +
                $" 条字幕的原文是空的，请先补上原文再翻译（共 {blank.Count} 条）。");
        }

        var items = doc.Cues.Select(c => (c.Id, c.OriginalText)).ToList();

        progress?.Report(new TranslationProgress($"正在翻译 {items.Count} 条字幕…", 0));
        var inner = new Progress<(int Done, int Total)>(p =>
            progress?.Report(new TranslationProgress(
                $"翻译中 {p.Done}/{p.Total}",
                p.Total > 0 ? (double)p.Done / p.Total : -1)));

        var map = await _translation.TranslateBatchAsync(items, doc.TargetLanguage, 2, inner, ct);

        // 全部拿到才写回：中途失败时 doc.Cues 保持上一次可用译文。
        foreach (var cue in doc.Cues)
        {
            if (map.TryGetValue(cue.Id, out var translated)) cue.TranslatedText = translated;
        }
        doc.TranslatedLanguage = doc.TargetLanguage;

        _logger.LogInformation(
            "[VideoTranslateDiag] 翻译完成 条数={Count} 目标={Lang}", map.Count, doc.TargetLanguage);

        await SaveAsync(doc, ct);
        progress?.Report(new TranslationProgress($"翻译完成，共 {map.Count} 条", 1));
    }

    // ---- 保存 ----

    /// <summary>写草稿（原子替换：先写唯一临时文件再 Move，避免崩在中间留下半截 JSON）。</summary>
    public async Task SaveAsync(VideoTranslationDocument doc, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(doc.SourceHash)) return;
        Normalize(doc);

        var path = DraftPath(doc.SourceHash);
        // 每次用唯一临时名：自动保存定时器与手动加载可能重叠，固定 .tmp 会互相覆盖/删掉对方半成品。
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(doc, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
            throw;
        }
    }

    // ---- 导出 ----

    /// <summary>
    /// 导出整片翻译成片。全程写临时文件，成功后原子替换到目标路径：
    /// 中途取消/失败不会留下「半个 MP4」让用户以为成了。
    /// </summary>
    public async Task ExportAsync(
        VideoTranslationDocument doc, string outputPath,
        IProgress<TranslationProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureSource(doc);
        ValidateExportable(doc);

        outputPath = Path.GetFullPath(outputPath);
        if (string.Equals(outputPath, doc.SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new DubException("导出路径不能和原视频相同（会覆盖原片）。请换一个文件名或目录。");

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var workDir = CreateDir(Path.Combine(Path.GetTempPath(), $"mixcut-vt-{Guid.NewGuid():N}"));
        var tempOut = Path.Combine(outDir ?? Path.GetTempPath(),
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(outputPath)}");

        try
        {
            // 1) 字幕带：整片只生成「一条」全帧透明 PNG 序列（ffconcat 控时间），
            //    这样 ffmpeg 永远只有 2 个输入、一条 overlay，几百条字幕也不会炸命令行/线程。
            progress?.Report(new TranslationProgress("1/2 渲染翻译字幕…", -1));
            var strip = await Task.Run(() => BuildCaptionStrip(doc, workDir, ct), ct);
            ct.ThrowIfCancellationRequested();

            // 成片固定无声，字幕识别仍读取原片音轨；编码只映射画面并显式 -an。
            progress?.Report(new TranslationProgress("2/2 编码无声翻译视频…", 0));
            var args = BuildExportArgs(doc, strip, tempOut);
            var timeout = TimeSpan.FromSeconds(Math.Max(300, doc.Duration * 60));
            await _ffmpeg.RunAsync(
                args, totalDuration: doc.Duration,
                onProgress: p => progress?.Report(new TranslationProgress(
                    $"2/2 编码无声翻译视频 {p.Percentage:F0}%",
                    p.Percentage > 0 ? Math.Min(1, p.Percentage / 100.0) : -1)),
                timeout: timeout, cancellationToken: ct);

            ct.ThrowIfCancellationRequested();
            File.Move(tempOut, outputPath, overwrite: true);

            _logger.LogInformation(
                "[VideoTranslateDiag] 导出完成 out={Out} 字幕={Cues} audioStreams=0",
                outputPath, strip?.CueCount ?? 0);

            progress?.Report(new TranslationProgress("导出完成", 1));
        }
        catch
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { /* 忽略 */ }
            throw;
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* 忽略 */ }
        }
    }

    // ---- 内部：前置校验 ----

    private static void EnsureSource(VideoTranslationDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.SourcePath) || !File.Exists(doc.SourcePath))
            throw new DubException("原视频不见了，可能已被移动或删除。请重新添加视频。");
    }

    private static void ValidateExportable(VideoTranslationDocument doc)
    {
        if (doc.Cues.Count == 0)
            throw new DubException("还没有字幕。请先「识别台词」，再翻译，然后导出。");

        if (string.IsNullOrWhiteSpace(doc.TranslatedLanguage))
            throw new DubException("还没有译文。请先点「翻译」，否则导出的成片只有原声没有新字幕。");

        if (!string.Equals(doc.TargetLanguage, doc.TranslatedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new DubException(
                $"当前译文是「{doc.TranslatedLanguage}」，但目标语言已改成「{doc.TargetLanguage}」。" +
                "请重新翻译一次再导出，否则成片里会是上一种语言。");
        }

        // 只统计「有原文但没译文」的行：用户把原文清空的空行视为已废弃，不拦导出。
        var missing = doc.Cues.Count(c =>
            !string.IsNullOrWhiteSpace(c.OriginalText) && string.IsNullOrWhiteSpace(c.TranslatedText));
        if (missing > 0)
        {
            throw new DubException(
                $"有 {missing} 条字幕还没有译文，导出会缺字幕。请重新翻译后再导出。");
        }
    }

    // ---- 内部：草稿整理 ----

    /// <summary>
    /// 只做「不破坏用户数据」的整理：
    /// <list type="bullet">
    /// <item>语言/遮挡框/字号无条件兜底（新工程也要有默认目标语言）；</item>
    /// <item>时间窗夹进 [0, 时长] 并保证正长度；</item>
    /// <item><b>不丢弃任何字幕</b>（用户可能正在改原文，清空不该被自动保存删掉）；</item>
    /// <item><b>不重新编号</b>（Id 是批量翻译与界面回填的锚点，重排会让译文错位）。</item>
    /// </list>
    /// </summary>
    private static void Normalize(VideoTranslationDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.TargetLanguage)) doc.TargetLanguage = "English";
        doc.MaskRect = doc.MaskRect.Clamped();
        doc.FontRatio = SubtitleFontSize.Clamp(doc.FontRatio <= 0 ? SubtitleFontSize.DefaultRatio : doc.FontRatio);

        if (doc.Cues.Count == 0)
        {
            doc.TranslatedLanguage = string.Empty;
            return;
        }

        var maxEnd = doc.Duration > 0 ? doc.Duration : double.MaxValue;
        var nextId = doc.Cues.Count == 0 ? 1 : doc.Cues.Max(c => c.Id) + 1;
        foreach (var cue in doc.Cues)
        {
            // 老草稿/手改 JSON 可能没有 Id：补一个单调递增的，已有 Id 一律原样保留。
            if (cue.Id <= 0) cue.Id = nextId++;

            cue.Start = Math.Max(0, Math.Min(cue.Start, maxEnd));
            var end = Math.Max(cue.End, cue.Start + 0.05);
            cue.End = Math.Min(Math.Max(end, cue.Start + 0.05), Math.Max(maxEnd, cue.Start + 0.05));
        }
    }

    // ---- 内部：字幕带 ----

    /// <summary>字幕带：一条 ffconcat 清单，配上覆盖整片时长的全帧透明 PNG 序列。</summary>
    private sealed record CaptionStrip(string ListPath, int CueCount);

    /// <summary>
    /// 把每条译文画进「全帧透明 PNG」，再用 ffconcat 的 file+duration 铺满整条时间轴
    /// （空隙用全透明帧填充）。导出时只叠一条视频流，输入数与字幕条数无关。
    /// 纯 CPU 工作，调用方需放到后台线程（<see cref="Task.Run(Action, CancellationToken)"/>）。
    /// </summary>
    private CaptionStrip? BuildCaptionStrip(VideoTranslationDocument doc, string workDir, CancellationToken ct)
    {
        var w = Math.Max(2, doc.Width);
        var h = Math.Max(2, doc.Height);
        var duration = Math.Max(0.05, doc.Duration);
        var mask = SafeMask(doc, w, h);
        var canvasW = Math.Max(120, mask?.Width ?? w);
        var fontSize = (float)SubtitleFontSize.FontSize(w, doc.FontRatio);
        var maskRect = doc.MaskRect.Clamped();

        // 时间轴铺排：空档→透明帧，字幕窗→该条字幕帧。
        var entries = new List<(string Path, double Duration)>();
        var blank = TransparentFramePath(workDir, w, h);
        var cursor = 0.0;
        var cueCount = 0;
        var index = 0;

        foreach (var cue in doc.Cues.OrderBy(c => c.Start))
        {
            ct.ThrowIfCancellationRequested();

            var text = CaptionRenderer.StripPunctuation(cue.TranslatedText);
            var start = Math.Max(cursor, Math.Min(cue.Start, duration));
            var end = Math.Min(Math.Max(start + 0.05, cue.End), duration);
            if (end <= start) continue;
            if (start > cursor) entries.Add((blank, start - cursor));

            // 空译文（用户刚清空）也占位，否则前后字幕会被压缩到错误时间。
            var frame = text.Length == 0
                ? blank
                : RenderCaptionFrame(text, canvasW, fontSize, maskRect, w, h, workDir, index++, ct);

            entries.Add((frame, end - start));
            cursor = end;
            if (!ReferenceEquals(frame, blank)) cueCount++;
        }

        if (entries.Count == 0) return null;              // 全片无字幕：不叠字幕流
        if (duration > cursor) entries.Add((blank, duration - cursor));

        ct.ThrowIfCancellationRequested();
        return new CaptionStrip(WriteConcatList(entries, workDir), cueCount);
    }

    /// <summary>缓存的全透明帧（同一文件可多次出现在 concat 清单里）。</summary>
    private static string TransparentFramePath(string workDir, int w, int h)
    {
        var path = Path.Combine(workDir, $"blank_{w}x{h}.png");
        if (File.Exists(path)) return path;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Transparent);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>一条字幕 → 全帧透明 PNG（字幕画在解算出的 overlay 原点上）。</summary>
    private static string RenderCaptionFrame(
        string text, int canvasW, float fontSize, SubtitleMaskRect maskRect,
        int frameW, int frameH, string workDir, int index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var captionPng = Path.Combine(workDir, $"cap_{index:D4}.png");
        var img = CaptionRenderer.RenderToFile(text, canvasW, withBackdrop: true, fontSize, captionPng);

        var (x, y) = CaptionLayout.OverlayOrigin(
            frameW, frameH, maskRect, img.PixelWidth, img.PixelHeight);

        var framePng = Path.Combine(workDir, $"frame_{index:D4}.png");
        using var frame = new Bitmap(frameW, frameH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var cap = Image.FromFile(img.Path);
            g.DrawImage(cap, x, y, img.PixelWidth, img.PixelHeight);
        }
        frame.Save(framePng, ImageFormat.Png);
        return framePng;
    }

    /// <summary>写 ffconcat 清单：末尾补一格同帧（concat 解复用器要求最后一格没有 duration 也能收尾）。</summary>
    private static string WriteConcatList(List<(string Path, double Duration)> entries, string workDir)
    {
        var listPath = Path.Combine(workDir, "captions.ffconcat");
        var sb = new StringBuilder();
        sb.AppendLine("ffconcat version 1.0");
        foreach (var (path, seconds) in entries)
        {
            sb.Append("file '").Append(path.Replace('\\', '/').Replace("'", @"'\''")).AppendLine("'");
            sb.Append("duration ").AppendLine(seconds.ToString("F5", CultureInfo.InvariantCulture));
        }
        // 最后一格重复一次且不带 duration，避免 ffmpeg 认为清单截断。
        if (entries.Count > 0)
        {
            sb.Append("file '")
              .Append(entries[^1].Path.Replace('\\', '/').Replace("'", @"'\''"))
              .AppendLine("'");
        }
        File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));
        return listPath;
    }

    // ---- 内部：ffmpeg 参数 ----

    private List<string> BuildExportArgs(
        VideoTranslationDocument doc, CaptionStrip? strip, string outputPath)
    {
        var w = Math.Max(2, doc.Width);
        var h = Math.Max(2, doc.Height);
        var duration = Math.Max(0.05, doc.Duration);
        var parts = new List<string>();
        // 不做模糊时直接引用输入流 "0:v"：绝不能写个 "src" 标签再引用 —— 没有对应的 [0:v]xxx[src] 过滤，
        // 未定义标签会让 filter_complex 解析失败（「Filter 'src' has not been defined」）。
        var videoLabel = "0:v";

        if (doc.BlurOriginalSubtitles && SafeMask(doc, w, h) is { } m)
        {
            // 半径必须 ≤ (平面宽-1)/2 且 ≤ (平面高-1)/2；yuv420 色度平面是亮度一半，是最紧约束。
            // 遮挡框被拖很窄时（如 76px）写死 20 会超限 → ffmpeg「Failed to evaluate filter params」整条失败。
            var radius = Math.Max(1, Math.Min(20, Math.Min(m.Width, m.Height) / 4 - 1));
            parts.Add("[0:v]split=2[mb0][mb1]");
            parts.Add($"[mb1]crop={m.Width}:{m.Height}:{m.X}:{m.Y},boxblur={radius}:1[mbb]");
            parts.Add($"[mb0][mbb]overlay={m.X}:{m.Y}[masked]");
            videoLabel = "masked";
        }

        var args = new List<string> { "-y", "-i", doc.SourcePath };

        // 字幕带是第 2 个输入（整片只 1 条流），不再按字幕条数堆输入。
        var stripIndex = 0;
        if (strip is not null)
        {
            args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", strip.ListPath });
            stripIndex = 1;
        }

        if (strip is not null)
        {
            // 字幕帧本身是全帧透明 PNG，overlay 固定 0:0，时间由 concat 帧时间戳决定。
            parts.Add($"[{videoLabel}][{stripIndex}:v]overlay=0:0:format=auto[vout]");
        }
        else
        {
            parts.Add($"[{videoLabel}]null[vout]");
        }

        args.Add("-filter_complex");
        args.Add(string.Join(';', parts));
        args.AddRange(new[] { "-map", "[vout]", "-an" });
        AppendVideoCodecArgs(args);

        args.AddRange(new[]
        {
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-t", Fmt(duration),
        });
        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// 视频编码参数。有硬编就用硬编（目标码率封顶，避免 4K 源码率飙高），
    /// 否则 libx264 CRF 20（同画质下比硬编更省体积）。带 <c>-tag:v avc1</c> 保证 QuickTime/平台能认。
    /// </summary>
    private static void AppendVideoCodecArgs(List<string> args)
    {
        var hardware = HardwareEncoderProbe.H264Hardware;
        if (!string.IsNullOrEmpty(hardware))
        {
            var kbps = ExportQuality.High.VideoBitrateKbps(ExportCodec.H264Hardware);
            args.AddRange(new[]
            {
                "-c:v", hardware, "-b:v", $"{kbps}k", "-maxrate", $"{kbps * 2}k",
                "-tag:v", "avc1",
            });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "libx264", "-crf", "20", "-preset", "medium" });
        }
    }

    /// <summary>遮挡/字幕区域（像素）。关闭模糊时字幕仍按同一个归一化框落位，只是不糊底层。</summary>
    private static PixelRect? SafeMask(VideoTranslationDocument doc, int w, int h)
    {
        var r = PixelRect.From(doc.MaskRect.Clamped(), w, h);
        if (r.Width < 2 || r.Height < 2) return null;
        // 保证完全落在画面内（ffmpeg crop 越界会直接报错）。
        var x = Math.Max(0, Math.Min(r.X, w - 2));
        var y = Math.Max(0, Math.Min(r.Y, h - 2));
        var cw = Math.Min(r.Width, w - x);
        var ch = Math.Min(r.Height, h - y);
        if (cw < 2 || ch < 2) return null;
        return new PixelRect(x, y, cw, ch);
    }

    private static string Fmt(double v) => v.ToString("F5", CultureInfo.InvariantCulture);

    // ---- 内部：探测 ----

    private async Task<(int Width, int Height, double Fps)> ProbeAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await _ffmpeg.RunProbeAsync(new[]
            {
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height,r_frame_rate", "-of", "json", path,
            }, cancellationToken: ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                return (0, 0, 0);

            var stream = streams[0];
            var w = stream.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
            var h = stream.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
            var fps = 0.0;
            if (stream.TryGetProperty("r_frame_rate", out var fe) && fe.GetString() is { } raw)
            {
                var slash = raw.IndexOf('/');
                if (slash > 0
                    && double.TryParse(raw[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                    && double.TryParse(raw[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
                    && den > 0)
                {
                    fps = num / den;
                }
                else
                {
                    double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);
                }
            }
            return (w, h, fps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 探测失败不阻断：后续 ffmpeg 会因为真正的原因（文件损坏）报更具体的错。
            _logger.LogWarning(ex, "[VideoTranslateDiag] 探测失败 path={Path}", path);
            return (0, 0, 0);
        }
    }
}
