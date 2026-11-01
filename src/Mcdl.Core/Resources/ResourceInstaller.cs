using System.Text.Json;
using Mcdl.Core.Net;
using Mcdl.Core.Versions;

namespace Mcdl.Core.Resources;

public sealed class ResourceInstallReport
{
    public int Added { get; set; }
    public int Replaced { get; set; }
    public int Skipped { get; set; }
    public List<string> Dependencies { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// 把 Modrinth 上的资源装进某个实例目录：解析 required 依赖、按项目去重替换、落盘后写一份
/// <c>.mcdl-resources.json</c> 记录，下次安装/卸载就不会出现"同项目两个版本共存"。
/// </summary>
public sealed class ResourceInstaller(ModrinthClient client)
{
    public Action<string>? Log { get; init; }
    public Action<DownloadProgress>? Progress { get; init; }
    public int MaxThreads { get; init; } = 16;

    private sealed record PlanItem(ModrinthProject Project, ModrinthVersion Version, ResourceType Type, bool IsDependency);

    public async Task<ResourceInstallReport> InstallAsync(
        InstanceContext instance,
        ModrinthProject project,
        ModrinthVersion version,
        bool includeRequiredDependencies = true,
        CancellationToken ct = default)
    {
        var report = new ResourceInstallReport();
        var state = LoadState(instance);

        var plan = new List<PlanItem> { new(project, version, project.Type, false) };
        var visited = new HashSet<string> { project.Id };

        if (includeRequiredDependencies)
            await CollectDependenciesAsync(instance, version, plan, visited, report, ct).ConfigureAwait(false);

        Log?.Invoke($"准备安装 {plan.Count} 个文件到 {Path.GetFileName(instance.InstanceDir)}");

        var downloads = new List<DownloadFile>();
        var pending = new List<(PlanItem Item, string Target, string Relative, bool Replacing)>();

        foreach (var item in plan)
        {
            var file = item.Version.PrimaryFile;
            if (file is null || string.IsNullOrEmpty(file.Url))
            {
                report.Errors.Add($"{item.Project.Title}：该版本没有可下载文件");
                continue;
            }

            var folder = instance.FolderFor(item.Type);
            var target = Path.Combine(folder, SanitizeFileName(file.Filename));
            var relative = Path.GetRelativePath(instance.GameDir, target);

            var existing = state.Installed.FirstOrDefault(r => r.ProjectId == item.Project.Id);
            var replacing = existing is not null && !string.Equals(existing.VersionId, item.Version.Id);

            if (existing is not null)
            {
                var oldPath = Path.Combine(instance.GameDir, existing.RelativePath);
                if (File.Exists(oldPath) && !string.Equals(oldPath, target, StringComparison.OrdinalIgnoreCase))
                    TryDelete(oldPath);
                state.Installed.Remove(existing);
            }

            if (File.Exists(target) && !replacing)
            {
                report.Skipped++;
                state.Installed.Add(ToRecord(item, relative));
                continue;
            }

            pending.Add((item, target, relative, replacing));
            downloads.Add(new DownloadFile
            {
                Name = file.Filename,
                TargetPath = target,
                Urls = new[] { file.Url },
                Size = file.Size,
                Sha1 = file.Sha1,
                AllowSplit = true,
            });
        }

        if (downloads.Count > 0)
        {
            var downloader = new SegmentedDownloader { MaxThreads = MaxThreads, Log = Log };
            var result = await downloader.DownloadAsync(downloads, Progress, ct).ConfigureAwait(false);

            if (result.Failed > 0)
                report.Errors.AddRange(result.Errors.Take(5));

            foreach (var (item, _, relative, replacing) in pending)
            {
                var record = ToRecord(item, relative);
                state.Installed.Add(record);
                if (replacing) report.Replaced++; else report.Added++;
                if (item.IsDependency) report.Dependencies.Add($"{item.Project.Title} {item.Version.VersionNumber}");
            }
        }

        SaveState(instance, state);
        return report;
    }

    // ---------- 依赖 ----------

    private async Task CollectDependenciesAsync(
        InstanceContext instance,
        ModrinthVersion version,
        List<PlanItem> plan,
        HashSet<string> visited,
        ResourceInstallReport report,
        CancellationToken ct)
    {
        foreach (var dep in version.Dependencies)
        {
            if (dep.IsIncompatible)
            {
                report.Warnings.Add($"该版本声明与 {dep.ProjectId} 不兼容，建议检查");
                continue;
            }

            if (!dep.IsRequired) continue;
            if (string.IsNullOrEmpty(dep.ProjectId) || !visited.Add(dep.ProjectId)) continue;

            try
            {
                var depProject = await client.GetProjectAsync(dep.ProjectId, ct).ConfigureAwait(false);

                ModrinthVersion? depVersion = null;
                if (!string.IsNullOrEmpty(dep.VersionId))
                    depVersion = await client.GetVersionAsync(dep.VersionId, ct).ConfigureAwait(false);

                depVersion ??= ModrinthClient.PickBest(
                    await client.GetVersionsAsync(depProject.Id, instance.MinecraftVersion, instance.Loader, ct)
                        .ConfigureAwait(false));

                if (depVersion is null)
                {
                    report.Warnings.Add($"依赖 {depProject.Title} 没有适配 {instance.MinecraftVersion}" +
                                        (instance.Loader is null ? "" : $" / {instance.Loader}") + " 的版本");
                    continue;
                }

                report.Dependencies.Add($"{depProject.Title} {depVersion.VersionNumber}");
                plan.Add(new PlanItem(depProject, depVersion, depProject.Type, true));

                await CollectDependenciesAsync(instance, depVersion, plan, visited, report, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"解析依赖 {dep.ProjectId} 失败：{ex.Message}");
            }
        }
    }

    // ---------- 卸载 ----------

    public bool Uninstall(InstanceContext instance, string projectId, Action<string>? log = null)
    {
        var state = LoadState(instance);
        var record = state.Installed.FirstOrDefault(r => r.ProjectId == projectId);
        if (record is null) return false;

        var path = Path.Combine(instance.InstanceDir, record.RelativePath);
        TryDelete(path);
        state.Installed.Remove(record);
        SaveState(instance, state);
        log?.Invoke($"已删除 {record.FileName}");
        return true;
    }

    // ---------- 状态读写 ----------

    public static ResourceState LoadState(InstanceContext instance)
    {
        try
        {
            if (!File.Exists(instance.StateFile)) return new ResourceState();
            return JsonSerializer.Deserialize<ResourceState>(File.ReadAllText(instance.StateFile), HttpUtil.JsonOptions)
                   ?? new ResourceState();
        }
        catch
        {
            return new ResourceState();
        }
    }

    public static void SaveState(InstanceContext instance, ResourceState state)
    {
        try
        {
            Directory.CreateDirectory(instance.InstanceDir);
            File.WriteAllText(instance.StateFile,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 状态文件写失败不影响已装文件
        }
    }

    private static InstalledResource ToRecord(PlanItem item, string relative) => new()
    {
        ProjectId = item.Project.Id,
        ProjectTitle = item.Project.Title,
        VersionId = item.Version.Id,
        VersionNumber = item.Version.VersionNumber,
        FileName = item.Version.PrimaryFile?.Filename ?? "",
        RelativePath = relative,
        Type = item.Type.ApiName(),
        IsDependency = item.IsDependency,
        InstalledAt = DateTimeOffset.Now,
    };

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
