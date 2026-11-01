using System.Text.Json;
using System.Text.Json.Serialization;
using Mcdl.Core.Versions;

namespace Mcdl.Core;

public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<VersionEntry> Versions { get; set; } = new();
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
}

public sealed class VersionEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset ReleaseTime { get; set; }
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("complianceLevel")] public int ComplianceLevel { get; set; }

    [JsonIgnore] public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsOldAlphaBeta => string.Equals(Type, "old_alpha", StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(Type, "old_beta", StringComparison.OrdinalIgnoreCase);
}

public static class MojangApi
{
    public const string VersionManifestV2 = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string ResourcesBase = "https://resources.download.minecraft.net/";
    public const string JavaRuntimeAll = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
    public const string FabricMeta = "https://meta.fabricmc.net/v2";
    public const string ForgeMavenMetadata = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    public const string NeoForgeMavenMetadata = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
}

public static class HttpUtil
{
    public static readonly HttpClient Shared = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 64,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mcdl/0.1 (+https://github.com/)");
        return client;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static async Task<T> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        using var resp = await Shared.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        return value ?? throw new InvalidDataException($"无法解析 JSON：{url}");
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        => await Shared.GetStringAsync(url, ct).ConfigureAwait(false);

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
        => await Shared.GetByteArrayAsync(url, ct).ConfigureAwait(false);

    /// <summary>按顺序尝试多个地址（官方 / 镜像），第一个成功即返回。</summary>
    public static async Task<string> GetStringAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                return await Shared.GetStringAsync(url, ct).ConfigureAwait(false);
            }
            // HttpClient 的超时会以 TaskCanceledException 形式抛出，不能当成"调用方取消"直接往外丢，
            // 否则镜像抖一下就整个失败
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("没有可用的下载地址");
    }

    public static async Task<T> GetJsonAsync<T>(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        var text = await GetStringAsync(urls, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)
               ?? throw new InvalidDataException($"无法解析 JSON：{urls[0]}");
    }

    public static Task<VersionManifest> LoadManifestAsync(CancellationToken ct = default)
        => LoadManifestAsync(SourceSet.Of(SourceKind.Auto).ManifestUrls, ct);

    public static Task<VersionManifest> LoadManifestAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
        => GetJsonAsync<VersionManifest>(urls, ct);
}
