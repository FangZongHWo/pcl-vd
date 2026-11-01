using System.Text.Json;
using Mcdl.Core.Net;

namespace Mcdl.Core.Versions;

public sealed class InstallReport
{
    public string VersionId { get; set; } = "";
    public int Downloaded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
    public long JavaMajor { get; set; } = 8;
    public bool Success => Failed == 0;
}

public sealed class InstallOptions
{
    /// <summary>数据源：自动 / 官方 / BMCLAPI 镜像。</summary>
    public SourceKind Source { get; init; } = SourceKind.Auto;

    public int MaxThreads { get; init; } = 32;

    /// <summary>调试/冒烟测试用：跳过几百 MB 的资源对象。</summary>
    public bool SkipAssets { get; init; }

    /// <summary>版本隔离：整合包内容装进 versions/&lt;id&gt; 还是 .minecraft 根目录。</summary>
    public bool Isolated { get; init; } = true;
}

/// <summary>
/// 原版安装：版本 JSON → 客户端 jar → 依赖库 → 资源索引 → 资源对象 → 日志配置。
/// 只做下载与落盘，natives 解压交给真正启动游戏的启动器（官方启动器 / Prism 都会自己解）。
/// </summary>
public sealed class VanillaInstaller(MinecraftPaths paths, InstallOptions options)
{
    public Action<string>? Log { get; init; }
    public Action<string, DownloadProgress>? Progress { get; init; }

    private readonly SourceSet _sources = SourceSet.Of(options.Source);
    private long _baseDone;
    private long _baseTotal;

    public async Task<InstallReport> InstallAsync(VersionEntry entry, CancellationToken ct = default)
    {
        var report = new InstallReport { VersionId = entry.Id };

        var jsonUrls = _sources.VersionJsonUrls(entry.Id, entry.Url);
        Log?.Invoke($"数据源：{_sources.DisplayName}；读取版本 JSON：{jsonUrls[0]}");
        var jsonText = await HttpUtil.GetStringAsync(jsonUrls, ct).ConfigureAwait(false);
        var version = JsonSerializer.Deserialize<VersionJson>(jsonText, HttpUtil.JsonOptions)
                      ?? throw new InvalidDataException($"版本 JSON 解析失败：{entry.Id}");

        report.JavaMajor = version.RequiredJavaMajor;

        paths.EnsureBase();
        paths.EnsureLauncherProfiles();
        Directory.CreateDirectory(paths.VersionDir(entry.Id));
        await File.WriteAllTextAsync(paths.VersionJsonPath(entry.Id), jsonText, ct).ConfigureAwait(false);

        // ---- 阶段一：客户端 + 依赖库 + 索引 + 日志配置 ----
        var core = BuildCoreFiles(version, entry.Id, report);
        Log?.Invoke($"共 {core.Count} 个文件待处理（含依赖库）");
        var first = await RunPhaseAsync("客户端与依赖库", core, ct).ConfigureAwait(false);

        // ---- 阶段二：资源对象 ----
        report.Downloaded += first.Downloaded;
        report.Skipped += first.Skipped;
        report.Failed += first.Failed;
        report.Errors.AddRange(first.Errors);

        if (options.SkipAssets)
        {
            Log?.Invoke("已跳过资源文件（调试模式）");
            return report;
        }

        var index = await LoadAssetIndexAsync(version, ct).ConfigureAwait(false);
        if (index is not null)
        {
            var assetFiles = BuildAssetFiles(index);
            Log?.Invoke($"资源对象 {assetFiles.Count} 个");
            var second = await RunPhaseAsync("资源文件", assetFiles, ct).ConfigureAwait(false);

            report.Downloaded += second.Downloaded;
            report.Skipped += second.Skipped;
            report.Failed += second.Failed;
            report.Errors.AddRange(second.Errors);
        }

        return report;
    }

    private async Task<DownloadResult> RunPhaseAsync(string phase, List<DownloadFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return new DownloadResult(0, 0, 0, Array.Empty<string>());

        var last = new DownloadProgress(0, 0, 0, "", 0);
        var downloader = new SegmentedDownloader
        {
            MaxThreads = options.MaxThreads,
            Log = Log,
        };

        var result = await downloader.DownloadAsync(files, p =>
        {
            last = p;
            Progress?.Invoke(phase, p with
            {
                DoneBytes = _baseDone + p.DoneBytes,
                TotalBytes = _baseTotal + p.TotalBytes,
            });
        }, ct).ConfigureAwait(false);

        _baseDone += last.DoneBytes;
        _baseTotal += last.TotalBytes;
        return result;
    }

    // ---------- 文件清单 ----------

    private List<DownloadFile> BuildCoreFiles(VersionJson version, string id, InstallReport report)
    {
        var list = new List<DownloadFile>();

        if (version.Downloads?.Client is { Url: { Length: > 0 } } client)
        {
            list.Add(MakeFile($"{id}.jar", paths.VersionJarPath(id),
                _sources.Packaged(client.Url!), client.Size, client.Sha1));
        }

        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;

            if (lib.Downloads?.Artifact is { Url: { Length: > 0 } } artifact)
            {
                var rel = artifact.Path ?? lib.MavenPath();
                var source = artifact.Url.StartsWith(SourceSet.OfficialLibraries, StringComparison.OrdinalIgnoreCase)
                    ? _sources.Library(rel)
                    : new SourceFile(new[] { artifact.Url }, !_sources.MirrorFirst);
                list.Add(MakeFile(Path.GetFileName(rel), LibraryPath(rel), source, artifact.Size, artifact.Sha1));
                continue;
            }

            // 老式 natives（1.13 ~ 1.18）：natives 映射到 classifiers
            if (lib.Natives is { Count: > 0 } && lib.Downloads?.Classifiers is { Count: > 0 } classifiers)
            {
                var key = ResolveNativesKey(lib.Natives);
                if (key is not null && classifiers.TryGetValue(key, out var native) && native.Url is { Length: > 0 })
                {
                    var rel = native.Path ?? lib.MavenPath();
                    var source = native.Url.StartsWith(SourceSet.OfficialLibraries, StringComparison.OrdinalIgnoreCase)
                        ? _sources.Library(rel)
                        : new SourceFile(new[] { native.Url }, !_sources.MirrorFirst);
                    list.Add(MakeFile(Path.GetFileName(rel), LibraryPath(rel), source, native.Size, native.Sha1));
                }
                continue;
            }

            // Fabric / Quilt 生成的 profile：只有 maven 坐标和 maven 根地址
            if (lib.Url is { Length: > 0 } mavenRoot)
            {
                var rel = lib.MavenPath();
                list.Add(MakeFile(Path.GetFileName(rel), LibraryPath(rel),
                    _sources.MavenOn(mavenRoot, rel), lib.Size, lib.Sha1));
            }
        }

        if (version.AssetIndex is { Url: { Length: > 0 } } indexRef)
        {
            list.Add(MakeFile($"{indexRef.Id}.json", Path.Combine(paths.Indexes, indexRef.Id + ".json"),
                _sources.Packaged(indexRef.Url!), indexRef.Size, indexRef.Sha1));
        }

        if (version.Logging?.Client?.File is { Url: { Length: > 0 } } logFile)
        {
            var fileName = logFile.Path ?? "client.xml";
            list.Add(MakeFile(fileName, Path.Combine(paths.LogConfigs, fileName),
                _sources.Packaged(logFile.Url!), logFile.Size, logFile.Sha1));
        }

        return list;
    }

    private string LibraryPath(string relative)
        => Path.Combine(paths.Libraries, relative.Replace('/', Path.DirectorySeparatorChar));

    private static DownloadFile MakeFile(string name, string target, SourceFile source, long size, string? sha1)
        => new()
        {
            Name = name,
            TargetPath = target,
            Urls = source.Urls,
            Size = size,
            Sha1 = sha1,
            AllowSplit = source.AllowSplit,
        };

    private string? ResolveNativesKey(Dictionary<string, string> natives)
    {
        if (!natives.TryGetValue(RuleEvaluator.CurrentOsName, out var template) || string.IsNullOrEmpty(template))
            return null;

        // 形如 natives-windows-${arch}，arch 在 64 位系统上是 64
        var arch = RuleEvaluator.CurrentOsArch == "x86" ? "32" : "64";
        return template.Replace("${arch}", arch);
    }

    private async Task<AssetIndex?> LoadAssetIndexAsync(VersionJson version, CancellationToken ct)
    {
        if (version.AssetIndex is null) return null;

        var path = Path.Combine(paths.Indexes, version.AssetIndex.Id + ".json");
        if (!File.Exists(path)) return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AssetIndex>(stream, HttpUtil.JsonOptions, ct).ConfigureAwait(false);
    }

    private List<DownloadFile> BuildAssetFiles(AssetIndex index)
    {
        var list = new List<DownloadFile>(index.Objects.Count);
        foreach (var (name, obj) in index.Objects)
        {
            if (obj.Hash.Length < 2) continue;
            var source = _sources.Asset(obj.Hash);
            list.Add(new DownloadFile
            {
                Name = name,
                TargetPath = Path.Combine(paths.Objects, obj.Hash[..2], obj.Hash),
                Urls = source.Urls,
                Size = obj.Size,
                Sha1 = obj.Hash,
                AllowSplit = source.AllowSplit,
            });
        }

        return list;
    }
}
