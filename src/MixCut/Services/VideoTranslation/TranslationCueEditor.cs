using MixCut.Services.Dubbing;

namespace MixCut.Services.VideoTranslation;

/// <summary>整片字幕的局部时间与拆合编辑，不改变其他字幕的文字和稳定ID。</summary>
public static class TranslationCueEditor
{
    public static void SetTime(VideoTranslationDocument doc, TranslationCue cue, double value, bool start)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("请输入有效的秒数。");
        var index = doc.Cues.IndexOf(cue);
        if (index < 0) return;
        if (start)
        {
            var lower = index > 0 ? doc.Cues[index - 1].End : 0;
            cue.Start = Math.Clamp(value, Math.Min(lower, cue.End - 0.05), cue.End - 0.05);
        }
        else
        {
            var upper = index + 1 < doc.Cues.Count ? doc.Cues[index + 1].Start : doc.Duration;
            cue.End = Math.Clamp(value, cue.Start + 0.05, Math.Max(cue.Start + 0.05, upper));
        }
    }

    public static TranslationCue Split(VideoTranslationDocument doc, TranslationCue cue, int offset = 0)
    {
        var index = doc.Cues.IndexOf(cue);
        if (index < 0 || cue.OriginalText.Trim().Length < 2 || cue.End - cue.Start < 0.1)
            throw new DubException("这句太短，无法继续拆分。");
        var original = cue.OriginalText;
        if (offset <= 0 || offset >= original.Length) offset = SplitOffset(original, 0.5);
        var ratio = (double)offset / original.Length;
        var boundary = Math.Clamp(cue.Start + (cue.End - cue.Start) * ratio, cue.Start + 0.05, cue.End - 0.05);
        var translationOffset = SplitOffset(cue.TranslatedText, ratio);
        var canSplitTranslation = translationOffset > 0 && translationOffset < cue.TranslatedText.Length;
        var right = new TranslationCue
        {
            Id = doc.Cues.Max(c => c.Id) + 1, Start = boundary, End = cue.End,
            OriginalText = original[offset..].Trim(),
            TranslatedText = canSplitTranslation ? cue.TranslatedText[translationOffset..].Trim() : "",
            TranslationLanguage = cue.TranslationLanguage,
        };
        cue.OriginalText = original[..offset].Trim();
        cue.TranslatedText = canSplitTranslation ? cue.TranslatedText[..translationOffset].Trim() : "";
        cue.End = boundary;
        doc.Cues.Insert(index + 1, right);
        return right;
    }

    public static void MergeNext(VideoTranslationDocument doc, TranslationCue cue)
    {
        var index = doc.Cues.IndexOf(cue);
        if (index < 0 || index + 1 >= doc.Cues.Count) throw new DubException("后面没有可合并的字幕。");
        var next = doc.Cues[index + 1];
        cue.OriginalText = cue.OriginalText.TrimEnd() + "\n" + next.OriginalText.TrimStart();
        var sameLanguage = string.Equals(cue.TranslationLanguage, next.TranslationLanguage, StringComparison.OrdinalIgnoreCase);
        cue.TranslatedText = sameLanguage && !string.IsNullOrWhiteSpace(cue.TranslatedText) && !string.IsNullOrWhiteSpace(next.TranslatedText)
            ? cue.TranslatedText.TrimEnd() + "\n" + next.TranslatedText.TrimStart() : "";
        cue.End = next.End;
        doc.Cues.RemoveAt(index + 1);
    }

    private static int SplitOffset(string text, double ratio)
    {
        if (text.Length < 2) return 0;
        var target = Math.Clamp((int)Math.Round(text.Length * ratio), 1, text.Length - 1);
        var breaks = Enumerable.Range(1, text.Length - 1)
            .Where(i => char.IsWhiteSpace(text[i]) && !string.IsNullOrWhiteSpace(text[..i]) && !string.IsNullOrWhiteSpace(text[i..])).ToList();
        return breaks.Count > 0 ? breaks.MinBy(i => Math.Abs(i - target)) : target;
    }
}
