using System.Globalization;
using System.Text;

namespace MixCut.Services.Dubbing;

/// <summary>单分镜中间片的 ffmpeg 滤镜图。对应 mac DubSegmentGraph。</summary>
public sealed record DubSegmentGraph(string FilterComplex, string VideoMapLabel, string AudioMapLabel);

/// <summary>一条逐句字幕 overlay：PNG 输入序号 + 落位 + 相对分镜起点的时间窗（enable=between 用）。对应 mac CaptionOverlay。</summary>
public readonly record struct CaptionOverlay(int InputIndex, int X, int Y, double Start, double End);

/// <summary>
/// 构建「切片 → 9:16 标准化 → 遮挡旧字幕 → 叠新字幕 → 换音轨(克隆配音+BGM混音)」滤镜图。
/// 对应 mac DubSegmentGraphBuilder。input 0=源视频；字幕 PNG=captionInputIndex；配音 m4a=dubAudioInputIndex；BGM=bgmInputIndex。
/// </summary>
public static class DubSegmentGraphBuilder
{
    // 后缀消毒链见 FFmpegRunner.LoudnormSanitize：loudnorm 遇纯静音音轨会产 NaN 采样导致
    // AAC 编码 exit -22；同时把 loudnorm 抬到 192kHz 的采样率锁回 44100（与主导出链同源一致）。
    private const string Loudnorm =
        "loudnorm=I=-16:TP=-1.5:LRA=11," + VideoProcessing.FFmpegRunner.LoudnormSanitize;
    private const double SilenceEpsilon = 0.001;
    /// <summary>混音时 BGM 相对音量（人声为主、BGM 垫底）。</summary>
    public const double BgmGain = 0.6;

    public static DubSegmentGraph Build(
        SubtitleMaskMode mode,
        int startFrame, int endFrame, double fps,
        int outputWidth, int outputHeight,
        PixelRect maskPixel,
        IReadOnlyList<CaptionOverlay> captions,
        bool keepOriginalAudio,
        int dubAudioInputIndex,
        int freezePadFrames,
        double trailingSilence,
        int? bgmInputIndex = null,
        int? vocalsInputIndex = null,
        double vocalsStart = 0,
        double vocalsEnd = 0,
        double? bgmStart = null,
        double? bgmEnd = null)
    {
        int w = outputWidth, h = outputHeight;
        // fps remains the source frame clock for trims; every intermediate shares one output clock.
        const int fpsInt = 30;
        var parts = new List<string>();
        string F(double v) => v.ToString("F5", CultureInfo.InvariantCulture);
        string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

        // 1) 视频：精确切片 + 9:16 标准化 → [base]
        parts.Add(
            $"[0:v]trim=start_frame={startFrame}:end_frame={endFrame},setpts=PTS-STARTPTS," +
            $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
            $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={fpsInt}[base]");

        // 2) 遮挡旧字幕 → [masked]
        int mx = maskPixel.X, my = maskPixel.Y, mw = maskPixel.Width, mh = maskPixel.Height;
        switch (mode)
        {
            case SubtitleMaskMode.None:
                parts.Add("[base]null[masked]");
                break;
            case SubtitleMaskMode.Blur:
                // boxblur 半径必须 ≤ (平面宽-1)/2 且 ≤ (平面高-1)/2；yuv420 色度平面尺寸是亮度的一半，
                // 是最紧的约束。遮挡框被用户拖很窄时（如 76px），写死 20 会超过色度半高 → ffmpeg
                // 「Failed to evaluate filter params: -22」整条导出失败。按遮挡区尺寸动态夹到安全半径。
                var blurRadius = Math.Max(1, Math.Min(20, Math.Min(mw, mh) / 4 - 1));
                parts.Add("[base]split=2[mb0][mb1]");
                parts.Add($"[mb1]crop={mw}:{mh}:{mx}:{my},boxblur={blurRadius}:1[mbb]");
                parts.Add($"[mb0][mbb]overlay={mx}:{my}[masked]");
                break;
            case SubtitleMaskMode.Solid:
                parts.Add($"[base]drawbox=x={mx}:y={my}:w={mw}:h={mh}:color=0x1A1A1A@1.0:t=fill[masked]");
                break;
            default: // Dim（已砍，兼容）
                parts.Add($"[base]drawbox=x={mx}:y={my}:w={mw}:h={mh}:color=0x000000@0.5:t=fill[masked]");
                break;
        }

        // 3) 逐句叠新字幕 PNG → [capped]（每句一个 overlay + enable=between；无字幕透传 [masked]）
        // 锁定段（保留原声）一律不叠，即使调用方误传了 captions。t=分镜内 0 起（由上面 trim/setpts 保证），
        // 正好对上 CaptionLine 的相对时间。旧数据/整段兜底：调用方传 1 条 [0,分镜时长] 即等价整段全程。
        var effectiveCaptions = keepOriginalAudio ? System.Array.Empty<CaptionOverlay>() : captions;
        string videoBeforePad;
        if (effectiveCaptions.Count == 0)
        {
            videoBeforePad = "masked";
        }
        else
        {
            var prev = "masked";
            for (var ci = 0; ci < effectiveCaptions.Count; ci++)
            {
                var c = effectiveCaptions[ci];
                var outLabel = ci == effectiveCaptions.Count - 1 ? "capped" : $"cap{ci}";
                parts.Add(
                    $"[{prev}][{c.InputIndex}:v]overlay={c.X}:{c.Y}:" +
                    $"enable='between(t,{F3(c.Start)},{F3(c.End)})'[{outLabel}]");
                prev = outLabel;
            }
            videoBeforePad = "capped";
        }

        // 4) 末尾定格补帧（freezePad 恒 0，保留逻辑）→ [vout]
        if (freezePadFrames > 0)
        {
            var dur = freezePadFrames / fps;
            parts.Add($"[{videoBeforePad}]tpad=stop_mode=clone:stop_duration={F3(dur)}[vout]");
        }
        else
        {
            parts.Add($"[{videoBeforePad}]null[vout]");
        }

        // 5) 音频 → [aout]
        if (keepOriginalAudio)
        {
            if (vocalsInputIndex is { } vi)
            {
                // BGM 替换模式（issue #22）：不用原混音轨，改用整轨 vocals.wav 按
                // [vocalsStart, vocalsEnd]（原视频时间轴，秒）切片 —— 去原 BGM 只留口播。
                parts.Add(
                    $"[{vi}:a]atrim=start={F(vocalsStart)}:end={F(vocalsEnd)}," +
                    "asetpts=PTS-STARTPTS,aresample=44100[aout]");
            }
            else
            {
                var aStart = startFrame / fps;
                var aEnd = endFrame / fps;
                parts.Add($"[0:a]atrim=start={F(aStart)}:end={F(aEnd)},asetpts=PTS-STARTPTS,aresample=44100[aout]");
            }
        }
        else
        {
            var padSuffix = trailingSilence > SilenceEpsilon ? $",apad=pad_dur={F3(trailingSilence)}" : "";
            if (bgmInputIndex is { } bgmIdx)
            {
                // 分离音轨属于原视频，替换画面的零起点不能用来裁音乐。
                var aStart = bgmStart ?? startFrame / fps;
                var aEnd = bgmEnd ?? endFrame / fps;
                parts.Add($"[{dubAudioInputIndex}:a]aresample=44100,{Loudnorm}{padSuffix}[voice]");
                parts.Add(
                    $"[{bgmIdx}:a]atrim=start={F(aStart)}:end={F(aEnd)},asetpts=PTS-STARTPTS,aresample=44100," +
                    $"volume={BgmGain.ToString("F2", CultureInfo.InvariantCulture)}[bgmcut]");
                parts.Add("[voice][bgmcut]amix=inputs=2:duration=first:normalize=0[aout]");
            }
            else
            {
                parts.Add($"[{dubAudioInputIndex}:a]aresample=44100,{Loudnorm}{padSuffix}[aout]");
            }
        }

        return new DubSegmentGraph(string.Join(";", parts), "[vout]", "[aout]");
    }
}
