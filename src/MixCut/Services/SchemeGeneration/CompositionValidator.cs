using MixCut.Models;

namespace MixCut.Services.SchemeGeneration;

internal static class CompositionValidator
{
    /// <summary>整条方案必须能解析且不重复引用分镜，避免保存缺镜头或重复口播。</summary>
    internal static List<Segment>? Resolve(IReadOnlyList<string>? ids, IReadOnlyDictionary<string, Segment> catalog)
    {
        if (ids is null || ids.Count == 0) return null;
        var seen = new HashSet<Guid>();
        var resolved = new List<Segment>();
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !catalog.TryGetValue(id, out var segment) || !seen.Add(segment.Id)) return null;
            resolved.Add(segment);
        }
        return resolved;
    }
}
