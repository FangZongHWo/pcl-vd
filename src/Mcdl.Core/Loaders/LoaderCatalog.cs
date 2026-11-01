using System.Xml.Linq;
using Mcdl.Core.Versions;

namespace Mcdl.Core.Loaders;

/// <summary>
/// 模组加载器的版本清单。
/// 2026 年 Forge 的 maven 已经换成 Reposilite，旧的 promotions_slim.json 不再可用，
/// 所以这里直接读 maven-metadata.xml，版本号形如 <c>26.3-66.1.0</c>（MC 版本 + 分隔符 + 加载器版本）。
/// </summary>
public static class LoaderCatalog
{
    public static async Task<IReadOnlyList<string>> ForgeVersionsAsync(
        string mcId, SourceSet? source = null, CancellationToken ct = default)
    {
        try
        {
            var urls = (source ?? SourceSet.Of(SourceKind.Auto)).ForgeMetadataUrls;
            var xml = await HttpUtil.GetStringAsync(urls, ct).ConfigureAwait(false);
            var all = ParseMavenMetadata(xml);
            var prefix = mcId + "-";
            return all.Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(v => v[prefix.Length..])
                .OrderByDescending(v => v, VersionLikeComparer.Instance)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static async Task<IReadOnlyList<string>> NeoForgeVersionsAsync(
        string mcId, SourceSet? source = null, CancellationToken ct = default)
    {
        try
        {
            var urls = (source ?? SourceSet.Of(SourceKind.Auto)).NeoForgeMetadataUrls;
            var xml = await HttpUtil.GetStringAsync(urls, ct).ConfigureAwait(false);
            var all = ParseMavenMetadata(xml);
            var parts = mcId.Split('.');

            // 1) 1.21.4 -> 21.4.x（NeoForge 去掉了主版本号 1）
            if (mcId.StartsWith("1.", StringComparison.Ordinal) && parts.Length >= 3)
            {
                var prefix = string.Join('.', parts.Skip(1)) + ".";
                var hits = all.Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v, VersionLikeComparer.Instance)
                    .ToList();
                if (hits.Count > 0) return hits;
            }

            // 2) 26.3 -> 26.3.0.95，版本号本身就带 MC 版本，整串保留
            var dotted = mcId + ".";
            var dottedHits = all.Where(v => v.StartsWith(dotted, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v, VersionLikeComparer.Instance)
                .ToList();
            if (dottedHits.Count > 0) return dottedHits;

            // 3) 万一以后改成 Forge 那种 26.3-66.1.0 风格
            var dashed = mcId + "-";
            return all.Where(v => v.StartsWith(dashed, StringComparison.OrdinalIgnoreCase))
                .Select(v => v[dashed.Length..])
                .OrderByDescending(v => v, VersionLikeComparer.Instance)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static async Task<IReadOnlyList<FabricLoader>> FabricVersionsAsync(
        string mcId, SourceSet? source = null, CancellationToken ct = default)
    {
        try
        {
            var bases = (source ?? SourceSet.Of(SourceKind.Auto)).FabricMetaBases;
            var urls = bases
                .Select(b => $"{b}/versions/loader/{Uri.EscapeDataString(mcId)}")
                .ToArray();
            var list = await HttpUtil.GetJsonAsync<List<FabricLoaderEntry>>(urls, ct).ConfigureAwait(false);
            return list.Select(x => new FabricLoader(x.Loader.Version, x.Loader.Stable)).ToList();
        }
        catch
        {
            return Array.Empty<FabricLoader>();
        }
    }

    private static List<string> ParseMavenMetadata(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants("version").Select(e => e.Value.Trim())
                .Where(v => v.Length > 0).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}

public sealed record FabricLoader(string Version, bool Stable);

public sealed class FabricLoaderEntry
{
    public FabricLoaderInfo Loader { get; set; } = new();
}

public sealed class FabricLoaderInfo
{
    public string Version { get; set; } = "";
    public bool Stable { get; set; }
}

/// <summary>形如 66.1.0 / 47.1.3 / 21.4.112 的版本号比较。</summary>
public sealed class VersionLikeComparer : IComparer<string>
{
    public static readonly VersionLikeComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var a = Split(x);
        var b = Split(y);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var na = i < a.Count ? a[i] : 0;
            var nb = i < b.Count ? b[i] : 0;
            var c = na.CompareTo(nb);
            if (c != 0) return c;
        }

        return string.CompareOrdinal(x, y);
    }

    private static List<int> Split(string s)
    {
        var result = new List<int>();
        var current = 0;
        var has = false;
        foreach (var ch in s)
        {
            if (char.IsAsciiDigit(ch))
            {
                current = current * 10 + (ch - '0');
                has = true;
            }
            else
            {
                if (has) result.Add(current);
                current = 0;
                has = false;
            }
        }

        if (has) result.Add(current);
        return result;
    }
}
