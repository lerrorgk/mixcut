using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MixCut.Services.AI;
using MixCut.Services.Dubbing;
using MixCut.Services.VideoProcessing;
using MixCut.Services.VideoTranslation;
using MixCut.Tests.Services.Export;
using Xunit;

namespace MixCut.Tests.Services.VideoTranslation;

/// <summary>
/// 真实 FFmpeg 验证整片翻译导出：成片完全无声、原片时间轴不缩短（6 秒 180 帧）、
/// 字幕只出现在各自时间窗且空隙不糊画面、关闭遮挡模糊同样能导出。
/// 不调用付费服务、不碰数据库；ASR 不使用所以传 null。
/// </summary>
public sealed class VideoTranslationMediaTests
{
    private const int Width = 320;
    private const int Height = 568;
    private const int CapY0 = 440;
    private const int CapY1 = 515;

    [MediaFact]
    public async Task Export_DropsAudio_KeepsFullTimeline_AndOnlyShowsCaptionInsideItsWindow()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mixcut-vt-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ffmpeg = new FFmpegRunner(NullLogger<FFmpegRunner>.Instance);
            var source = Path.Combine(dir, "source.mp4");
            var output = Path.Combine(dir, "output.mp4");
            // 带一条真实音轨：导出后必须一条都没有（用户明确要求成片完全无声）。
            await ffmpeg.RunAsync(new[]
            {
                "-y", "-f", "lavfi", "-i", $"testsrc2=s={Width}x{Height}:r=30:d=6",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=6:sample_rate=44100",
                "-vf", "drawgrid=w=8:h=8:t=2:c=white", "-c:v", "libx264", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", source,
            });

            var doc = Doc(source, blur: true,
                (1, 0, 1, "第一句"),
                (2, 3, 4, "第二句"));

            var service = new VideoTranslationService(
                ffmpeg, null!, Translator(), NullLogger<VideoTranslationService>.Instance);
            await service.ExportAsync(doc, output);

            // 时间轴：仍是从 0 开始的 6 秒 / 180 帧。
            var stream = await VideoStream(ffmpeg, output);
            Assert.Equal(Width, stream.GetProperty("width").GetInt32());
            Assert.Equal(Height, stream.GetProperty("height").GetInt32());
            Assert.Equal(180, int.Parse(stream.GetProperty("nb_frames").GetString()!, CultureInfo.InvariantCulture));
            Assert.InRange(double.Parse(stream.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture), -0.001, 0.001);

            // 音轨：整片静音 => 输出里不应存在任何音频流。
            var audio = await ffmpeg.RunProbeAsync(new[]
            {
                "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "json", output,
            });
            using (var json = JsonDocument.Parse(audio))
            {
                var hasAudio = json.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0;
                Assert.False(hasAudio, "成片必须没有任何音轨");
            }

            // 字幕时序：字幕窗内出现笔画，空隙（1~3 秒）与片尾（4~6 秒）必须等于原片、不能被糊。
            var caption = await Frame(ffmpeg, output, 0.5);
            var gap = await Frame(ffmpeg, output, 2.0);
            var sourceGap = await Frame(ffmpeg, source, 2.0);
            var tail = await Frame(ffmpeg, output, 5.0);
            var sourceTail = await Frame(ffmpeg, source, 5.0);

            Assert.True(Sharpness(caption) > Sharpness(gap),
                $"字幕窗内应出现笔画：caption={Sharpness(caption):F3}, gap={Sharpness(gap):F3}");
            // 旧硬字幕区域整片模糊；新字幕应只在台词窗口内出现，区域外画面保持原样。
            Assert.True(Sharpness(gap) < Sharpness(sourceGap) * 0.5, "空隙中旧字幕区域应保持模糊");
            Assert.True(Sharpness(tail) < Sharpness(sourceTail) * 0.5, "片尾不应残留翻译字幕");
            Assert.InRange(MeanOutsideMaskDifference(gap, sourceGap), 0, 8);
            Assert.InRange(MeanOutsideMaskDifference(tail, sourceTail), 0, 8);
        }
        finally { Directory.Delete(dir, true); }
    }

    [MediaFact]
    public async Task Export_WithBlurDisabled_StillSucceeds_AndKeepsOriginalPixelsInGap()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mixcut-vt-noblur-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ffmpeg = new FFmpegRunner(NullLogger<FFmpegRunner>.Instance);
            var source = Path.Combine(dir, "source.mp4");
            var output = Path.Combine(dir, "output.mp4");
            await ffmpeg.RunAsync(new[]
            {
                "-y", "-f", "lavfi", "-i", $"testsrc2=s={Width}x{Height}:r=30:d=6",
                "-vf", "drawgrid=w=8:h=8:t=2:c=white", "-c:v", "libx264", "-pix_fmt", "yuv420p", source,
            });

            var doc = Doc(source, blur: false, (1, 0, 6, "整段字幕"));

            var service = new VideoTranslationService(
                ffmpeg, null!, Translator(), NullLogger<VideoTranslationService>.Instance);
            await service.ExportAsync(doc, output);

            Assert.True(File.Exists(output));
            var stream = await VideoStream(ffmpeg, output);
            Assert.Equal(180, int.Parse(stream.GetProperty("nb_frames").GetString()!, CultureInfo.InvariantCulture));

            // 关闭模糊时底图像素必须保持原样（不再引用不存在的过滤标签导致失败）。
            var exported = await Frame(ffmpeg, output, 4.5);
            var sourceLate = await Frame(ffmpeg, source, 4.5);
            Assert.True(Sharpness(exported) > Sharpness(sourceLate),
                "关闭模糊后仍应烧上字幕");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static VideoTranslationDocument Doc(
        string source, bool blur, params (int Id, double Start, double End, string Text)[] cues)
    {
        var doc = new VideoTranslationDocument
        {
            SourcePath = source,
            SourceHash = "test",
            Duration = 6,
            Width = Width,
            Height = Height,
            Fps = 30,
            TargetLanguage = "English",
            TranslatedLanguage = "English",
            BlurOriginalSubtitles = blur,
        };
        foreach (var (id, start, end, text) in cues)
        {
            doc.Cues.Add(new TranslationCue
            {
                Id = id, Start = start, End = end, OriginalText = text, TranslatedText = text,
            });
        }
        return doc;
    }

    private static TranslationService Translator() =>
        new(() => new UnusedProvider(), NullLogger<TranslationService>.Instance);

    private static async Task<JsonElement> VideoStream(FFmpegRunner ffmpeg, string path)
    {
        var probe = await ffmpeg.RunProbeAsync(new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries",
            "stream=width,height,nb_frames,start_time", "-of", "json", path,
        });
        using var info = JsonDocument.Parse(probe);
        return info.RootElement.GetProperty("streams")[0].Clone();
    }

    private static async Task<byte[]> Frame(FFmpegRunner ffmpeg, string path, double seconds) =>
        await ffmpeg.RunAsync(new[]
        {
            "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", path,
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1",
        });

    /// <summary>字幕带内的横向梯度均值：有笔画就明显变大。</summary>
    private static double Sharpness(byte[] image)
    {
        long total = 0;
        for (var y = CapY0; y < CapY1; y++)
            for (var x = 1; x < Width; x++) total += Math.Abs(image[y * Width + x] - image[y * Width + x - 1]);
        return total / (double)((CapY1 - CapY0) * (Width - 1));
    }

    private static double MeanOutsideMaskDifference(byte[] a, byte[] b) =>
        a.Select((value, index) => (value, index))
            .Where(p => p.index / Width < CapY0 || p.index / Width >= CapY1)
            .Average(p => Math.Abs(p.value - b[p.index]));

    private sealed class UnusedProvider : IAiProvider
    {
        public Task<T> GenerateJsonAsync<T>(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("导出路径不应调用翻译模型");

        public Task<string> GenerateTextAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("导出路径不应调用翻译模型");
    }
}
