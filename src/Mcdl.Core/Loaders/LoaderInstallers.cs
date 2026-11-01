using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcdl.Core.Net;
using Mcdl.Core.Versions;

namespace Mcdl.Core.Loaders;

/// <summary>
/// Fabric 不需要跑安装器：官方 meta 直接给出「成品」版本 JSON（继承原版），
/// 落盘 + 下依赖即可，秒级完成，也不需要 Java。这和 Prism 的 lite 安装是同一个思路。
/// </summary>
public sealed class FabricInstaller(MinecraftPaths paths, InstallOptions options)
{
    public Action<string>? Log { get; init; }
    public Action<string, DownloadProgress>? Progress { get; init; }

    private readonly SourceSet _sources = SourceSet.Of(options.Source);

    public async Task<(InstallReport Report, string ProfileId)> InstallAsync(
        string mcId, string loaderVersion, CancellationToken ct = default)
    {
        var urls = _sources.FabricMetaBases
            .Select(b => $"{b}/versions/loader/{Uri.EscapeDataString(mcId)}/" +
                         $"{Uri.EscapeDataString(loaderVersion)}/profile/json")
            .ToArray();

        Log?.Invoke($"获取 Fabric profile：loader {loaderVersion} / MC {mcId}（数据源 {_sources.DisplayName}）");
        var text = await HttpUtil.GetStringAsync(urls, ct).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize<VersionJson>(text, HttpUtil.JsonOptions)
                      ?? throw new InvalidDataException("Fabric profile 解析失败");

        var id = string.IsNullOrWhiteSpace(profile.Id)
            ? $"fabric-loader-{loaderVersion}-{mcId}"
            : profile.Id;

        paths.EnsureBase();
        paths.EnsureLauncherProfiles();
        Directory.CreateDirectory(paths.VersionDir(id));
        await File.WriteAllTextAsync(paths.VersionJsonPath(id), text, ct).ConfigureAwait(false);

        var files = new List<DownloadFile>();
        foreach (var lib in profile.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;
            var relative = lib.MavenPath();
            var root = lib.Url?.TrimEnd('/') ?? "https://maven.fabricmc.net";
            var source = _sources.MavenOn(root, relative);
            files.Add(new DownloadFile
            {
                Name = Path.GetFileName(relative),
                TargetPath = Path.Combine(paths.Libraries, relative.Replace('/', Path.DirectorySeparatorChar)),
                Urls = source.Urls,
                Size = lib.Size,
                Sha1 = lib.Sha1,
                AllowSplit = source.AllowSplit,
            });
        }

        Log?.Invoke($"Fabric 依赖 {files.Count} 个");
        var downloader = new SegmentedDownloader { MaxThreads = options.MaxThreads, Log = Log };
        var result = await downloader.DownloadAsync(files, p => Progress?.Invoke("Fabric 依赖", p), ct)
            .ConfigureAwait(false);

        var report = new InstallReport
        {
            VersionId = id,
            Downloaded = result.Downloaded,
            Skipped = result.Skipped,
            Failed = result.Failed,
        };
        report.Errors.AddRange(result.Errors);
        return (report, id);
    }
}

/// <summary>
/// Forge / NeoForge 必须跑官方安装器：它要用 Java 执行 processors 做二进制 patch，纯 JSON 拼不出来。
///
/// 但官方安装器只用 JDK 自带的 HttpURLConnection 下载，在部分网络环境里会解析到被污染的 DNS 或直接超时。
/// 所以这里先做一步「喂饭」：从 installer jar 里的 <c>install_profile.json</c> 读出它需要的全部依赖，
/// 用我们自己的引擎下好后放进 <c>libraries/</c>，安装器就不会再去下载，只做本地 patch。
/// </summary>
public sealed class ForgeInstaller(MinecraftPaths paths, InstallOptions options)
{
    public Action<string>? Log { get; init; }
    public Action<string, DownloadProgress>? Progress { get; init; }

    public const string NeoForgeBase = "https://maven.neoforged.net/releases/";
    public const string ForgeBase = "https://maven.minecraftforge.net/";

    public async Task<(bool Ok, string Message, string? VersionId)> InstallAsync(
        string mcId, string loaderVersion, string javaExe, bool neoForge, CancellationToken ct = default)
    {
        var fileName = neoForge
            ? $"neoforge-{loaderVersion}-installer.jar"
            : $"forge-{mcId}-{loaderVersion}-installer.jar";
        var url = neoForge
            ? $"{NeoForgeBase}net/neoforged/neoforge/{loaderVersion}/{fileName}"
            : $"{ForgeBase}net/minecraftforge/forge/{mcId}-{loaderVersion}/{fileName}";

        var localJar = Path.Combine(Path.GetTempPath(), fileName);
        Log?.Invoke($"下载安装器：{fileName}");
        var downloader = new SegmentedDownloader { MaxThreads = Math.Min(8, options.MaxThreads), Log = Log };
        var result = await downloader
            .DownloadAsync(new[]
            {
                new DownloadFile { Name = fileName, TargetPath = localJar, Urls = new[] { url } },
            }, null, ct).ConfigureAwait(false);

        if (result.Failed > 0)
            return (false, $"安装器下载失败：{string.Join("; ", result.Errors.Take(2))}", null);

        paths.EnsureBase();
        paths.EnsureLauncherProfiles();

        // 1) 喂饭：installer 自己需要的依赖先下好
        await SeedInstallerLibrariesAsync(localJar, ct).ConfigureAwait(false);

        // 2) 跑安装器
        var (ok, output) = await RunInstallerAsync(javaExe, localJar, ct).ConfigureAwait(false);

        // 3) 万一还有漏的（安装器抱怨某些库下不下来），把它们补上再跑一次
        if (!ok)
        {
            var missing = ExtractMissingLibraries(output);
            if (missing.Count > 0)
            {
                Log?.Invoke($"安装器报告 {missing.Count} 个库缺失，补齐后重试");
                await DownloadMavenCoordsAsync(missing, ct).ConfigureAwait(false);
                (ok, output) = await RunInstallerAsync(javaExe, localJar, ct).ConfigureAwait(false);
            }
        }

        if (!ok)
        {
            try { File.Delete(localJar); } catch { /* 忽略 */ }
            return (false, $"安装器退出异常：{Tail(output, 600)}", null);
        }

        var id = neoForge
            ? Directory.GetDirectories(paths.Versions).Select(Path.GetFileName)
                .Where(n => n is not null && n.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n!.Length).FirstOrDefault()
            : Directory.GetDirectories(paths.Versions).Select(Path.GetFileName)
                .Where(n => n is not null && n.Contains($"{mcId}-forge", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n!.Length).FirstOrDefault();

        try { File.Delete(localJar); } catch { /* 忽略 */ }

        return id is null
            ? (false, "安装器已退出，但没有找到生成的版本目录", null)
            : (true, $"{id} 安装完成", id);
    }

    private async Task<(bool Ok, string Output)> RunInstallerAsync(string javaExe, string jar, CancellationToken ct)
    {
        Log?.Invoke($"执行安装器：{Path.GetFileName(javaExe)} -jar … --installClient");
        var psi = new ProcessStartInfo(javaExe)
        {
            WorkingDirectory = Path.GetDirectoryName(jar) ?? Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // 某些网络环境里 JVM 会卡在 IPv6 连接上
        psi.ArgumentList.Add("-Djava.net.preferIPv4Stack=true");
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(jar);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(paths.Root);

        using var process = Process.Start(psi);
        if (process is null) return (false, "无法启动 Java 进程");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = await stdoutTask.ConfigureAwait(false) + "\n" + await stderrTask.ConfigureAwait(false);

        return (process.ExitCode == 0, output);
    }

    /// <summary>把 installer jar 里的 install_profile.json 依赖全下好，安装器就只剩本地工作了。</summary>
    private async Task SeedInstallerLibrariesAsync(string jarPath, CancellationToken ct)
    {
        List<string> coords;
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("install_profile.json");
            if (entry is null) return;

            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var profile = JsonSerializer.Deserialize<InstallProfile>(text, HttpUtil.JsonOptions);
            coords = profile?.Libraries?.Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                     ?? new List<string>();
        }
        catch (Exception ex)
        {
            Log?.Invoke("读取 install_profile.json 失败：" + ex.Message);
            return;
        }

        if (coords.Count == 0) return;
        Log?.Invoke($"安装器依赖 {coords.Count} 个，先行下载");
        await DownloadMavenCoordsAsync(coords, ct).ConfigureAwait(false);
    }

    private async Task DownloadMavenCoordsAsync(IReadOnlyCollection<string> coords, CancellationToken ct)
    {
        var files = new List<DownloadFile>();
        foreach (var coord in coords)
        {
            var relative = MavenPathOf(coord);
            if (relative is null) continue;

            var target = Path.Combine(paths.Libraries, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) && new FileInfo(target).Length > 0) continue;

            files.Add(new DownloadFile
            {
                Name = Path.GetFileName(relative),
                TargetPath = target,
                Urls = MavenBasesOf(coord).Select(baseUrl => baseUrl + relative).ToArray(),
            });
        }

        if (files.Count == 0) return;

        var downloader = new SegmentedDownloader { MaxThreads = options.MaxThreads, Log = Log };
        var result = await downloader.DownloadAsync(files, p => Progress?.Invoke("安装器依赖", p), ct)
            .ConfigureAwait(false);
        Log?.Invoke($"安装器依赖下载完成：{result.Downloaded} 个，失败 {result.Failed}");
    }

    private static List<string> ExtractMissingLibraries(string output)
    {
        var result = new List<string>();
        var flag = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("failed to download", StringComparison.OrdinalIgnoreCase))
            {
                flag = true;
                continue;
            }

            if (!flag || line.Length == 0) continue;
            if (Regex.IsMatch(line, @"^[A-Za-z0-9_.\-]+:[A-Za-z0-9_.\-]+:[A-Za-z0-9_.+\-]+(:[A-Za-z0-9_.\-]+)?$"))
                result.Add(line);
            else
                flag = false;
        }

        return result.Distinct().ToList();
    }

    /// <summary>
    /// 安装器自己用的镜像列表就是 <c>maven.neoforged.net/releases</c>（它是整个 Maven 的代理），
    /// 所以 NeoForge 的组优先走这里，其余组先试 Maven Central，再退到代理和 Mojang 库站。
    /// </summary>
    private static IReadOnlyList<string> MavenBasesOf(string coord)
    {
        var group = coord.Split(':')[0];
        const string central = "https://repo1.maven.org/maven2/";
        const string mojang = "https://libraries.minecraft.net/";

        if (group.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
            return new[] { NeoForgeBase, central };

        if (group.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
            return new[] { ForgeBase, NeoForgeBase, central };

        return new[] { central, NeoForgeBase, mojang };
    }

    private static string? MavenPathOf(string coord)
    {
        var parts = coord.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    private static string Tail(string text, int max)
    {
        text = text.Replace("\r", "");
        return text.Length <= max ? text : text[^max..];
    }

    private sealed class InstallProfile
    {
        [JsonPropertyName("libraries")] public List<InstallProfileLibrary>? Libraries { get; set; }
    }

    private sealed class InstallProfileLibrary
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }
}
