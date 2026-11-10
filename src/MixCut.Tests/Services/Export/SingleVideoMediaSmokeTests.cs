using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MixCut.Infrastructure;
using MixCut.Models;
using MixCut.Services.Dubbing;
using MixCut.Services.Export;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;
using MixCut.ViewModels;
using Xunit;

namespace MixCut.Tests.Services.Export;

/// <summary>使用真实 FFmpeg 验证原片顺序、字幕模糊/压制和音轨替换；不调用付费服务。</summary>
public sealed class SingleVideoMediaSmokeTests
{
    [MediaFact]
    public async Task Export_PreservesFullPictureTimeline_BlursAndReplacesOnlyChosenAudio()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mixcut-single-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ffmpeg = new FFmpegRunner(NullLogger<FFmpegRunner>.Instance);
            var source = Path.Combine(dir, "source.mp4");
            var audio = Path.Combine(dir, "dub.m4a");
            var output = Path.Combine(dir, "output.mp4");
            await ffmpeg.RunAsync(new[] { "-y", "-f", "lavfi", "-i", "testsrc2=s=320x568:r=30:d=6",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=6:sample_rate=44100",
                "-vf", "drawgrid=w=8:h=8:t=2:c=white", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source });
            await ffmpeg.RunAsync(new[] { "-y", "-f", "lavfi", "-i", "sine=frequency=880:duration=2:sample_rate=44100", "-c:a", "aac", audio });
            var video = new Video { LocalPath = source, Fps = 30, Duration = 6, Width = 320, Height = 568 };
            var first = new Segment { Video = video, VideoId = video.Id, StartFrame = 0, EndFrame = 60, Fps = 30,
                StartTime = 0, EndTime = 2, HasHardSubtitle = true, MaskRect = new SubtitleMaskRect(0, 0.75, 1, 0.15) };
            var second = new Segment { Video = video, VideoId = video.Id, StartFrame = 60, EndFrame = 120, Fps = 30,
                StartTime = 2, EndTime = 4, HasHardSubtitle = true, MaskRect = first.MaskRect };
            var dub = new SegmentDub { SegmentId = second.Id, RewrittenText = "新的逐句字幕", AudioFilePath = audio,
                Status = SegmentDubStatus.Generated, GeneratedForStartFrame = 60, GeneratedForEndFrame = 120,
                GeneratedForTextHash = DubbingViewModel.TextHash("新的逐句字幕"),
                CaptionLines = new() { new("新的逐句字幕", 0, 2) } };
            second.SegmentDubs.Add(dub);
            var input = SingleVideoExportPlanner.Build(video, new[] { second, first },
                new Dictionary<Guid, Guid?> { [second.Id] = dub.Id });
            var service = new DubExportService(ffmpeg, NullLogger<DubExportService>.Instance, new AppSettings());
            await service.ExportAsync(input, output, new ExportConfig { Resolution = ExportResolution.Original });
            var probe = await ffmpeg.RunProbeAsync(new[] { "-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=width,height,nb_frames,start_time,duration", "-of", "json", output });
            using var info = JsonDocument.Parse(probe);
            var stream = info.RootElement.GetProperty("streams")[0];
            Assert.Equal(320, stream.GetProperty("width").GetInt32());
            Assert.Equal(568, stream.GetProperty("height").GetInt32());
            Assert.Equal(180, int.Parse(stream.GetProperty("nb_frames").GetString()!, CultureInfo.InvariantCulture));
            Assert.InRange(double.Parse(stream.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture), -0.001, 0.001);

            // 片头/配音段/自动补回的片尾：音频应依次为原声440、配音880、原声440Hz。
            Assert.InRange(await Frequency(ffmpeg, output, 0.6), 425, 455);
            Assert.InRange(await Frequency(ffmpeg, output, 2.6), 860, 900);
            Assert.InRange(await Frequency(ffmpeg, output, 4.8), 425, 455);
            var original = await Frame(ffmpeg, source, 1);
            var blurred = await Frame(ffmpeg, output, 1);
            var captioned = await Frame(ffmpeg, output, 3);
            Assert.True(Sharpness(blurred, 430, 500) < Sharpness(original, 430, 500) * 0.6,
                "原声段也必须模糊旧字幕区域");
            Assert.True(Sharpness(captioned, 430, 500) > Sharpness(blurred, 430, 500),
                "配音段应出现新的字幕笔画");
            var tail = await Frame(ffmpeg, output, 5);
            var sourceTail = await Frame(ffmpeg, source, 5);
            Assert.InRange(MeanDifference(tail, sourceTail), 0, 18);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async Task<byte[]> Frame(FFmpegRunner ffmpeg, string path, double seconds) =>
        await ffmpeg.RunAsync(new[] { "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", path,
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" });

    private static double Sharpness(byte[] image, int y0, int y1)
    {
        long total = 0;
        for (var y = y0; y < y1; y++)
            for (var x = 1; x < 320; x++) total += Math.Abs(image[y * 320 + x] - image[y * 320 + x - 1]);
        return total / (double)((y1 - y0) * 319);
    }
    private static double MeanDifference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
    private static async Task<double> Frequency(FFmpegRunner ffmpeg, string path, double start)
    {
        var data = await ffmpeg.RunAsync(new[] { "-ss", start.ToString(CultureInfo.InvariantCulture), "-i", path,
            "-t", "0.3", "-vn", "-ac", "1", "-ar", "44100", "-f", "s16le", "pipe:1" });
        var crossings = 0;
        for (var i = 2; i < data.Length; i += 2)
            if (BitConverter.ToInt16(data, i - 2) < 0 && BitConverter.ToInt16(data, i) >= 0) crossings++;
        return crossings / (data.Length / 2.0 / 44100);
    }
}

public sealed class MediaFactAttribute : FactAttribute
{
    public MediaFactAttribute()
    {
        if (!BundledBinaries.FfmpegAvailable || !BundledBinaries.FfprobeAvailable)
            Skip = "真实导出冒烟需要测试输出目录 bin/ 下的 ffmpeg.exe 与 ffprobe.exe。";
    }
}
