using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcdl.Core.Java;
using Mcdl.Core.Loaders;
using Mcdl.Core.Net;
using Mcdl.Core.Versions;

namespace Mcdl.Core.Resources;

public sealed class ModpackInstallReport
{
    public string PackName { get; set; } = "";
    public string? InstanceVersionId { get; set; }
    public int FilesDownloaded { get; set; }
    public int OverridesExtracted { get; set; }
    public List<string> Steps { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// .mrpack 整合包安装。格式（实测 Fabulously Optimized v15.0.0）：
/// <code>
/// modrinth.index.json  formatVersion=1 / game=minecraft
///                      dependencies: { "fabric-loader": "0.19.5", "minecraft": "26.3" }
///                      files[48]: path / fileSize / hashes / env{client,server} / downloads[]
/// overrides/           直接覆盖到实例目录
/// </code>
/// </summary>
public sealed class ModpackInstaller(MinecraftPaths paths, InstallOptions options)
{
    public Action<string>? Log { get; init; }
    public Action<string, DownloadProgress>? Progress { get; init; }

    /// <summary>从 Modrinth 在线安装（先下包再装）。</summary>
    public Task<ModpackInstallReport> InstallAsync(
        ModrinthProject pack,
        ModrinthVersion version,
        CancellationToken ct = default)
        => InstallCoreAsync(pack, version, null, ct);

    /// <summary>从本地 .mrpack 文件安装（不下载）。</summary>
    public Task<ModpackInstallReport> InstallFromArchiveAsync(
        string mrpackPath,
        string? instanceNameHint = null,
        CancellationToken ct = default)
        => InstallCoreAsync(null, null, mrpackPath, ct, instanceNameHint);

    private async Task<ModpackInstallReport> InstallCoreAsync(
        ModrinthProject? pack,
        ModrinthVersion? version,
        string? localPath,
        CancellationToken ct,
        string? nameHint = null)
    {
        var report = new ModpackInstallReport { PackName = pack?.Title ?? nameHint ?? "整合包" };

        // 1) 下包（本地导入时跳过）
        string temp;
        if (localPath is not null)
        {
            temp = localPath;
            Log?.Invoke($"从本地导入整合包：{Path.GetFileName(localPath)}");
        }
        else
        {
            var file = version!.PrimaryFile ?? throw new InvalidDataException("整合包版本没有文件");
            temp = Path.Combine(Path.GetTempPath(), $"mcdl-pack-{Guid.NewGuid():N}.mrpack");
            Log?.Invoke($"下载整合包 {file.Filename}（{SizeFormat.Of(file.Size)}）");
            var downloader = new SegmentedDownloader { MaxThreads = options.MaxThreads, Log = Log };
            var dl = await downloader.DownloadAsync(new[]
            {
                new DownloadFile
                {
                    Name = file.Filename, TargetPath = temp, Urls = new[] { file.Url },
                    Size = file.Size, Sha1 = file.Sha1,
                },
            }, p => Progress?.Invoke("整合包本体", p), ct).ConfigureAwait(false);

            if (dl.Failed > 0 || !File.Exists(temp))
            {
                report.Errors.Add("整合包下载失败：" + string.Join("; ", dl.Errors.Take(2)));
                return report;
            }
        }

        try
        {
            using var zip = ZipFile.OpenRead(temp);

            // 2) 读 index
            var indexEntry = zip.GetEntry("modrinth.index.json")
                             ?? throw new InvalidDataException("整合包里没有 modrinth.index.json");
            MrpackIndex index;
            using (var reader = new StreamReader(indexEntry.Open()))
                index = JsonSerializer.Deserialize<MrpackIndex>(await reader.ReadToEndAsync(ct).ConfigureAwait(false),
                            HttpUtil.JsonOptions) ?? throw new InvalidDataException("index 解析失败");

            var mcVersion = index.Dependencies.TryGetValue("minecraft", out var mc) ? mc : null;
            if (string.IsNullOrEmpty(mcVersion))
                throw new InvalidDataException("整合包没有声明 minecraft 版本");

            Log?.Invoke($"整合包 {index.Name}  {mcVersion}  " +
                        string.Join("  ", index.Dependencies.Where(kv => kv.Key != "minecraft")
                            .Select(kv => $"{kv.Key}={kv.Value}")));

            // 3) 基底版本 + 加载器
            var installOptions = new InstallOptions
            {
                Source = options.Source,
                MaxThreads = options.MaxThreads,
                SkipAssets = options.SkipAssets,
            };

            var manifest = await HttpUtil.LoadManifestAsync(SourceSet.Of(options.Source).ManifestUrls, ct)
                .ConfigureAwait(false);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion);
            if (entry is null)
            {
                report.Errors.Add($"版本清单里没有 {mcVersion}");
                return report;
            }

            var vanilla = new VanillaInstaller(paths, installOptions)
            {
                Log = Log,
                Progress = (phase, p) => Progress?.Invoke(phase, p),
            };
            var baseReport = await vanilla.InstallAsync(entry, ct).ConfigureAwait(false);
            report.Steps.Add($"原版 {mcVersion}：{baseReport.Downloaded} 下载 / {baseReport.Skipped} 跳过");
            if (!baseReport.Success)
            {
                report.Errors.AddRange(baseReport.Errors.Take(3));
                return report;
            }

            var instanceId = mcVersion;

            if (index.Dependencies.TryGetValue("fabric-loader", out var fabricVersion))
            {
                var fabric = new FabricInstaller(paths, installOptions)
                {
                    Log = Log,
                    Progress = (phase, p) => Progress?.Invoke(phase, p),
                };
                var (fabricReport, profileId) = await fabric.InstallAsync(mcVersion, fabricVersion, ct)
                    .ConfigureAwait(false);
                instanceId = profileId;
                report.Steps.Add($"Fabric {fabricVersion} → {profileId}（依赖 {fabricReport.Downloaded}）");
                if (!fabricReport.Success) report.Errors.AddRange(fabricReport.Errors.Take(3));
            }
            else if (index.Dependencies.TryGetValue("neoforge", out var neoVersion)
                     || index.Dependencies.TryGetValue("forge", out neoVersion))
            {
                var isNeo = index.Dependencies.ContainsKey("neoforge");
                var java = await new JavaRuntimeManager(paths) { Log = Log }
                    .EnsureAsync(VersionJson.GuessComponent((int)baseReport.JavaMajor), ct).ConfigureAwait(false);
                if (java is null)
                {
                    report.Errors.Add("没有可用的 Java 运行时，无法执行安装器");
                    return report;
                }

                var forge = new ForgeInstaller(paths, installOptions) { Log = Log };
                var (ok, message, created) = await forge
                    .InstallAsync(mcVersion, neoVersion, java, isNeo, ct).ConfigureAwait(false);
                report.Steps.Add((isNeo ? "NeoForge " : "Forge ") + message);
                if (!ok || created is null)
                {
                    report.Errors.Add(message);
                    return report;
                }

                instanceId = created;
            }

            report.InstanceVersionId = instanceId;
            var instanceDir = paths.VersionDir(instanceId);
            Directory.CreateDirectory(instanceDir);

            // 4) 下载整合包里的文件
            var files = new List<DownloadFile>();
            foreach (var f in index.Files)
            {
                if (f.Env is not null && string.Equals(f.Env.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = SafeJoin(instanceDir, f.Path);
                if (target is null)
                {
                    report.Warnings.Add($"可疑路径，已跳过：{f.Path}");
                    continue;
                }

                files.Add(new DownloadFile
                {
                    Name = Path.GetFileName(f.Path),
                    TargetPath = target,
                    Urls = f.Downloads,
                    Size = f.FileSize,
                    Sha1 = f.Hashes.TryGetValue("sha1", out var sha1) ? sha1 : null,
                });
            }

            Log?.Invoke($"整合包文件 {files.Count} 个");
            if (files.Count > 0)
            {
                var contentDownloader = new SegmentedDownloader { MaxThreads = options.MaxThreads, Log = Log };
                var packDownloaded = await contentDownloader
                    .DownloadAsync(files, p => Progress?.Invoke("整合包内容", p), ct).ConfigureAwait(false);
                report.FilesDownloaded = packDownloaded.Downloaded + packDownloaded.Skipped;
                if (packDownloaded.Failed > 0) report.Errors.AddRange(packDownloaded.Errors.Take(5));
            }

            // 5) overrides
            report.OverridesExtracted = ExtractOverrides(zip, instanceDir, report.Warnings);

            // 6) 留个包名，方便 UI 展示
            File.WriteAllText(Path.Combine(instanceDir, ".mcdl-modpack.json"),
                JsonSerializer.Serialize(new
                {
                    name = index.Name ?? pack?.Title ?? nameHint ?? "整合包",
                    packVersion = index.VersionId ?? version?.VersionNumber ?? "",
                    source = pack is null ? "local" : "modrinth",
                    projectId = pack?.Id,
                    installedAt = DateTimeOffset.Now,
                }, new JsonSerializerOptions { WriteIndented = true }));

            report.Steps.Add($"整合包文件 {report.FilesDownloaded} 个，覆盖文件 {report.OverridesExtracted} 个");
            return report;
        }
        finally
        {
            if (localPath is null) { try { File.Delete(temp); } catch { /* 忽略 */ } }
        }
    }

    private static int ExtractOverrides(ZipArchive zip, string instanceDir, List<string> warnings)
    {
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            var prefix = entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) ? "overrides/"
                : entry.FullName.StartsWith("client-overrides/", StringComparison.Ordinal) ? "client-overrides/"
                : null;
            if (prefix is null) continue;

            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0) continue;

            var target = SafeJoin(instanceDir, relative);
            if (target is null) { warnings.Add($"可疑路径，已跳过：{entry.FullName}"); continue; }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>zip 里的路径可能带 ../，必须校验最终仍在实例目录内。</summary>
    private static string? SafeJoin(string root, string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);
        return combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase)
            ? combined
            : null;
    }

    private sealed class MrpackIndex
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("versionId")] public string? VersionId { get; set; }
        [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
        [JsonPropertyName("files")] public List<MrpackFile> Files { get; set; } = new();
    }

    private sealed class MrpackFile
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("fileSize")] public long FileSize { get; set; }
        [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = new();
        [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = new();
        [JsonPropertyName("env")] public MrpackEnv? Env { get; set; }
    }

    private sealed class MrpackEnv
    {
        [JsonPropertyName("client")] public string? Client { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
    }
}
