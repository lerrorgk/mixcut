using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MixCut.Services.AI;
using MixCut.Services.Dubbing;
using MixCut.Services.VideoProcessing;
using MixCut.Services.VideoTranslation;
using Xunit;

namespace MixCut.Tests.Services.VideoTranslation;

/// <summary>
/// 批量翻译的 ID 契约：模型乱序可以接受，但重复 / 缺条 / 未知 / 空译文必须整批拒绝；
/// 取消时不得把半批译文写进草稿（否则用户会以为翻译成功了一半）。
/// 全部使用假 provider，不触网、不调用付费 API、不碰数据库。
/// </summary>
public sealed class BatchTranslationMappingTests
{
    private static TranslationService Translator(string json) =>
        new(() => new FakeProvider(json), NullLogger<TranslationService>.Instance);

    [Fact]
    public async Task Batch_AcceptsOutOfOrderIds_AndMapsByCueId()
    {
        // 请求 1/2/3，模型按 3/1/2 顺序返回：必须按 id 落位，不能按返回顺序。
        const string json =
            """{"items":[{"id":3,"translation":"三"},{"id":1,"translation":"一"},{"id":2,"translation":"二"}]}""";
        var map = await Translator(json).TranslateBatchAsync(
            new[] { (1, "one"), (2, "two"), (3, "three") }, "English");

        Assert.Equal(3, map.Count);
        Assert.Equal("一", map[1]);
        Assert.Equal("二", map[2]);
        Assert.Equal("三", map[3]);
    }

    [Fact]
    public async Task Batch_RejectsDuplicateId()
    {
        const string json = """{"items":[{"id":1,"translation":"一"},{"id":1,"translation":"又一一"}]}""";
        var ex = await Assert.ThrowsAsync<DubException>(() => Translator(json).TranslateBatchAsync(
            new[] { (1, "one"), (2, "two") }, "English"));
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public async Task Batch_RejectsUnknownId()
    {
        const string json = """{"items":[{"id":1,"translation":"一"},{"id":9,"translation":"凭空多出来的"}]}""";
        var ex = await Assert.ThrowsAsync<DubException>(() => Translator(json).TranslateBatchAsync(
            new[] { (1, "one") }, "English"));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task Batch_RejectsMissingId()
    {
        const string json = """{"items":[{"id":1,"translation":"一"}]}""";
        var ex = await Assert.ThrowsAsync<DubException>(() => Translator(json).TranslateBatchAsync(
            new[] { (1, "one"), (2, "two") }, "English"));
        Assert.Contains("没拿到译文", ex.Message);
    }

    [Fact]
    public async Task Batch_RejectsEmptyTranslation()
    {
        const string json = """{"items":[{"id":1,"translation":""},{"id":2,"translation":"二"}]}""";
        var ex = await Assert.ThrowsAsync<DubException>(() => Translator(json).TranslateBatchAsync(
            new[] { (1, "one"), (2, "two") }, "English"));
        Assert.Contains("空的", ex.Message);
    }

    [Fact]
    public async Task Cancel_KeepsPreviousTranslations()
    {
        var source = Path.GetTempFileName();
        try
        {
            var doc = new VideoTranslationDocument
            {
                SourcePath = source,
                SourceHash = new string('a', 64),
                Duration = 6,
                TargetLanguage = "English",
                TranslatedLanguage = "English",
                Cues =
                {
                    new TranslationCue { Id = 1, Start = 0, End = 1, OriginalText = "one", TranslatedText = "旧一" },
                    new TranslationCue { Id = 2, Start = 1, End = 2, OriginalText = "two", TranslatedText = "旧二" },
                },
            };
            var service = new VideoTranslationService(
                new FFmpegRunner(NullLogger<FFmpegRunner>.Instance),
                null!,
                Translator("{}"),
                NullLogger<VideoTranslationService>.Instance);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.TranslateAsync(doc, null, cts.Token));

            // 取消后既不改动任何一条已有译文，也不得把「已翻译」的语言标记改掉。
            Assert.Equal("旧一", doc.Cues[0].TranslatedText);
            Assert.Equal("旧二", doc.Cues[1].TranslatedText);
            Assert.Equal("English", doc.TranslatedLanguage);
        }
        finally { File.Delete(source); }
    }

    private sealed class FakeProvider(string json) : IAiProvider
    {
        public Task<T> GenerateJsonAsync<T>(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

        public Task<string> GenerateTextAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(json);
    }
}
