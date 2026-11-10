using System.IO;
using MixCut.Models;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>按原视频时间轴规划单条成片；分镜仅提供字幕处理与配音，不改变原画面或镜头顺序。</summary>
public static class SingleVideoExportPlanner
{
    /// <summary>
    /// choices 以分镜 Id 为键、配音 Id 为值；缺项或 null 保留原声。
    /// 调用方须加载各分镜的 SegmentDubs。明确选定的配音不可用时直接报错，禁止悄悄换回原声。
    /// </summary>
    public static DubExportInput Build(Video video, IReadOnlyList<Segment> segments,
        IReadOnlyDictionary<Guid, Guid?> choices)
    {
        if (string.IsNullOrWhiteSpace(video.LocalPath) || !File.Exists(video.LocalPath))
            throw new DubException("原视频文件已不存在，请重新导入视频后再导出。");
        if (!double.IsFinite(video.Fps) || video.Fps <= 0
            || !double.IsFinite(video.Duration) || video.Duration <= 0
            || video.Duration * video.Fps > int.MaxValue)
            throw new DubException("原视频的时长或帧率信息不完整，请重新导入视频后再导出。");

        var fps = video.Fps;
        var totalFrames = FrameTime.SecondsToFrame(video.Duration, fps);
        if (totalFrames <= 0)
            throw new DubException("原视频没有可导出的画面，请重新导入视频。");

        var ordered = segments.OrderBy(s => s.StartFrame).ThenBy(s => s.EndFrame).ToList();
        var ids = ordered.Select(s => s.Id).ToHashSet();
        if (choices.Any(c => c.Value is not null && !ids.Contains(c.Key)))
            throw new DubException("选中的配音对应的分镜已不存在，请重新选择后导出。");

        var specs = new List<DubSegmentSpec>();
        var cursor = 0;
        foreach (var segment in ordered)
        {
            var label = string.IsNullOrWhiteSpace(segment.SegmentIndex) ? "未命名" : segment.SegmentIndex;
            if (segment.VideoId is { } videoId && videoId != video.Id)
                throw new DubException($"分镜「{label}」不属于当前视频，请重新打开视频后导出。");
            if (segment.StartFrame < 0 || segment.EndFrame <= segment.StartFrame || segment.EndFrame > totalFrames)
                throw new DubException($"分镜「{label}」的起止位置超出原视频或时长为空，请先调整分镜边界。");
            if (segment.StartFrame < cursor)
                throw new DubException($"分镜「{label}」与前一个分镜存在重叠，无法确定这段画面使用哪条配音，请先调整分镜边界。");

            // AI 切分可能跳过静音/转场；补回这些区间才能保留完整原片。
            if (segment.StartFrame > cursor) specs.Add(Original(cursor, segment.StartFrame));
            choices.TryGetValue(segment.Id, out var chosenId);
            if (chosenId is null)
            {
                // 保留原声与字幕处理互相独立：不锁定渲染，空配音路径让引擎沿用原音轨。
                specs.Add(new DubSegmentSpec(video.LocalPath, segment.StartFrame, segment.EndFrame, fps,
                    Array.Empty<CaptionLine>(), segment.HasHardSubtitle, segment.MaskStyleRaw, segment.MaskRect,
                    IsVoiceLocked: false, DubAudioPath: null, 0, 0, BgmAudioPath: null));
            }
            else
            {
                if (segment.IsVoiceLocked)
                    throw new DubException($"分镜「{label}」已锁定保留原声，请先解除锁定或选择原声。");
                var dub = segment.SegmentDubs.FirstOrDefault(d => d.Id == chosenId.Value);
                if (dub is null)
                    throw new DubException($"分镜「{label}」选中的配音已不存在，请重新选择配音。");
                if (dub.Status != SegmentDubStatus.Generated || string.IsNullOrWhiteSpace(dub.AudioFilePath)
                    || !File.Exists(dub.AudioFilePath))
                    throw new DubException($"分镜「{label}」选中的配音尚未生成或音频文件已丢失，请重新生成配音后导出。");
                if (dub.GeneratedForStartFrame != segment.StartFrame || dub.GeneratedForEndFrame != segment.EndFrame
                    || dub.GeneratedForTextHash != ViewModels.DubbingViewModel.TextHash(dub.RewrittenText))
                    throw new DubException($"分镜「{label}」选中的配音已过期，台词或分镜边界已变化，请重新生成配音后导出。");

                var captions = dub.CaptionLines;
                if (captions.Count == 0)
                {
                    var text = string.IsNullOrEmpty(dub.RewrittenText) ? segment.Text : dub.RewrittenText;
                    if (!string.IsNullOrEmpty(text))
                        captions.Add(new CaptionLine(text, 0, (segment.EndFrame - segment.StartFrame) / fps));
                }
                specs.Add(new DubSegmentSpec(video.LocalPath, segment.StartFrame, segment.EndFrame, fps,
                    captions, segment.HasHardSubtitle, segment.MaskStyleRaw, segment.MaskRect,
                    IsVoiceLocked: false, DubAudioPath: dub.AudioFilePath,
                    // 原片时间轴不允许因旧数据中的定格补帧而延长。
                    0, dub.TrailingSilence, BgmAudioPath: BackgroundPath(video)));
            }
            cursor = segment.EndFrame;
        }
        if (cursor < totalFrames) specs.Add(Original(cursor, totalFrames));
        return new DubExportInput(specs, video.Width, video.Height);

        DubSegmentSpec Original(int start, int end) => new(video.LocalPath, start, end, fps,
            Array.Empty<CaptionLine>(), false, nameof(MaskStyle.Blur), SubtitleMaskRect.Default,
            IsVoiceLocked: true, DubAudioPath: null, 0, 0, BgmAudioPath: null);
    }

    private static string? BackgroundPath(Video video)
    {
        if (string.IsNullOrEmpty(video.ContentHash)) return null;
        var path = Path.Combine(AppPaths.StemsDirectory(video.ContentHash), "bgm.wav");
        return File.Exists(path) ? path : null;
    }
}
