using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Mcdl.Core.Net;
using Mcdl.Core.Versions;

namespace Mcdl.Core.Java;

/// <summary>
/// Mojang 官方 Java 运行时清单（<c>all.json</c>）。
/// 26.x 需要 Java 25（java-runtime-epsilon），1.20.5+ 需要 21（delta），1.18~1.20.4 需要 17（gamma），
/// 1.17 需要 16（alpha），1.13~1.16 需要 8（jre-legacy）。
/// </summary>
public sealed class JavaRuntimeManager(MinecraftPaths paths)
{
    public Action<string>? Log { get; init; }
    public Action<string, DownloadProgress>? Progress { get; init; }

    private static Dictionary<string, Dictionary<string, List<JreEntry>>>? _index;

    public static string PlatformKey()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows())
            return arch == Architecture.Arm64 ? "windows-arm64" : arch == Architecture.X86 ? "windows-x86" : "windows-x64";
        if (OperatingSystem.IsMacOS())
            return arch == Architecture.Arm64 ? "mac-os-arm64" : "mac-os";
        return arch == Architecture.X86 ? "linux-i386" : "linux";
    }

    public string? ExistingJava(string component)
    {
        var dir = Path.Combine(paths.Runtime, component, PlatformKey());
        var exe = Path.Combine(dir, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>确保对应组件的 Java 运行时存在，返回 java 可执行文件路径。</summary>
    public async Task<string?> EnsureAsync(string component, CancellationToken ct = default)
    {
        var existing = ExistingJava(component);
        if (existing is not null) return existing;

        var platform = PlatformKey();
        var index = await LoadIndexAsync(ct).ConfigureAwait(false);
        if (index is null
            || !index.TryGetValue(platform, out var byComponent)
            || !byComponent.TryGetValue(component, out var entries)
            || entries.Count == 0)
        {
            Log?.Invoke($"官方清单里没有 {platform} 的 {component}");
            return null;
        }

        var entry = entries[0];
        Log?.Invoke($"下载 Java 运行时 {component} {entry.Version?.Name}（{platform}）");
        var manifestText = await HttpUtil.GetStringAsync(entry.Manifest!.Url!, ct).ConfigureAwait(false);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<JreManifest>(manifestText, HttpUtil.JsonOptions);
        if (manifest is null) return null;

        var root = Path.Combine(paths.Runtime, component, platform);
        Directory.CreateDirectory(root);

        var files = new List<DownloadFile>();
        var executables = new List<string>();

        foreach (var (relative, file) in manifest.Files)
        {
            var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (file.Type == "directory")
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var raw = file.Downloads?.Raw;
            if (file.Type != "file" || raw?.Url is null) continue;

            files.Add(new DownloadFile
            {
                Name = relative,
                TargetPath = target,
                Urls = new[] { raw.Url },
                Size = raw.Size,
                Sha1 = raw.Sha1,
            });

            if (file.Executable) executables.Add(target);
        }

        var downloader = new SegmentedDownloader { MaxThreads = 16, Log = Log };
        var result = await downloader.DownloadAsync(files, p => Progress?.Invoke("Java 运行时", p), ct)
            .ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in executables)
            {
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                              | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                              | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { /* 忽略 */ }
            }
        }

        Log?.Invoke($"Java 运行时就绪：{files.Count} 个文件，失败 {result.Failed}");
        return ExistingJava(component);
    }

    private async Task<Dictionary<string, Dictionary<string, List<JreEntry>>>?> LoadIndexAsync(CancellationToken ct)
    {
        if (_index is not null) return _index;
        try
        {
            _index = await HttpUtil.GetJsonAsync<Dictionary<string, Dictionary<string, List<JreEntry>>>>(
                MojangApi.JavaRuntimeAll, ct).ConfigureAwait(false);
            return _index;
        }
        catch (Exception ex)
        {
            Log?.Invoke("读取 Java 运行时清单失败：" + ex.Message);
            return null;
        }
    }
}

public sealed class JreEntry
{
    [JsonPropertyName("manifest")] public JreManifestRef? Manifest { get; set; }
    [JsonPropertyName("version")] public JreVersionRef? Version { get; set; }
}

public sealed class JreManifestRef
{
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class JreVersionRef
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("released")] public DateTimeOffset Released { get; set; }
}

public sealed class JreManifest
{
    [JsonPropertyName("files")] public Dictionary<string, JreFile> Files { get; set; } = new();
}

public sealed class JreFile
{
    [JsonPropertyName("type")] public string Type { get; set; } = "file";
    [JsonPropertyName("executable")] public bool Executable { get; set; }
    [JsonPropertyName("downloads")] public JreDownloads? Downloads { get; set; }
}

public sealed class JreDownloads
{
    [JsonPropertyName("raw")] public JreDownload? Raw { get; set; }
}

public sealed class JreDownload
{
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}
