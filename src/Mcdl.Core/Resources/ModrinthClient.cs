using System.Diagnostics;
using System.Text.Json;

namespace Mcdl.Core.Resources;

/// <summary>
/// Modrinth API v2 客户端。免 key，限流 300 次/分钟（响应头 X-Ratelimit-Remaining 实测为 300），
/// 这里用「两次请求之间至少隔 200ms」的方式自限速，避免整合包批量拉依赖时被 429。
/// </summary>
public sealed class ModrinthClient
{
    public const string Base = "https://api.modrinth.com/v2";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _lastCallMs = -1000;

    public Action<string>? Log { get; init; }

    private static async Task<string> GetAsync(string path, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = 200 - (Clock.ElapsedMilliseconds - _lastCallMs);
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            _lastCallMs = Clock.ElapsedMilliseconds;
            return await HttpUtil.GetStringAsync(Base + path, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, HttpUtil.JsonOptions)
        ?? throw new InvalidDataException("Modrinth 返回内容无法解析");

    // ---------- 搜索 ----------

    public async Task<ModrinthSearchResponse> SearchAsync(
        string query,
        ResourceType type,
        string? gameVersion = null,
        string? loader = null,
        int offset = 0,
        int limit = 20,
        string index = "relevance",
        bool releasesOnly = false,
        CancellationToken ct = default)
    {
        var and = new List<string> { $"[\"project_type:{type.ApiName()}\"]" };

        if (type is ResourceType.Mod or ResourceType.Modpack)
        {
            if (loader is { Length: > 0 })
                and.Add($"[\"categories:{loader}\"]");
        }

        if (gameVersion is { Length: > 0 })
            and.Add($"[\"versions:{gameVersion}\"]");

        var facets = Uri.EscapeDataString("[" + string.Join(",", and) + "]");
        var path = $"/search?query={Uri.EscapeDataString(query ?? "")}" +
                   $"&facets={facets}&index={index}&offset={offset}&limit={limit}";

        var result = Parse<ModrinthSearchResponse>(await GetAsync(path, ct).ConfigureAwait(false));

        if (releasesOnly)
            Log?.Invoke("提示：搜索结果按项目返回，正式版过滤在版本级做");

        return result;
    }

    // ---------- 项目 / 版本 ----------

    public async Task<ModrinthProject> GetProjectAsync(string idOrSlug, CancellationToken ct = default)
        => Parse<ModrinthProject>(await GetAsync($"/project/{Uri.EscapeDataString(idOrSlug)}", ct).ConfigureAwait(false));

    public async Task<List<ModrinthProject>> GetProjectsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new List<ModrinthProject>();

        var result = new List<ModrinthProject>();
        // 批量接口一次最多 1000 个 id，这里按 100 分批，URL 才不至于太长
        foreach (var chunk in list.Chunk(100))
        {
            var idsJson = Uri.EscapeDataString(JsonSerializer.Serialize(chunk));
            var text = await GetAsync($"/projects?ids={idsJson}", ct).ConfigureAwait(false);
            result.AddRange(Parse<List<ModrinthProject>>(text));
        }

        return result;
    }

    public async Task<List<ModrinthVersion>> GetVersionsAsync(
        string projectId, string? gameVersion = null, string? loader = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (gameVersion is { Length: > 0 })
            query.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        if (loader is { Length: > 0 })
            query.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));

        var path = $"/project/{Uri.EscapeDataString(projectId)}/version" +
                   (query.Count > 0 ? "?" + string.Join("&", query) : "");

        return Parse<List<ModrinthVersion>>(await GetAsync(path, ct).ConfigureAwait(false));
    }

    public async Task<ModrinthVersion?> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        var list = await GetVersionsByIdsAsync(new[] { versionId }, ct).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    public async Task<List<ModrinthVersion>> GetVersionsByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new List<ModrinthVersion>();

        var result = new List<ModrinthVersion>();
        foreach (var chunk in list.Chunk(100))
        {
            var idsJson = Uri.EscapeDataString(JsonSerializer.Serialize(chunk));
            var text = await GetAsync($"/versions?ids={idsJson}", ct).ConfigureAwait(false);
            result.AddRange(Parse<List<ModrinthVersion>>(text));
        }

        return result;
    }

    /// <summary>
    /// 按实例的 MC 版本（以及加载器）筛可安装版本。
    /// 注意：光影 / 资源包 / 数据包没有"加载器"概念，带上 loader 过滤会被筛空。
    /// </summary>
    public Task<List<ModrinthVersion>> GetVersionsForAsync(
        InstanceContext instance, ModrinthProject project, CancellationToken ct = default)
    {
        var loader = project.Type is ResourceType.Mod or ResourceType.Modpack ? instance.Loader : null;
        return GetVersionsAsync(project.Id, instance.MinecraftVersion, loader, ct);
    }

    /// <summary>挑一个和目标版本/加载器匹配的最佳版本：正式版优先，其次按发布时间倒序。</summary>
    public static ModrinthVersion? PickBest(IEnumerable<ModrinthVersion> versions)
        => versions
            .OrderByDescending(v => v.IsRelease)
            .ThenByDescending(v => v.DatePublished)
            .FirstOrDefault();
}
