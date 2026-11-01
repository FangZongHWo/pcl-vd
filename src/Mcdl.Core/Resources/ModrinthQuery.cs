using System.Text;

namespace Mcdl.Core.Resources;

/// <summary>
/// Modrinth 的搜索只认英文标题，中文关键词（比如「机械动力」）会直接 0 结果。
/// 这里放一张常见模组的中英对照表，搜索前先翻译一次；认不出来的原样交给 API。
/// </summary>
public static class ModrinthQuery
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["机械动力"] = "create",
        ["机械动力：飞轮"] = "flywheel",
        ["飞轮"] = "flywheel",
        ["钠"] = "sodium",
        ["钠优化"] = "sodium",
        ["地形优化"] = "sodium",
        ["虹彩"] = "iris",
        ["光影前置"] = "iris",
        ["物品管理器"] = "jei",
        ["苹果皮"] = "appleskin",
        ["小地图"] = "xaero",
        ["旅行地图"] = "journeymap",
        ["通用机械"] = "mekanism",
        ["匠魂"] = "tinkers",
        ["暮色森林"] = "twilight forest",
        ["拔刀剑"] = "slashblade",
        ["区块预生成"] = "chunky",
        ["附魔描述"] = "enchdesc",
        ["建筑魔杖"] = "construction wand",
    };

    public static bool HasCjk(string? text)
        => !string.IsNullOrEmpty(text) && text.Any(c => c is >= '\u4e00' and <= '\u9fff');

    /// <summary>
    /// 把中文关键词换成 Modrinth 上能搜到的英文词。
    /// 顺序：内置高频表 → PCL 的 26k 条 MC 百科对照表（模糊匹配）→ 原样返回。
    /// </summary>
    public static string Translate(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0 || !HasCjk(trimmed)) return query;

        if (Aliases.TryGetValue(trimmed, out var exact)) return exact;

        var hit = WikiDatabase.Search(trimmed, 1);
        if (hit.Count > 0) return hit[0].Entry.SearchTerm;

        // 没命中整串时按包含关系退一步，例如「机械动力附属」
        foreach (var (cn, en) in Aliases)
            if (trimmed.Contains(cn, StringComparison.Ordinal))
                return en;

        return query;
    }

    public static string? SuggestEnglish(string query)
    {
        if (Aliases.TryGetValue(query.Trim(), out var en)) return en;

        var hit = WikiDatabase.Search(query.Trim(), 1);
        return hit.Count > 0 ? hit[0].Entry.SearchTerm : null;
    }

    public static IReadOnlyDictionary<string, string> All => Aliases;
}
