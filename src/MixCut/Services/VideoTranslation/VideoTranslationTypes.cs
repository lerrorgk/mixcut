using MixCut.Models;

namespace MixCut.Services.VideoTranslation;

/// <summary>
/// 一条整片字幕：相对视频起点的时间窗 + 原文 + 译文。
/// 时间可手动校对，翻译只改 <see cref="TranslatedText"/>，不碰时间，避免译文长短影响口型对齐。
/// </summary>
public sealed class TranslationCue
{
    /// <summary>稳定 ID（从 1 递增）。批量翻译按 ID 对齐回填，避免模型乱序/漏条时错位。</summary>
    public int Id { get; set; }

    /// <summary>起始秒（相对视频起点）。</summary>
    public double Start { get; set; }

    /// <summary>结束秒。</summary>
    public double End { get; set; }

    /// <summary>识别出的原文，保留标点。</summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>译文；空 = 尚未翻译（导出会明确报错，不回退原文）。</summary>
    public string TranslatedText { get; set; } = string.Empty;

    /// <summary>该句译文使用的语言，支持分次补译和手动校对。</summary>
    public string TranslationLanguage { get; set; } = string.Empty;

    public bool IsTranslatedTo(string language, string legacyLanguage = "") =>
        !string.IsNullOrWhiteSpace(TranslatedText) && string.Equals(
            string.IsNullOrEmpty(TranslationLanguage) ? legacyLanguage : TranslationLanguage,
            language, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 一个独立整片翻译工程（纯 JSON 草稿，不落数据库、不依赖混剪项目）。
/// 与混剪分镜库完全解耦：用户拖进一个成片视频就能翻译，不需要先建项目。
/// </summary>
public sealed class VideoTranslationDocument
{
    /// <summary>源视频绝对路径。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>源视频内容哈希（SHA-256）。用于：草稿是否过期判定、人声分离缓存目录定位。</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>显示名（文件名去扩展名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>源视频像素宽（字幕定位/模糊区域都按它算）。</summary>
    public int Width { get; set; }

    /// <summary>源视频像素高。</summary>
    public int Height { get; set; }

    /// <summary>源视频帧率。</summary>
    public double Fps { get; set; }

    /// <summary>源视频时长（秒）。</summary>
    public double Duration { get; set; }

    /// <summary>缩略图绝对路径（UI 列表用，缺失时 <c>OpenAsync</c> 会补生成）。</summary>
    public string ThumbnailPath { get; set; } = string.Empty;

    /// <summary>目标语言（英文语言名，如 <c>English</c>），来自 <c>TranslationService.Languages</c>。</summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>
    /// 当前译文<b>实际</b>使用的语言。空 = 没翻译过。
    /// 换目标语言时不清空旧译文（用户可能只是切错了想切回来），但导出要求
    /// <see cref="TranslatedLanguage"/> == <see cref="TargetLanguage"/>，防止「重开工程以为旧译文已换成新语言」。
    /// </summary>
    public string TranslatedLanguage { get; set; } = string.Empty;

    /// <summary>是否模糊原片硬字幕区域。</summary>
    public bool BlurOriginalSubtitles { get; set; } = true;

    /// <summary>字幕区域（归一化坐标）。模糊与字幕落位共用同一个框，保证「挡住哪就写在哪」。</summary>
    public SubtitleMaskRect MaskRect { get; set; } = SubtitleMaskRect.Default;

    /// <summary>字幕字号比例（相对成片宽度），与混剪导出同源（<see cref="SubtitleFontSize"/>）。</summary>
    public double FontRatio { get; set; } = SubtitleFontSize.DefaultRatio;

    /// <summary>全部字幕（按时间升序，ID 递增）。</summary>
    public List<TranslationCue> Cues { get; set; } = new();
}

/// <summary>
/// 进度回调载荷。<paramref name="Fraction"/> 为 -1 表示该阶段算不出百分比（UI 用无限进度条）。
/// </summary>
public readonly record struct TranslationProgress(string Text, double Fraction = -1);
