namespace MixCut.ViewModels;

/// <summary>侧边栏导航项。对应 macOS 版 NavigationItem。</summary>
public enum NavigationItem
{
    Overview,
    ImportMedia,
    SegmentLibrary,
    Schemes,
    // issue #22：BGM 库放在「混剪方案」和「导出」之间（对齐 mac，⌘6 → Ctrl+6 但按枚举序=Ctrl+5 后一位）。
    // 注意插入会使 Export 的序号 +1，AppSettings.LastNavItem 存的旧序号会错位一次，无实害。
    BgmLibrary,
    Export,
    SingleVideo,
}

public static class NavigationItemExtensions
{
    public static string Label(this NavigationItem item) => item switch
    {
        NavigationItem.Overview => "项目概览",
        NavigationItem.ImportMedia => "素材导入",
        NavigationItem.SegmentLibrary => "分镜素材库",
        NavigationItem.Schemes => "混剪方案",
        NavigationItem.BgmLibrary => "BGM 库",
        NavigationItem.SingleVideo => "视频翻译",
        NavigationItem.Export => "导出",
        _ => item.ToString(),
    };

    /// <summary>导航项前缀图标（对齐 macOS 版 SF Symbol，但用 emoji 等价物）。</summary>
    public static string Icon(this NavigationItem item) => item switch
    {
        NavigationItem.Overview => "▦", // rectangle.3.group
        NavigationItem.ImportMedia => "⬇", // square.and.arrow.down
        NavigationItem.SegmentLibrary => "🎞", // film.stack
        NavigationItem.Schemes => "📋", // list.bullet.clipboard
        NavigationItem.BgmLibrary => "🎵", // music.note.list
        NavigationItem.SingleVideo => "💬",
        NavigationItem.Export => "⬆", // square.and.arrow.up
        _ => string.Empty,
    };

    /// <summary>带图标的 Label，例如 "▦  项目概览"。</summary>
    public static string LabelWithIcon(this NavigationItem item) => $"{item.Icon()}  {item.Label()}";

    public static IReadOnlyList<NavigationItem> All { get; } = Enum.GetValues<NavigationItem>();
}
