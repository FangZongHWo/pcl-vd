using System.Reflection;
using System.Text;

namespace Mcdl.Core.Resources;

/// <summary>一条 MC 百科词条：中文名 + 各平台 slug。</summary>
public sealed record WikiEntry(string? ChineseName, string Slug, string? CurseSlug)
{
    /// <summary>给 Modrinth 搜索用的英文关键词。</summary>
    public string SearchTerm => Slug.Replace('-', ' ').Replace('_', ' ').Trim();

    public override string ToString() => $"{ChineseName}  ({Slug})";
}

public sealed record WikiHit(WikiEntry Entry, double Similarity, bool Exact);

/// <summary>
/// PCL 的 WikiEntries.txt：26k 行「中文名 → 平台 slug」对照表，格式沿用 PCL 的定义：
/// <list type="bullet">
/// <item>一行可含多个写法，用 <c>¨</c> 分隔</item>
/// <item>每段 <c>slug|中文名</c>；slug 侧 <c>@</c> 表示平台：前缀=仅 Modrinth，后缀=CF/MR 同 slug，<c>a@b</c>=CF 用 a、MR 用 b</item>
/// <item>中文名里的 <c>*</c> 展开为由 slug 生成的英文名</item>
/// <item>最后一行是浏览量的 86 进制压缩表，这里不解析（排序用下载量）</item>
/// </list>
/// 匹配算法照抄 PCL 的 <c>SearchSimilarity</c>：贪心最长公共子串覆盖 + 长度/位置加权，不是编辑距离。
/// </summary>
public static class WikiDatabase
{
    private const string ResourceName = "Mcdl.Core.Resources.WikiEntries.txt";

    private static readonly Lazy<List<WikiEntry>> Entries = new(Load);

    public static IReadOnlyList<WikiEntry> All => Entries.Value;

    public static WikiEntry? Best(string query) => Search(query, 1).FirstOrDefault()?.Entry;

    public static IReadOnlyList<WikiHit> Search(string query, int max = 5, double minSimilarity = 0.25)
    {
        query = query.Trim().ToLowerInvariant();
        if (query.Length == 0) return Array.Empty<WikiHit>();

        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<WikiHit>();

        foreach (var entry in Entries.Value)
        {
            var sources = BuildSources(entry);
            var similarity = WeightedSimilarity(sources, query);

            var exact = parts.Length > 0 && parts.All(p =>
                sources.Any(s => s.Aliases.Any(a =>
                    a.Replace(" ", "").Contains(p, StringComparison.OrdinalIgnoreCase))));

            if (!exact && similarity < minSimilarity) continue;
            hits.Add(new WikiHit(entry, similarity, exact));
        }

        return hits
            .OrderByDescending(h => h.Exact)
            .ThenByDescending(h => h.Similarity)
            .Take(max)
            .ToList();
    }

    // ---------- 加载 ----------

    private static List<WikiEntry> Load()
    {
        var result = new List<WikiEntry>(32000);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null) return result;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                // 浏览量压缩行：没有任何 | 或 ¨ 的纯字符行
                if (!line.Contains('|')) continue;

                foreach (var chunk in line.Split('¨'))
                {
                    var parsed = ParseEntry(chunk);
                    if (parsed is not null) result.Add(parsed);
                }
            }
        }
        catch
        {
            // 表损坏时退化为空表，搜索退回「原样交给 API」
        }

        return result;
    }

    private static WikiEntry? ParseEntry(string chunk)
    {
        var parts = chunk.Split('|');
        if (parts.Length < 2) return null;

        var slugs = parts[0].Trim();
        var name = parts[^1].Trim();
        if (slugs.Length == 0 || name.Length == 0) return null;

        string cf, mr;
        if (slugs.StartsWith('@'))
        {
            mr = slugs[1..];
            cf = mr;
        }
        else if (slugs.EndsWith('@'))
        {
            cf = slugs[..^1];
            mr = cf;
        }
        else if (slugs.Contains('@'))
        {
            var split = slugs.Split('@');
            cf = split[0];
            mr = split.Length > 1 ? split[1] : split[0];
        }
        else
        {
            cf = slugs;
            mr = slugs;
        }

        if (name.Contains('*'))
        {
            var english = mr.Replace("-", " ");
            english = english.Length > 0 ? char.ToUpperInvariant(english[0]) + english[1..] : english;
            name = name.Replace("*", $" ({english})");
        }

        return new WikiEntry(name, mr, cf);
    }

    // ---------- 相似度（移植自 PCL ModBase.vb 的 SearchSimilarity） ----------

    private sealed record Source(string[] Aliases, double Weight);

    private static List<Source> BuildSources(WikiEntry entry)
    {
        var sources = new List<Source>(2);

        if (entry.ChineseName is { Length: > 0 } name)
        {
            var cn = name.Contains(" (") ? name[..name.IndexOf(" (", StringComparison.Ordinal)] : name;
            sources.Add(new Source(cn.Split('/', StringSplitOptions.RemoveEmptyEntries), 1));

            var en = name.Contains(" (") ? name[(name.IndexOf(" (", StringComparison.Ordinal) + 2)..].TrimEnd(')') : "";
            sources.Add(new Source(new[] { (en + entry.Slug).Trim() }, 0.5));
        }
        else
        {
            sources.Add(new Source(new[] { entry.Slug }, 0.5));
        }

        return sources;
    }

    private static double WeightedSimilarity(List<Source> sources, string query)
    {
        double sum = 0;
        foreach (var source in sources)
            if (source.Aliases.Length > 0)
                sum += source.Aliases.Max(a => Similarity(a, query)) * source.Weight;
        return sum;
    }

    /// <summary>贪心最长公共子串覆盖：命中一段就从源里删掉一段，按长度与位置加权。</summary>
    private static double Similarity(string source, string query)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(query)) return 0;

        var text = new StringBuilder(source.ToLowerInvariant().Replace(" ", ""));
        query = query.ToLowerInvariant().Replace(" ", "");

        int qp = 0, queryLength = query.Length, sourceLength = text.Length;
        if (queryLength == 0) return 0;

        double lenSum = 0;
        while (qp < queryLength)
        {
            int sp = 0, lenMax = 0, spMax = 0;
            var currentLength = text.Length;

            while (sp < currentLength)
            {
                var len = 0;
                while (qp + len < queryLength && sp + len < currentLength && text[sp + len] == query[qp + len])
                    len++;

                if (len > lenMax)
                {
                    lenMax = len;
                    spMax = sp;
                }

                sp += len > 0 ? len : 1;
            }

            if (lenMax > 0)
            {
                text.Remove(spMax, lenMax);
                var weight = Math.Pow(1.4, 3 + lenMax) - 3.6;
                weight *= 1 + 0.3 * Math.Max(0, 3 - Math.Abs(qp - spMax));
                lenSum += weight;
            }

            qp += lenMax > 0 ? lenMax : 1;
        }

        return lenSum / queryLength * (3 / Math.Sqrt(sourceLength + 15)) * (queryLength <= 2 ? 3 - queryLength : 1);
    }
}
