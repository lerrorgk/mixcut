using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MixCut.Models;
using MixCut.Services.Dubbing;
using MixCut.Services.Export;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;
using Xunit;

namespace MixCut.Tests.Services.Export;

public sealed class DubBatchExportTests
{
    [Fact]
    public void Graph_UsesOriginalBgmWindow_AndSourceClockForTrimming()
    {
        var graph = DubSegmentGraphBuilder.Build(SubtitleMaskMode.None, 0, 125, 25,
            320, 180, new PixelRect(0, 140, 320, 40), Array.Empty<CaptionOverlay>(),
            false, 1, 0, 0, bgmInputIndex: 2, bgmStart: 20, bgmEnd: 25);
        Assert.Contains("trim=start_frame=0:end_frame=125", graph.FilterComplex);
        Assert.Contains("fps=30[base]", graph.FilterComplex);
        Assert.Contains("[2:a]atrim=start=20.00000:end=25.00000", graph.FilterComplex);
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(1080, 1920, 720, 1280)]
    public void Resolution_PreservesOrientation(int w, int h, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), DubExportService.Resolution(
            new ExportConfig { Resolution = ExportResolution.P720 }, w, h));
    }

    [Fact]
    public async Task BatchCache_ComparesCaptionValues_AndCleansUpAfterCancellation()
    {
        var cache = new DubExportService.BatchRenderCache();
        string? renderedPath = null;
        var calls = 0;
        Task Render(string dir, string path)
        {
            calls++;
            renderedPath = path;
            return File.WriteAllTextAsync(path, "test intermediate");
        }
        var spec = Spec("not-used.mp4", 30, 0, 60) with
        {
            CaptionLines = new[] { new CaptionLine("first", 0, 1) }
        };
        try
        {
            var first = await cache.GetOrRenderAsync(spec, 320, 180, ExportQuality.High, 0.05, "libx264", Render, default);
            var equivalent = spec with { CaptionLines = new[] { new CaptionLine("first", 0, 1) } };
            Assert.Equal(first, await cache.GetOrRenderAsync(equivalent, 320, 180, ExportQuality.High, 0.05, "libx264", Render, default));
            Assert.Equal(1, calls);
            equivalent.CaptionLines[0].Text = "corrected";
            Assert.NotEqual(first, await cache.GetOrRenderAsync(equivalent, 320, 180, ExportQuality.High, 0.05, "libx264", Render, default));
            Assert.Equal(2, calls);
            await cache.GetOrRenderAsync(equivalent, 320, 180, ExportQuality.High, 0.06, "libx264", Render, default);
            Assert.Equal(3, calls);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrRenderAsync(
                spec, 320, 180, ExportQuality.High, 0.05, "libx264", Render, canceled.Token));
        }
        finally { cache.Dispose(); }
        Assert.False(File.Exists(renderedPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(renderedPath)));
    }

    [MediaFact]
    public async Task Mixed25And30Fps_KeepTimelineAndAudio_AndReuseSegmentsAcrossCombinations()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mixcut-dubbatch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ffmpeg = new FFmpegRunner(NullLogger<FFmpegRunner>.Instance);
            var a = Path.Combine(dir, "25.mp4");
            var b = Path.Combine(dir, "30.mp4");
            foreach (var (path, fps, frequency) in new[] { (a, 25, 440), (b, 30, 880) })
                await ffmpeg.RunAsync(new[] { "-y", "-f", "lavfi", "-i", $"testsrc2=s=320x180:r={fps}:d=4",
                    "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=4:sample_rate=44100",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path });
            var service = new DubExportService(ffmpeg, NullLogger<DubExportService>.Instance, new AppSettings());
            var first = Spec(a, 25, 25, 75);
            var second = Spec(b, 30, 30, 90);
            var config = new ExportConfig { Resolution = ExportResolution.Original };
            using var cache = new DubExportService.BatchRenderCache();
            var output = Path.Combine(dir, "ab.mp4");
            await service.ExportAsync(new DubExportInput(new[] { first, second }, 320, 180), output, config, renderCache: cache);
            Assert.Equal(2, cache.RenderedCount);
            await service.ExportAsync(new DubExportInput(new[] { second, first }, 320, 180), Path.Combine(dir, "ba.mp4"), config, renderCache: cache);
            Assert.Equal(2, cache.RenderedCount);
            Assert.Equal(2, cache.ReusedCount);
            var probe = await ffmpeg.RunProbeAsync(new[] { "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=nb_frames,time_base,duration,width,height", "-of", "json", output });
            using var info = JsonDocument.Parse(probe);
            var stream = info.RootElement.GetProperty("streams")[0];
            Assert.Equal("120", stream.GetProperty("nb_frames").GetString());
            Assert.Equal("1/90000", stream.GetProperty("time_base").GetString());
            Assert.Equal(320, stream.GetProperty("width").GetInt32());
            Assert.Equal(180, stream.GetProperty("height").GetInt32());
            Assert.InRange(double.Parse(stream.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture), 3.95, 4.08);
            Assert.InRange(await Frequency(ffmpeg, output, 0.6), 425, 455);
            Assert.InRange(await Frequency(ffmpeg, output, 2.6), 860, 900);
        }
        finally { Directory.Delete(dir, true); }
    }

    [MediaFact]
    public async Task ReplacedPicture_BgmUsesOriginalVideoTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mixcut-bgm-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ffmpeg = new FFmpegRunner(NullLogger<FFmpegRunner>.Instance);
            var picture = Path.Combine(dir, "replacement.mp4");
            var bgm = Path.Combine(dir, "bgm.wav");
            var voice = Path.Combine(dir, "voice.wav");
            var output = Path.Combine(dir, "output.mp4");
            await ffmpeg.RunAsync(new[] { "-y", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", picture });
            await ffmpeg.RunAsync(new[] { "-y", "-f", "lavfi", "-i",
                "aevalsrc=0.1*sin(2*PI*if(lt(t\\,2)\\,440\\,880)*t):s=44100:d=4", bgm });
            await ffmpeg.RunAsync(new[] { "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", "2", voice });
            var spec = Spec(picture, 30, 0, 60) with
            {
                IsVoiceLocked = false, DubAudioPath = voice,
                BgmAudioPath = bgm, BgmStart = 2, BgmEnd = 4
            };
            var service = new DubExportService(ffmpeg, NullLogger<DubExportService>.Instance, new AppSettings());
            await service.ExportSingleSegmentAsync(spec, 320, 180, output,
                new ExportConfig { Resolution = ExportResolution.Original });
            Assert.InRange(await Frequency(ffmpeg, output, 0.6), 860, 900);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static DubSegmentSpec Spec(string path, double fps, int start, int end) =>
        new(path, start, end, fps, Array.Empty<CaptionLine>(), false, "Blur", SubtitleMaskRect.Default,
            true, null, 0, 0, null);

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
