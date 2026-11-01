using System.Diagnostics;
using Mcdl.Core;
using Mcdl.Core.Net;
using Mcdl.Core.Resources;
using Mcdl.Core.Versions;

// 冒烟测试：命令行跑一遍完整流程，不经过 UI。
// 用法：
//   Mcdl.Smoke --version 26.3 --dir E:\mc-test [--no-assets] [--source auto|official|bmclapi] [--threads 32]
//   Mcdl.Smoke --search sodium [--type mod|shader|resourcepack|modpack] [--mc 26.3] [--loader fabric]
//   Mcdl.Smoke --install-resource sodium --dir E:\mc --instance fabric-loader-0.20.1-26.3
//   Mcdl.Smoke --install-pack fabulously-optimized --dir E:\mc

string? versionId = null;
string? dir = null;
string? loader = null;
string? loaderVersion = null;
string? search = null;
string? typeArg = null;
string? mcArg = null;
string? instanceArg = null;
string? installResource = null;
string? installPack = null;
var noAssets = false;
var kind = SourceKind.Auto;
var threads = 32;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--version": versionId = args[++i]; break;
        case "--dir": dir = args[++i]; break;
        case "--loader": loader = args[++i]; break;
        case "--loader-version": loaderVersion = args[++i]; break;
        case "--no-assets": noAssets = true; break;
        case "--search": search = args[++i]; break;
        case "--type": typeArg = args[++i]; break;
        case "--mc": mcArg = args[++i]; break;
        case "--instance": instanceArg = args[++i]; break;
        case "--install-resource": installResource = args[++i]; break;
        case "--install-pack": installPack = args[++i]; break;
        case "--source":
            kind = args[++i].ToLowerInvariant() switch
            {
                "bmclapi" or "mirror" => SourceKind.BmclApi,
                "official" or "mojang" => SourceKind.Official,
                _ => SourceKind.Auto,
            };
            break;
        case "--threads": threads = int.Parse(args[++i]); break;
    }
}

var sources = SourceSet.Of(kind);
var client = new ModrinthClient { Log = Console.WriteLine };

// ---- 词表查询 ----
if (args.Length >= 2 && args[0] == "--wiki")
{
    var q = args[1];
    var hits = WikiDatabase.Search(q, 6);
    Console.WriteLine($"词表 {WikiDatabase.All.Count} 条；『{q}』命中 {hits.Count} 条：");
    foreach (var h in hits)
        Console.WriteLine($"  {h.Entry.ChineseName}  ->  {h.Entry.SearchTerm}   sim={h.Similarity:0.000} exact={h.Exact}");
    Console.WriteLine($"翻译结果：{ModrinthQuery.Translate(q)}");
    return 0;
}
// ---- 搜索 ----
if (search is not null)
{
    var type = ResourceTypes.Parse(typeArg ?? "mod");
    var query = ModrinthQuery.Translate(search);
    if (!string.Equals(query, search, StringComparison.Ordinal))
        Console.WriteLine($"中文关键词已翻译：{search} → {query}");
    var mcFilter = string.Equals(mcArg, "all", StringComparison.OrdinalIgnoreCase) ? null : mcArg;
    var loaderFilter = string.Equals(loader, "all", StringComparison.OrdinalIgnoreCase) ? null : loader;

    var result = await client.SearchAsync(query, type, mcFilter, loaderFilter, limit: 10);
    Console.WriteLine($"共 {result.TotalHits} 个结果，显示前 {result.Hits.Count} 个：");
    foreach (var hit in result.Hits)
        Console.WriteLine($"  [{hit.Type.DisplayName()}] {hit.Title}  ({hit.Slug})  " +
                          $"下载 {hit.Downloads:N0}  版本 {string.Join(",", hit.Versions.Take(3))}");
    return 0;
}

// ---- 装单个资源（含依赖）----
if (installResource is not null)
{
    if (dir is null || instanceArg is null)
    {
        Console.WriteLine("需要 --dir 和 --instance");
        return 1;
    }

    var paths0 = new MinecraftPaths(dir);
    var instance = InstanceContext.TryLoad(paths0, instanceArg, Console.WriteLine);
    if (instance is null) return 2;

    Console.WriteLine($"实例 {instance.VersionId}：MC {instance.MinecraftVersion}" +
                      $"  加载器 {instance.Loader ?? "原版"}  目录 {instance.InstanceDir}");

    var project = await client.GetProjectAsync(installResource);
    var versions = await client.GetVersionsForAsync(instance, project);
    var version = ModrinthClient.PickBest(versions);
    if (version is null)
    {
        Console.WriteLine($"没有适配 {instance.MinecraftVersion} 的版本");
        return 3;
    }

    Console.WriteLine($"选中 {project.Title} {version.VersionNumber}（{version.PrimaryFile?.Filename}）");
    var resourceInstaller = new ResourceInstaller(client)
    {
        Log = Console.WriteLine,
        Progress = (p) => Console.WriteLine($"  {SizeFormat.Of(p.DoneBytes)}/{SizeFormat.Of(p.TotalBytes)} 线程 {p.ActiveThreads}"),
    };

    var resReport = await resourceInstaller.InstallAsync(instance, project, version);
    Console.WriteLine($"结果：新增 {resReport.Added}  替换 {resReport.Replaced}  跳过 {resReport.Skipped}  错误 {resReport.Errors.Count}");
    foreach (var d in resReport.Dependencies) Console.WriteLine("  依赖：" + d);
    foreach (var w in resReport.Warnings) Console.WriteLine("  警告：" + w);
    foreach (var e in resReport.Errors) Console.WriteLine("  错误：" + e);
    return resReport.Success ? 0 : 4;
}

// ---- 装整合包 ----
if (installPack is not null)
{
    if (dir is null)
    {
        Console.WriteLine("需要 --dir");
        return 1;
    }

    var paths1 = new MinecraftPaths(dir);
    var project = await client.GetProjectAsync(installPack);
    var versions = await client.GetVersionsAsync(project.Id);
    var version = ModrinthClient.PickBest(versions) ?? versions.FirstOrDefault();
    if (version is null)
    {
        Console.WriteLine("整合包没有可用版本");
        return 3;
    }

    Console.WriteLine($"整合包 {project.Title} {version.VersionNumber}（{version.PrimaryFile?.Filename}）");
    var packInstaller = new ModpackInstaller(paths1,
        new InstallOptions { Source = kind, MaxThreads = threads, SkipAssets = noAssets })
    {
        Log = Console.WriteLine,
        Progress = (phase, p) => Console.WriteLine($"  [{phase}] {SizeFormat.Of(p.DoneBytes)}/{SizeFormat.Of(p.TotalBytes)}"),
    };

    var packReport = await packInstaller.InstallAsync(project, version);
    Console.WriteLine($"结果：实例 {packReport.InstanceVersionId}  文件 {packReport.FilesDownloaded}  覆盖 {packReport.OverridesExtracted}");
    foreach (var s in packReport.Steps) Console.WriteLine("  步骤：" + s);
    foreach (var w in packReport.Warnings.Take(5)) Console.WriteLine("  警告：" + w);
    foreach (var e in packReport.Errors) Console.WriteLine("  错误：" + e);
    return packReport.Success ? 0 : 4;
}

if (versionId is null || dir is null)
{
    Console.WriteLine("用法: Mcdl.Smoke --version <id> --dir <path> [--no-assets] [--source auto|official|bmclapi] [--threads N]");
    Console.WriteLine("      Mcdl.Smoke --search <关键词> [--type mod|shader|resourcepack|modpack] [--mc 26.3] [--loader fabric]");
    Console.WriteLine("      Mcdl.Smoke --install-resource <slug> --dir <mc> --instance <versionId>");
    Console.WriteLine("      Mcdl.Smoke --install-pack <slug> --dir <mc>");
    return 1;
}

Console.WriteLine($"版本 {versionId}  目标 {dir}  线程 {threads}  数据源 {sources.DisplayName}  跳过资源 {noAssets}");

var manifest = await HttpUtil.LoadManifestAsync(sources.ManifestUrls);
var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId);
if (entry is null)
{
    Console.WriteLine($"清单里没有版本 {versionId}");
    return 2;
}

var paths = new MinecraftPaths(dir);
var clock = Stopwatch.StartNew();
var lastPrint = 0L;
long lastDone = 0;
var speedSamples = new List<double>();

var installer = new VanillaInstaller(paths, new InstallOptions
{
    Source = kind,
    MaxThreads = threads,
    SkipAssets = noAssets,
})
{
    Log = message => Console.WriteLine("  " + message),
    Progress = (phase, p) =>
    {
        if (clock.ElapsedMilliseconds - lastPrint < 1000) return;
        lastPrint = clock.ElapsedMilliseconds;
        var delta = p.DoneBytes - lastDone;
        lastDone = p.DoneBytes;
        if (delta > 0) speedSamples.Add(delta);
        var pct = p.TotalBytes > 0 ? p.DoneBytes * 100.0 / p.TotalBytes : 0;
        Console.WriteLine($"  [{phase}] {SizeFormat.Of(p.DoneBytes)}/{SizeFormat.Of(p.TotalBytes)} " +
                          $"{pct:0.0}%  瞬时 {SizeFormat.Of(delta)}/s  线程 {p.ActiveThreads}");
    },
};

var report = await installer.InstallAsync(entry);
clock.Stop();

Console.WriteLine();
Console.WriteLine($"结果: 下载 {report.Downloaded}  跳过 {report.Skipped}  失败 {report.Failed}");
Console.WriteLine($"耗时: {clock.Elapsed.TotalSeconds:0.0}s   需要 Java {report.JavaMajor}");
if (speedSamples.Count > 0)
    Console.WriteLine($"峰值速度: {SizeFormat.Of((long)speedSamples.Max())}/s   平均: {SizeFormat.Of((long)speedSamples.Average())}/s");
foreach (var error in report.Errors.Take(10)) Console.WriteLine("  错误: " + error);

if (loader is null) return report.Success ? 0 : 3;

// ---- 加载器 ----
var allOk = report.Success;
if (loaderVersion is null)
{
    var versions = loader.ToLowerInvariant() switch
    {
        "fabric" => (await Mcdl.Core.Loaders.LoaderCatalog.FabricVersionsAsync(versionId))
            .Select(v => v.Version).ToList(),
        "forge" => (await Mcdl.Core.Loaders.LoaderCatalog.ForgeVersionsAsync(versionId)).ToList(),
        "neoforge" => (await Mcdl.Core.Loaders.LoaderCatalog.NeoForgeVersionsAsync(versionId)).ToList(),
        _ => new List<string>(),
    };
    Console.WriteLine($"{loader} 可用版本 {versions.Count} 个: {string.Join(", ", versions.Take(5))}");
    if (versions.Count == 0) return 4;
    loaderVersion = versions[0];
}

Console.WriteLine();
Console.WriteLine($"== 安装 {loader} {loaderVersion} ==");

if (loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
{
    var fabric = new Mcdl.Core.Loaders.FabricInstaller(paths, new InstallOptions { Source = kind, MaxThreads = threads })
    {
        Log = message => Console.WriteLine("  " + message),
        Progress = (phase, p) =>
            Console.WriteLine($"  [{phase}] {SizeFormat.Of(p.DoneBytes)}/{SizeFormat.Of(p.TotalBytes)} 线程 {p.ActiveThreads}"),
    };
    var (fabricReport, profileId) = await fabric.InstallAsync(versionId, loaderVersion);
    Console.WriteLine($"Fabric 结果: {profileId}  下载 {fabricReport.Downloaded}  失败 {fabricReport.Failed}");
    Console.WriteLine($"  版本 JSON 存在: {File.Exists(paths.VersionJsonPath(profileId))}");
    allOk &= fabricReport.Success;
}
else
{
    var javaManager = new Mcdl.Core.Java.JavaRuntimeManager(paths) { Log = message => Console.WriteLine("  " + message) };
    var component = Mcdl.Core.Versions.VersionJson.GuessComponent((int)report.JavaMajor);
    var java = await javaManager.EnsureAsync(component);
    Console.WriteLine($"Java: {java ?? "无"}");
    if (java is null) return 5;

    var forge = new Mcdl.Core.Loaders.ForgeInstaller(paths, new InstallOptions { Source = kind, MaxThreads = threads })
    {
        Log = message => Console.WriteLine("  " + message),
    };
    var (ok, message, created) = await forge.InstallAsync(versionId, loaderVersion, java,
        loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"结果: {ok}  {message}  {created}");
    allOk &= ok;
}

return allOk ? 0 : 3;

