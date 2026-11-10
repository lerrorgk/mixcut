using System.IO;
using MixCut.Models;
using MixCut.Services.Dubbing;
using MixCut.ViewModels;
using Xunit;

namespace MixCut.Tests.Services.Export;

public sealed class SingleVideoExportPlannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mixcut-single-plan-{Guid.NewGuid():N}");
    private readonly Video _video;

    public SingleVideoExportPlannerTests()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "original.mp4");
        File.WriteAllText(source, "planner only checks file presence");
        _video = new Video { LocalPath = source, Duration = 10, Fps = 30, Width = 1080, Height = 1920 };
    }

    [Fact]
    public void Build_SortsBySourceFrameAndPreservesAllGapsAndOriginalPicture()
    {
        var first = Segment(30, 90);
        var second = Segment(150, 240);
        first.PictureShowsReplaced = true;
        first.ReplacedPictureVideoPath = "replacement.mp4";
        first.ReplacedPictureFrameCount = 60;

        var input = SingleVideoExportPlanner.Build(_video, new[] { second, first }, new Dictionary<Guid, Guid?>());

        Assert.Equal(new[] { (0, 30), (30, 90), (90, 150), (150, 240), (240, 300) },
            input.Segments.Select(s => (s.StartFrame, s.EndFrame)).ToArray());
        Assert.All(input.Segments, s => Assert.Equal(_video.LocalPath, s.VideoPath));
        Assert.Equal(300, input.Segments.Sum(s => s.EndFrame - s.StartFrame));
        Assert.Equal(1080, input.MaxWidth);
        Assert.Equal(1920, input.MaxHeight);
    }

    [Fact]
    public void Build_WithoutSegmentsPreservesEntireVideo()
    {
        var spec = Assert.Single(SingleVideoExportPlanner.Build(_video, Array.Empty<Segment>(),
            new Dictionary<Guid, Guid?>()).Segments);
        Assert.Equal(0, spec.StartFrame);
        Assert.Equal(300, spec.EndFrame);
        Assert.Null(spec.DubAudioPath);
        Assert.Empty(spec.CaptionLines);
    }

    [Fact]
    public void Build_OriginalAudioCanBlurOldSubtitlesWithoutAddingCaptions()
    {
        var segment = Segment(0, 300);
        segment.SubtitleTreatment = SubtitleTreatment.Blur;
        var spec = Assert.Single(SingleVideoExportPlanner.Build(_video, new[] { segment },
            new Dictionary<Guid, Guid?> { [segment.Id] = null }).Segments);

        Assert.True(spec.HasHardSubtitle);
        Assert.Equal(nameof(MaskStyle.Blur), spec.MaskStyleRaw);
        Assert.False(spec.IsVoiceLocked); // 引擎允许遮挡；空音频路径仍保留原声。
        Assert.Null(spec.DubAudioPath);
        Assert.Empty(spec.CaptionLines);
        var graph = DubSegmentGraphBuilder.Build(SubtitleMaskMode.Blur, spec.StartFrame, spec.EndFrame,
            spec.Fps, 1080, 1920, PixelRect.From(spec.MaskRect, 1080, 1920),
            Array.Empty<CaptionOverlay>(), keepOriginalAudio: true, dubAudioInputIndex: 1,
            freezePadFrames: 0, trailingSilence: 0);
        Assert.Contains("boxblur=", graph.FilterComplex);
        Assert.Contains("[0:a]", graph.FilterComplex);
    }

    [Theory]
    [InlineData("boundary")]
    [InlineData("text")]
    [InlineData("missing")]
    [InlineData("pending")]
    public void Build_RejectsUnusableExplicitDubInsteadOfFallingBack(string failure)
    {
        var segment = Segment(0, 300);
        var dub = GeneratedDub(segment);
        if (failure == "boundary") dub.GeneratedForEndFrame--;
        if (failure == "text") dub.RewrittenText += "新台词";
        if (failure == "missing") dub.AudioFilePath = Path.Combine(_directory, "missing.m4a");
        if (failure == "pending") dub.Status = SegmentDubStatus.Pending;

        var error = Assert.Throws<DubException>(() => SingleVideoExportPlanner.Build(_video,
            new[] { segment }, new Dictionary<Guid, Guid?> { [segment.Id] = dub.Id }));
        Assert.Contains(failure is "boundary" or "text" ? "已过期" : "重新生成配音", error.Message);
    }

    [Fact]
    public void Build_UsesSelectedDubAndAddsCaptionFallbackWithoutExtendingPicture()
    {
        var segment = Segment(0, 300);
        var dub = GeneratedDub(segment);
        dub.FreezePadFrames = 12;
        var spec = Assert.Single(SingleVideoExportPlanner.Build(_video, new[] { segment },
            new Dictionary<Guid, Guid?> { [segment.Id] = dub.Id }).Segments);

        Assert.Equal(dub.AudioFilePath, spec.DubAudioPath);
        Assert.False(spec.IsVoiceLocked);
        Assert.Equal(0, spec.FreezePadFrames);
        var caption = Assert.Single(spec.CaptionLines);
        Assert.Equal(dub.RewrittenText, caption.Text);
        Assert.Equal(10, caption.End);
    }

    [Fact]
    public void Build_RejectsOverlappingSegments()
    {
        var error = Assert.Throws<DubException>(() => SingleVideoExportPlanner.Build(_video,
            new[] { Segment(0, 150), Segment(120, 300) }, new Dictionary<Guid, Guid?>()));
        Assert.Contains("重叠", error.Message);
    }

    private Segment Segment(int start, int end) => new()
    {
        Video = _video, VideoId = _video.Id, StartFrame = start, EndFrame = end,
        Fps = _video.Fps, SegmentIndex = $"seg_{start}", Text = "原台词"
    };

    private SegmentDub GeneratedDub(Segment segment)
    {
        var audio = Path.Combine(_directory, $"{Guid.NewGuid():N}.m4a");
        File.WriteAllText(audio, "planner only checks file presence");
        var dub = new SegmentDub
        {
            SegmentId = segment.Id, AudioFilePath = audio, RewrittenText = "新的配音台词",
            Status = SegmentDubStatus.Generated, GeneratedForStartFrame = segment.StartFrame,
            GeneratedForEndFrame = segment.EndFrame,
            GeneratedForTextHash = DubbingViewModel.TextHash("新的配音台词")
        };
        segment.SegmentDubs.Add(dub);
        return dub;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
