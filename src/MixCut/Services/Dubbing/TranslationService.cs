using System.Text;
using Microsoft.Extensions.Logging;
using MixCut.Services.AI;

namespace MixCut.Services.Dubbing;

/// <summary>
/// 字幕翻译：把识别/手写的台词译成目标语言，交给配音合成、逐句字幕与整片翻译使用。
/// 提供两条入口：<see cref="TranslateAsync"/> 一次译一段（单视频工作区逐段处理，用户能先修正译文再配音）；
/// <see cref="TranslateBatchAsync"/> 整片批量翻译（按 ID 回填、带前后文语境）。
/// </summary>
public sealed class TranslationService
{
    /// <summary>可选目标语言。值是传给模型的英文语言名，键是界面显示文案。</summary>
    public static readonly IReadOnlyList<(string Label, string Name)> Languages = new[]
    {
        ("英语", "English"),
        ("日语", "Japanese"),
        ("韩语", "Korean"),
        ("西班牙语", "Spanish"),
        ("葡萄牙语", "Portuguese"),
        ("法语", "French"),
        ("德语", "German"),
        ("俄语", "Russian"),
        ("阿拉伯语", "Arabic"),
        ("泰语", "Thai"),
        ("越南语", "Vietnamese"),
        ("印尼语", "Indonesian"),
    };

    private readonly Func<IAiProvider> _provider;
    private readonly ILogger<TranslationService> _logger;

    public TranslationService(AIProviderManager aiManager, ILogger<TranslationService> logger)
        : this(() => aiManager.CurrentProvider(), logger) { }

    /// <summary>便于测试直接注入假 provider，不触碰真实 API 与设置文件。</summary>
    internal TranslationService(Func<IAiProvider> provider, ILogger<TranslationService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <summary>
    /// 把一段台词译为目标语言。源文为空或不含可译内容时抛 <see cref="DubException"/>，
    /// 让调用方给出可操作提示，而不是把空译文送进配音。
    /// </summary>
    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) throw new DubException("请先填写台词，再翻译。");
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new DubException("请先选择目标语言。");

        var prompt = BuildPrompt(text, targetLanguage);
        _logger.LogInformation("[TranslateDiag] 请求翻译 源字数={Len} 目标={Lang}", text.Length, targetLanguage);

        TranslationResultDto dto;
        try
        {
            dto = await _provider().GenerateJsonAsync<TranslationResultDto>(prompt, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (DubException) { throw; }
        catch (Exception ex)
        {
            // 供应商的错误已是人话；这里只补一层「翻译失败」上下文，避免用户以为配音模块坏了。
            // 走 ExceptionTranslator 而非 ex.Message：原始 Message 可能是英文 HTTP/JSON 报错，不许外泄给用户。
            _logger.LogError(ex, "[TranslateDiag] 翻译请求失败");
            throw new DubException("翻译失败：" + MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex), ex);
        }

        var translation = dto.Translation?.Trim() ?? string.Empty;
        if (translation.Length == 0)
            throw new DubException("翻译结果为空，可能被模型安全策略拦截。请重试或在「设置 → AI 模型」换一个模型。");

        _logger.LogInformation("[TranslateDiag] 翻译完成 译文字数={Len}", translation.Length);
        return translation;
    }

    /// <summary>
    /// 整片批量翻译（整片翻译工作台用）：按 <c>Id</c> 逐条回填，带前后文语境。
    /// <para>
    /// 明确不做「缺条兜底」：只要有一条请求 id 没拿到非空译文就整批抛 <see cref="DubException"/>，
    /// 由调用方保留上一份可用译文让用户重试 —— 静默回退原文会让用户以为已经译好了。
    /// </para>
    /// </summary>
    /// <param name="items">待译条目（Id + 原文），Id 必须唯一且稳定。</param>
    /// <param name="targetLanguage">目标语言英文名（<see cref="Languages"/> 的 Name）。</param>
    /// <param name="contextLines">每条附带的前后文条数（只作语境，不翻译）。</param>
    /// <param name="onProgress">已完成条数 / 总条数回调（供 UI 细化进度）。</param>
    public async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(
        IReadOnlyList<(int Id, string Text)> items, string targetLanguage,
        int contextLines = 2, IProgress<(int Done, int Total)>? onProgress = null,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new DubException("请先选择目标语言。");

        var pending = items.Where(i => !string.IsNullOrWhiteSpace(i.Text)).ToList();
        if (pending.Count == 0) throw new DubException("没有可翻译的字幕（原文全为空）。");

        var result = new Dictionary<int, string>();
        var done = 0;

        // 分批：单次请求条数越多，模型越容易漏条；20 条在实测里是漏条率与请求数的平衡点。
        for (var offset = 0; offset < pending.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = pending.Skip(offset).Take(BatchSize).ToList();
            var chunkResult = await TranslateChunkAsync(items, chunk, targetLanguage, contextLines, ct);
            foreach (var kv in chunkResult) result[kv.Key] = kv.Value;
            done += chunk.Count;
            onProgress?.Report((done, pending.Count));
        }

        return result;
    }

    /// <summary>单次请求最大条数。</summary>
    private const int BatchSize = 20;

    private async Task<Dictionary<int, string>> TranslateChunkAsync(
        IReadOnlyList<(int Id, string Text)> all, List<(int Id, string Text)> chunk,
        string targetLanguage, int contextLines, CancellationToken ct)
    {
        var indexById = new Dictionary<int, int>();
        for (var i = 0; i < all.Count; i++) indexById[all[i].Id] = i;
        contextLines = Math.Max(0, contextLines);

        var payload = new StringBuilder();
        payload.Append("{\"items\":[");
        for (var i = 0; i < chunk.Count; i++)
        {
            var item = chunk[i];
            var idx = indexById[item.Id];
            if (i > 0) payload.Append(',');
            payload.Append("{\"id\":").Append(item.Id);
            var before = JoiningContext(all, idx - contextLines, idx - 1);
            var after = JoiningContext(all, idx + 1, idx + contextLines);
            if (before.Length > 0) payload.Append(",\"before\":").Append(JsonString(before));
            if (after.Length > 0) payload.Append(",\"after\":").Append(JsonString(after));
            payload.Append(",\"text\":").Append(JsonString(item.Text)).Append('}');
        }
        payload.Append("]}");

        var prompt = BuildBatchPrompt(payload.ToString(), targetLanguage);
        _logger.LogInformation("[TranslateDiag] 批量翻译请求 条数={Count} 目标={Lang}", chunk.Count, targetLanguage);

        BatchTranslationDto dto;
        try
        {
            dto = await _provider().GenerateJsonAsync<BatchTranslationDto>(prompt, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (DubException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TranslateDiag] 批量翻译请求失败");
            // 绝不把原生报错原文（英文 stack / HTTP 码）甩给用户，统一走人话翻译。
            throw new DubException("翻译失败：" + MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex), ex);
        }

        // 严格按请求的 id 集合校验：重复、未知、缺条、空译文全部算失败，
        // 不做「后者覆盖前者」或「多给的忽略」，否则译文会错行到别的字幕上。
        var expected = chunk.Select(c => c.Id).ToHashSet();
        var map = new Dictionary<int, string>();
        foreach (var row in dto.Items ?? new List<BatchTranslationRow>())
        {
            if (row is null) continue;

            if (!expected.Contains(row.Id))
            {
                _logger.LogWarning("[TranslateDiag] 批量翻译返回未知 id={Id}", row.Id);
                throw new DubException(
                    $"翻译结果里出现了不存在的字幕编号 {row.Id}，为避免译文错行已中止。请重试；" +
                    "持续失败可在「设置 → AI 模型」换一个模型。");
            }

            var text = row.Translation?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                throw new DubException(
                    $"第 {row.Id} 条字幕的译文是空的（可能被模型安全策略拦截）。请重试或更换模型。");
            }

            if (!map.TryAdd(row.Id, text))
            {
                _logger.LogWarning("[TranslateDiag] 批量翻译重复 id={Id}", row.Id);
                throw new DubException(
                    $"翻译结果里第 {row.Id} 条重复出现，为避免译文错行已中止。请重试；" +
                    "持续失败可在「设置 → AI 模型」换一个模型。");
            }
        }

        var missing = chunk.Where(c => !map.ContainsKey(c.Id)).Select(c => c.Id).ToList();
        if (missing.Count > 0)
        {
            _logger.LogWarning("[TranslateDiag] 批量翻译漏条 缺={Missing}", string.Join(',', missing));
            throw new DubException(
                $"有 {missing.Count} 条字幕没拿到译文（模型漏条，编号 {string.Join('、', missing.Take(8))}" +
                (missing.Count > 8 ? " 等" : "") + "）。请重试；持续失败可在「设置 → AI 模型」换一个更强的模型。");
        }

        // 一次性返回整批：调用方不会拿到半批，避免「一半新一半旧」的混合译文。
        return map;
    }

    private static string JoiningContext(IReadOnlyList<(int Id, string Text)> all, int from, int to)
    {
        var sb = new StringBuilder();
        for (var i = Math.Max(0, from); i <= Math.Min(all.Count - 1, to); i++)
        {
            if (string.IsNullOrWhiteSpace(all[i].Text)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(all[i].Text.Trim());
        }
        return sb.ToString();
    }

    private static string JsonString(string? s) => System.Text.Json.JsonSerializer.Serialize(s ?? string.Empty);

    private static string BuildPrompt(string text, string targetLanguage)
    {
        var sb = new StringBuilder();
        sb.Append("你是广告视频台词的翻译。把下面这段台词翻译成").Append(targetLanguage).Append("。\n")
          .Append("要求：\n")
          .Append("1. 只输出译文，不要解释、不要加引号、不要保留原文；\n")
          .Append("2. 译文长度尽量与原文语速相当（原文会被配音念出，过长会挤压画面时间）；\n")
          .Append("3. 保留数字、品牌名、专有名词的准确性，口语广告语气可自然化；\n")
          .Append("4. 原文可能有语音识别错字，按上下文理解为最合理的原意再翻译；\n")
          .Append("5. 输出 JSON：{\"translation\":\"译文\"}。\n\n")
          .Append("原文：\n").Append(text);
        return sb.ToString();
    }

    private static string BuildBatchPrompt(string payloadJson, string targetLanguage)
    {
        var sb = new StringBuilder();
        sb.Append("你是一名专业字幕译者。把下面 JSON 里的每一条字幕翻译成").Append(targetLanguage).Append("。\n")
          .Append("字段说明：id=字幕编号（必须原样回填），before/after=紧邻的前后文（仅供理解语境，不要翻译、不要输出），text=要翻译的台词。\n")
          .Append("要求：\n")
          .Append("1. 逐条输出，条目数量与输入完全一致，每条都要有非空译文，不允许合并、拆条或漏条；\n")
          .Append("2. 译文按上下文的连贯语义翻译，不要孤立逐字直译，但不得添加原文没有的信息；\n")
          .Append("3. 保持口语自然、贴合短视频节奏，长度尽量与原文相当（字幕显示时间由原文决定）；\n")
          .Append("4. 数字、品牌名、人名、专有名词保持准确；原文的识别错字按语义理解为合理原意；\n")
          .Append("5. 只输出 JSON，不要解释、不要代码块围栏，格式：{\"items\":[{\"id\":1,\"translation\":\"译文\"}]}\n\n")
          .Append("输入：\n").Append(payloadJson);
        return sb.ToString();
    }

    private sealed class TranslationResultDto
    {
        public string? Translation { get; set; }
    }

    private sealed class BatchTranslationDto
    {
        public List<BatchTranslationRow>? Items { get; set; }
    }

    private sealed class BatchTranslationRow
    {
        public int Id { get; set; }
        public string? Translation { get; set; }
    }
}
