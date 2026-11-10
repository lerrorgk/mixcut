using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MixCut.Models;
using MixCut.Services.AI;
using MixCut.Services.Dubbing;
using MixCut.Services.SchemeGeneration;
using MixCut.Services.VideoProcessing;
using MixCut.Services.VideoTranslation;
using Xunit;

namespace MixCut.Tests.Services.VideoTranslation;

public sealed class TranslationEditingTests
{
    [Fact]
    public async Task TranslateOne_KeepsOtherCorrections_AndIncludesNeighborContext()
    {
        var source = Path.GetTempFileName();
        try
        {
            var provider = new RecordingProvider();
            var translator = new TranslationService(() => provider, NullLogger<TranslationService>.Instance);
            var doc = new VideoTranslationDocument
            {
                SourcePath = source, Duration = 9, TargetLanguage = "English", TranslatedLanguage = "English",
                Cues = new()
                {
                    new() { Id = 1, Start = 0, End = 3, OriginalText = "品牌", TranslatedText = "My checked brand" },
                    new() { Id = 2, Start = 3, End = 6, OriginalText = "优惠", TranslatedText = "" },
                    new() { Id = 3, Start = 6, End = 9, OriginalText = "下单", TranslatedText = "My checked CTA" },
                },
            };
            var service = new VideoTranslationService(new FFmpegRunner(NullLogger<FFmpegRunner>.Instance), null!, translator,
                NullLogger<VideoTranslationService>.Instance);
            await service.TranslateCuesAsync(doc, new HashSet<int> { 2 });
            Assert.Equal("My checked brand", doc.Cues[0].TranslatedText);
            Assert.Equal("Don't miss it!\nIt's 9.9.", doc.Cues[1].TranslatedText);
            Assert.Equal("My checked CTA", doc.Cues[2].TranslatedText);
            Assert.Contains("before", provider.Prompt);
            Assert.Contains("after", provider.Prompt);
            Assert.Contains("\"id\":2", provider.Prompt);
            Assert.DoesNotContain("\"id\":3", provider.Prompt);
            Assert.True(doc.Cues.All(c => c.IsTranslatedTo("English")));
            doc.TargetLanguage = "French";
            await service.TranslateCuesAsync(doc, new HashSet<int> { 2 });
            Assert.False(doc.Cues[0].IsTranslatedTo("French", doc.TranslatedLanguage));
            Assert.True(doc.Cues[1].IsTranslatedTo("French"));
            Assert.False(doc.Cues[2].IsTranslatedTo("French", doc.TranslatedLanguage));
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public void SplitMerge_PreservesTextOrder_TimelineAndStableIds()
    {
        var cue = new TranslationCue { Id = 7, Start = 1, End = 5, OriginalText = "买一送一立即下单",
            TranslatedText = "Buy one get one. Order now!", TranslationLanguage = "English" };
        var doc = new VideoTranslationDocument { Duration = 7, Cues = new() { cue } };
        var right = TranslationCueEditor.Split(doc, cue, 4);
        Assert.Equal(7, cue.Id); Assert.Equal(8, right.Id);
        Assert.Equal(3, cue.End); Assert.Equal(cue.End, right.Start); Assert.Equal(5, right.End);
        Assert.Equal("买一送一", cue.OriginalText); Assert.Equal("立即下单", right.OriginalText);
        Assert.Equal("Buy one get one. Order now!", cue.TranslatedText + " " + right.TranslatedText);
        TranslationCueEditor.MergeNext(doc, cue);
        Assert.Single(doc.Cues); Assert.Equal(5, cue.End);
        Assert.Equal("买一送一\n立即下单", cue.OriginalText);
        Assert.Contains("Order now!", cue.TranslatedText);
    }

    [Fact]
    public void TimeEdit_ClampsToNeighborsAndDuration()
    {
        var cue = new TranslationCue { Id = 2, Start = 2, End = 4 };
        var doc = new VideoTranslationDocument { Duration = 6, Cues = new()
        { new() { Id = 1, Start = 0, End = 1 }, cue, new() { Id = 3, Start = 5, End = 6 } } };
        TranslationCueEditor.SetTime(doc, cue, -1, true);
        TranslationCueEditor.SetTime(doc, cue, 99, false);
        Assert.Equal(1, cue.Start); Assert.Equal(5, cue.End);
        Assert.Throws<ArgumentException>(() => TranslationCueEditor.SetTime(doc, cue, double.NaN, true));
    }

    [Fact]
    public void PreviewWindow_ShiftsOnlyIntersectingCues_AndKeepsPunctuationAndNewlines()
    {
        var doc = new VideoTranslationDocument { Duration = 20, TargetLanguage = "English", TranslatedLanguage = "English",
            Cues = new() { new() { Id = 1, Start = 0, End = 2 }, new() { Id = 2, Start = 7, End = 10,
                TranslatedText = "Don't!\nIt's 9.9." }, new() { Id = 3, Start = 15, End = 17 } } };
        var preview = VideoTranslationService.CreatePreviewWindow(doc, 8, 6);
        Assert.Single(preview.Cues); Assert.Equal(0, preview.Cues[0].Start); Assert.Equal(2, preview.Cues[0].End);
        Assert.Equal("Don't!\nIt's 9.9.", preview.Cues[0].TranslatedText);
        Assert.Equal(7, doc.Cues[1].Start);
    }

    [Fact]
    public void CompositionRejectsMissingOrRepeatedSegments()
    {
        var a = new Segment(); var b = new Segment();
        var catalog = new Dictionary<string, Segment> { ["V1_01"] = a, ["V1_02"] = b };
        Assert.Null(CompositionValidator.Resolve(new[] { "V1_01", "missing" }, catalog));
        Assert.Null(CompositionValidator.Resolve(new[] { "V1_01", "V1_01" }, catalog));
        Assert.Null(CompositionValidator.Resolve(Array.Empty<string>(), catalog));
        Assert.Equal(new[] { a, b }, CompositionValidator.Resolve(new[] { "V1_01", "V1_02" }, catalog));
    }

    private sealed class RecordingProvider : IAiProvider
    {
        public string Prompt = "";
        public Task<T> GenerateJsonAsync<T>(string prompt, CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            return Task.FromResult(JsonSerializer.Deserialize<T>(
                """{"items":[{"id":2,"translation":"Don't miss it!\nIt's 9.9."}]}""",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }
        public Task<string> GenerateTextAsync(string prompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
