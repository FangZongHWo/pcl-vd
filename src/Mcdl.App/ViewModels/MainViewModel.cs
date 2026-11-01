using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using Mcdl.Core;
using Mcdl.Core.Java;
using Mcdl.Core.Loaders;
using Mcdl.Core.Net;
using Mcdl.Core.Resources;
using Mcdl.Core.Versions;

namespace Mcdl.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly List<VersionRow> _allVersions = new();
    private readonly StringBuilder _log = new();

    private string _targetDir;
    private string _searchText = "";
    private bool _showSnapshots;
    private VersionRow? _selectedVersion;
    private bool _isBusy;
    private string _statusText = "正在读取版本清单…";
    private string _footerText = "";
    private bool _isLogVisible;
    private double _progressValue;
    private string _progressText = "尚未开始";
    private string _speedText = "";
    private string _currentFileName = "";

    public MainViewModel()
    {
        _targetDir = DefaultMinecraftDir();
        Loaders = new ObservableCollection<string> { "原版", "Fabric", "Forge", "NeoForge" };
        _selectedLoader = Loaders[0];
        JavaOptions = new ObservableCollection<string> { "自动（按版本选择）" };
        _selectedJava = JavaOptions[0];
        Sources = new ObservableCollection<string>(SourceLabels.Keys);
        _selectedSource = Sources[0];
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedVersion is not null && !IsBusy);
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);

        ShowVersionsCommand = new RelayCommand(() => SwitchTab(0));
        ShowResourcesCommand = new RelayCommand(() => SwitchTab(1));
        ShowAboutCommand = new RelayCommand(() => SwitchTab(2));
        SearchResourcesCommand = new AsyncRelayCommand(SearchResourcesAsync);
        InstallResourceCommand = new AsyncRelayCommand(InstallResourceAsync,
            () => SelectedResourceVersion is not null && !IsResourceBusy);
    }

    /// <summary>数据源下拉的显示名 → SourceKind。</summary>
    private static readonly Dictionary<string, SourceKind> SourceLabels = new()
    {
        ["自动（官方优先，失败回落镜像）"] = SourceKind.Auto,
        ["官方（Mojang）"] = SourceKind.Official,
        ["国内镜像（BMCLAPI）"] = SourceKind.BmclApi,
    };

    public ObservableCollection<string> Sources { get; }

    private string _selectedSource;
    public string SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value)) return;
            _sourceKind = SourceLabels.TryGetValue(value, out var kind) ? kind : SourceKind.Auto;
            OnPropertyChanged(nameof(SourceHint));
            _ = InitializeAsync();
        }
    }

    public string SourceHint => _sourceKind switch
    {
        SourceKind.BmclApi => "全部走 BMCLAPI 国内镜像，Maven 库自动降为单线程（镜像的 Range 不可靠）",
        SourceKind.Official => "只连 Mojang 官方源，速度取决于你的网络",
        _ => "官方为主，失败自动换 BMCLAPI 镜像",
    };

    public ObservableCollection<VersionRow> Versions { get; } = new();
    public ObservableCollection<string> Loaders { get; }
    public ObservableCollection<string> LoaderVersions { get; } = new();
    public ObservableCollection<string> JavaOptions { get; }

    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand ToggleLogCommand { get; }

    private string _selectedLoader;
    public string SelectedLoader
    {
        get => _selectedLoader;
        set { if (Set(ref _selectedLoader, value)) _ = RefreshLoaderVersionsAsync(); }
    }

    private string? _selectedLoaderVersion;
    public string? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set => Set(ref _selectedLoaderVersion, value);
    }

    private string _selectedJava;
    public string SelectedJava
    {
        get => _selectedJava;
        set => Set(ref _selectedJava, value);
    }

    public bool IsLoaderVersionEnabled => SelectedLoader != "原版" && LoaderVersions.Count > 0;

    public string TargetDir
    {
        get => _targetDir;
        // TextBox 初始化时会先推一个空串过来，不能让它把默认目录冲掉
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Set(ref _targetDir, value);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplyFilter(); }
    }

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set { if (Set(ref _showSnapshots, value)) ApplyFilter(); }
    }

    public VersionRow? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!Set(ref _selectedVersion, value)) return;
            OnPropertyChanged(nameof(DetailTitle));
            OnPropertyChanged(nameof(DetailSubtitle));
            InstallCommand.RaiseCanExecuteChanged();
        }
    }

    public string DetailTitle => SelectedVersion?.Id ?? "选择一个版本";

    public string DetailSubtitle
    {
        get
        {
            var e = SelectedVersion?.Entry;
            if (e is null) return "左侧列表只显示 1.13 及以上版本（含 2026 新命名 26.x）";
            var kind = e.IsRelease ? "正式版" : "快照";
            return $"{kind}  ·  发布于 {e.ReleaseTime.LocalDateTime:yyyy-MM-dd HH:mm}  ·  ID {e.Id}";
        }
    }

    public string VersionCountText => $"{Versions.Count} 个版本";

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(InstallButtonText));
            }
        }
    }

    public long DownloadBytes { get; private set; }

    private SourceKind _sourceKind = SourceKind.Auto;
    private readonly int _maxThreads = 32;

    public string InstallButtonText => IsBusy ? "正在下载…" : "下载到该目录";

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string FooterText
    {
        get => _footerText;
        set => Set(ref _footerText, value);
    }

    public bool IsLogVisible
    {
        get => _isLogVisible;
        set => Set(ref _isLogVisible, value);
    }

    public string LogText => _log.ToString();

    public double ProgressValue
    {
        get => _progressValue;
        set => Set(ref _progressValue, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    public string SpeedText
    {
        get => _speedText;
        set => Set(ref _speedText, value);
    }

    public string CurrentFileName
    {
        get => _currentFileName;
        set => Set(ref _currentFileName, value);
    }

    public async Task InitializeAsync()
    {
        if (IsBusy) return;

        var sources = SourceSet.Of(_sourceKind);
        var keepId = SelectedVersion?.Id;

        try
        {
            StatusText = $"正在读取版本清单（{sources.DisplayName}）…";
            var manifest = await HttpUtil.LoadManifestAsync(sources.ManifestUrls).ConfigureAwait(true);
            _allVersions.Clear();

            foreach (var entry in manifest.Versions)
            {
                if (entry.IsOldAlphaBeta) continue;
                var supported = McVersion.IsSupportedByApp(entry.Id)
                                || (!entry.IsRelease && entry.ReleaseTime.Year >= 2026);
                if (!supported) continue;
                _allVersions.Add(new VersionRow(entry, entry.Id == manifest.Latest.Release));
            }

            _allVersions.Sort(static (a, b) =>
            {
                var pa = McVersion.TryParse(a.Id, out var va);
                var pb = McVersion.TryParse(b.Id, out var vb);
                if (pa && pb) return vb.CompareTo(va);
                if (pa) return -1;
                if (pb) return 1;
                return b.Entry.ReleaseTime.CompareTo(a.Entry.ReleaseTime);
            });

            ApplyFilter();
            FooterText = $"数据源 {sources.DisplayName}  ·  最新正式版 {manifest.Latest.Release}  ·  最新快照 {manifest.Latest.Snapshot}";
            StatusText = "就绪。选择版本后即可下载。";

            SelectedVersion = (keepId is null ? null : Versions.FirstOrDefault(v => v.Id == keepId))
                              ?? Versions.FirstOrDefault();
            AppendLog($"版本清单已从 {sources.ManifestUrls[0]} 读取，共 {Versions.Count} 个可用版本");
        }
        catch (Exception ex)
        {
            StatusText = "读取版本清单失败：" + ex.Message;
            FooterText = "";
            AppendLog("读取版本清单失败：" + ex);
        }
    }

    private void ApplyFilter()
    {
        var keyword = _searchText.Trim();
        Versions.Clear();

        foreach (var row in _allVersions)
        {
            if (!ShowSnapshots && !row.Entry.IsRelease) continue;
            if (keyword.Length > 0 && !row.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            Versions.Add(row);
        }

        OnPropertyChanged(nameof(VersionCountText));
        if (SelectedVersion is not null && !Versions.Contains(SelectedVersion))
            SelectedVersion = Versions.FirstOrDefault();
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        OnPropertyChanged(nameof(IsLoaderVersionEnabled));

        if (SelectedLoader == "原版" || SelectedVersion is null)
        {
            OnPropertyChanged(nameof(IsLoaderVersionEnabled));
            return;
        }

        try
        {
            var mc = SelectedVersion.Id;
            if (SelectedLoader == "Fabric")
            {
                foreach (var item in (await LoaderCatalog.FabricVersionsAsync(mc, SourceSet.Of(_sourceKind)).ConfigureAwait(true)).Take(40))
                    LoaderVersions.Add(item.Version + (item.Stable ? "  (稳定)" : ""));
            }
            else if (SelectedLoader == "Forge")
            {
                foreach (var v in await LoaderCatalog.ForgeVersionsAsync(mc, SourceSet.Of(_sourceKind)).ConfigureAwait(true))
                    LoaderVersions.Add(v);
            }
            else if (SelectedLoader == "NeoForge")
            {
                foreach (var v in await LoaderCatalog.NeoForgeVersionsAsync(mc, SourceSet.Of(_sourceKind)).ConfigureAwait(true))
                    LoaderVersions.Add(v);
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            StatusText = LoaderVersions.Count > 0
                ? $"{SelectedLoader} 有 {LoaderVersions.Count} 个可用版本。"
                : $"{SelectedLoader} 暂未发布 {mc} 的版本。";
        }
        catch (Exception ex)
        {
            StatusText = $"读取 {SelectedLoader} 版本失败：{ex.Message}";
            AppendLog(ex.ToString());
        }
        finally
        {
            OnPropertyChanged(nameof(IsLoaderVersionEnabled));
        }
    }

    // ================= 导航 =================

    private int _tab; // 0=版本 1=资源 2=关于

    public bool IsVersionTab => _tab == 0;
    public bool IsResourceTab => _tab == 1;
    public bool IsAboutTab => _tab == 2;

    public RelayCommand ShowVersionsCommand { get; private set; } = null!;
    public RelayCommand ShowResourcesCommand { get; private set; } = null!;
    public RelayCommand ShowAboutCommand { get; private set; } = null!;

    private void SwitchTab(int tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        OnPropertyChanged(nameof(IsVersionTab));
        OnPropertyChanged(nameof(IsResourceTab));
        OnPropertyChanged(nameof(IsAboutTab));

        if (tab == 1)
        {
            if (ResourceResults.Count == 0) _ = SearchResourcesAsync();
            else RefreshResourceTarget();
        }
    }

    // ================= 资源页 =================

    private readonly ModrinthClient _modrinth = new();
    private ModrinthProject? _resourceProject;
    private bool _isResourceBusy;

    public ObservableCollection<ResourceRow> ResourceResults { get; } = new();
    public ObservableCollection<string> ResourceTypeOptions { get; } = new() { "模组", "光影", "资源包", "整合包" };
    public ObservableCollection<ResourceVersionRow> ResourceVersions { get; } = new();

    public AsyncRelayCommand SearchResourcesCommand { get; private set; } = null!;
    public AsyncRelayCommand InstallResourceCommand { get; private set; } = null!;

    private string _resourceQuery = "";
    public string ResourceQuery
    {
        get => _resourceQuery;
        set => Set(ref _resourceQuery, value);
    }

    private string _selectedResourceType = "模组";
    public string SelectedResourceType
    {
        get => _selectedResourceType;
        set
        {
            if (!Set(ref _selectedResourceType, value)) return;
            _ = SearchResourcesAsync();
        }
    }

    private ResourceRow? _selectedResource;
    public ResourceRow? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (!Set(ref _selectedResource, value)) return;
            _ = LoadResourceDetailAsync(value);
        }
    }

    private ResourceVersionRow? _selectedResourceVersion;
    public ResourceVersionRow? SelectedResourceVersion
    {
        get => _selectedResourceVersion;
        set
        {
            if (!Set(ref _selectedResourceVersion, value)) return;
            UpdateResourceSummary();
            InstallResourceCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasResourceVersions => ResourceVersions.Count > 0;

    private string _resourceTitle = "搜索并安装资源";
    public string ResourceTitle
    {
        get => _resourceTitle;
        set => Set(ref _resourceTitle, value);
    }

    private string _resourceSubtitle = "数据来自 Modrinth，无需登录";
    public string ResourceSubtitle
    {
        get => _resourceSubtitle;
        set => Set(ref _resourceSubtitle, value);
    }

    private Avalonia.Media.Imaging.Bitmap? _resourceIcon;
    public Avalonia.Media.Imaging.Bitmap? ResourceIcon => _resourceIcon;
    public bool HasResourceIcon => _resourceIcon is not null;

    private void SetResourceIcon(Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        _resourceIcon = bitmap;
        OnPropertyChanged(nameof(ResourceIcon));
        OnPropertyChanged(nameof(HasResourceIcon));
    }

    private string _resourceTargetText = "先在「版本」页选一个已安装的版本";
    public string ResourceTargetText
    {
        get => _resourceTargetText;
        set => Set(ref _resourceTargetText, value);
    }

    private string _resourceHint = "";
    public string ResourceHint
    {
        get => _resourceHint;
        set => Set(ref _resourceHint, value);
    }

    private string _resourceStatus = "";
    public string ResourceStatus
    {
        get => _resourceStatus;
        set => Set(ref _resourceStatus, value);
    }

    private string _resourceCountText = "";
    public string ResourceCountText
    {
        get => _resourceCountText;
        set => Set(ref _resourceCountText, value);
    }

    private string _resourceProgressText = "";
    public string ResourceProgressText
    {
        get => _resourceProgressText;
        set => Set(ref _resourceProgressText, value);
    }

    private string _resourceSpeedText = "";
    public string ResourceSpeedText
    {
        get => _resourceSpeedText;
        set => Set(ref _resourceSpeedText, value);
    }

    private double _resourceProgressValue;
    public double ResourceProgressValue
    {
        get => _resourceProgressValue;
        set => Set(ref _resourceProgressValue, value);
    }

    public bool IsResourceBusy
    {
        get => _isResourceBusy;
        set
        {
            if (Set(ref _isResourceBusy, value))
            {
                OnPropertyChanged(nameof(InstallResourceButtonText));
                InstallResourceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string InstallResourceButtonText => IsResourceBusy ? "正在安装…" : "安装到当前版本";

    // ---------- 搜索筛选：游戏版本 / 加载器 ----------

    public ObservableCollection<string> ResourceVersionOptions { get; } = new();
    public ObservableCollection<string> ResourceLoaderOptions { get; } = new()
    {
        "跟随实例", "全部加载器", "Fabric", "Forge", "NeoForge", "Quilt",
    };

    private string _selectedResourceVersionOption = "跟随实例";
    public string SelectedResourceVersionOption
    {
        get => _selectedResourceVersionOption;
        set
        {
            if (!Set(ref _selectedResourceVersionOption, value)) return;
            _ = SearchResourcesAsync();
        }
    }

    private string _selectedResourceLoaderOption = "跟随实例";
    public string SelectedResourceLoaderOption
    {
        get => _selectedResourceLoaderOption;
        set
        {
            if (!Set(ref _selectedResourceLoaderOption, value)) return;
            _ = SearchResourcesAsync();
        }
    }

    /// <summary>把界面上的选项翻译成 API 用的 (mcVersion, loader)，null 表示不加这个 facet。</summary>
    private (string? Mc, string? Loader) ResolveSearchFilter(InstanceContext? instance)
    {
        string? mc;
        if (_selectedResourceVersionOption.StartsWith("跟随", StringComparison.Ordinal))
            mc = instance?.MinecraftVersion;
        else if (_selectedResourceVersionOption.StartsWith("全部", StringComparison.Ordinal))
            mc = null;
        else
            mc = _selectedResourceVersionOption;

        string? loader = _selectedResourceLoaderOption switch
        {
            "跟随实例" => instance?.Loader,
            "全部加载器" => null,
            "Fabric" => "fabric",
            "Forge" => "forge",
            "NeoForge" => "neoforge",
            "Quilt" => "quilt",
            _ => null,
        };

        return (mc, loader);
    }

    /// <summary>搜索条件和安装目标不一致时提醒用户（装进去不会生效）。</summary>
    private bool SearchMatchesInstance(InstanceContext? instance)
    {
        if (instance is null) return false;
        var (mc, loader) = ResolveSearchFilter(instance);
        if (mc is not null && !string.Equals(mc, instance.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
            return false;
        if (loader is not null && !string.Equals(loader, instance.Loader, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private void RebuildResourceVersionOptions(InstanceContext? instance)
    {
        var followLabel = instance is null
            ? "跟随实例"
            : $"跟随实例（{instance.MinecraftVersion}）";

        var keep = _selectedResourceVersionOption;
        var wasFollowing = keep.StartsWith("跟随", StringComparison.Ordinal);

        ResourceVersionOptions.Clear();
        ResourceVersionOptions.Add(followLabel);
        ResourceVersionOptions.Add("全部版本");
        foreach (var row in _allVersions.Where(v => v.Entry.IsRelease).Take(60))
            ResourceVersionOptions.Add(row.Id);

        var match = wasFollowing
            ? followLabel
            : ResourceVersionOptions.FirstOrDefault(o =>
                string.Equals(o, keep, StringComparison.OrdinalIgnoreCase));

        _selectedResourceVersionOption = match ?? followLabel;
        OnPropertyChanged(nameof(SelectedResourceVersionOption));
    }

    private InstanceContext? ResolveInstance()
    {
        if (SelectedVersion is null) return null;
        return InstanceContext.TryLoad(new MinecraftPaths(TargetDir), SelectedVersion.Id, AppendLog);
    }

    private ResourceType CurrentResourceType => _selectedResourceType switch
    {
        "光影" => ResourceType.Shader,
        "资源包" => ResourceType.ResourcePack,
        "整合包" => ResourceType.Modpack,
        _ => ResourceType.Mod,
    };

    private void RefreshResourceTarget()
    {
        var instance = ResolveInstance();
        RebuildResourceVersionOptions(instance);

        if (instance is null)
        {
            ResourceTargetText = "先在「版本」页选一个已安装的版本，资源会装进它的隔离目录";
            return;
        }

        ResourceTargetText = $"{instance.VersionId}  ·  MC {instance.MinecraftVersion}  ·  " +
                             $"{instance.Loader ?? "原版"}  →  {Path.GetFileName(instance.InstanceDir)}";

        var (mc, loader) = ResolveSearchFilter(instance);
        var type = CurrentResourceType;

        if (!SearchMatchesInstance(instance))
        {
            ResourceHint = $"搜索条件与当前实例不一致：筛的是 {mc ?? "全部版本"} / {loader ?? "全部加载器"}，" +
                           $"实例是 {instance.MinecraftVersion} / {instance.Loader ?? "原版"}。" +
                           "这样搜出来的模组装进去不会生效，只适合先看看。";
            return;
        }

        ResourceHint = type switch
        {
            ResourceType.Shader => "光影需要 Iris 或 OptiFine 才生效（原版无法使用）",
            ResourceType.ResourcePack => "资源包装好后在游戏「选项 → 资源包」里启用",
            ResourceType.Modpack => "整合包会新建一个独立实例，不会动你现有的版本",
            _ => instance.Loader is null
                ? "当前版本是原版，装模组前请先装 Fabric / Forge / NeoForge"
                : $"模组会按 {instance.Loader} + {instance.MinecraftVersion} 过滤",
        };
    }

    private async Task SearchResourcesAsync()
    {
        var instance = ResolveInstance();
        var type = CurrentResourceType;
        var (mcFilter, loaderFilter) = ResolveSearchFilter(instance);

        var mc = type is ResourceType.Modpack ? null : mcFilter;
        var loader = type is ResourceType.Mod or ResourceType.Modpack ? loaderFilter : null;

        // Modrinth 只有英文标题，中文关键词先过一遍常见别名
        var query = ModrinthQuery.Translate(_resourceQuery);

        ResourceStatus = "正在搜索…";
        try
        {
            var result = await _modrinth
                .SearchAsync(query, type, mc, loader, limit: 20)
                .ConfigureAwait(true);

            ResourceResults.Clear();
            foreach (var hit in result.Hits) ResourceResults.Add(new ResourceRow(hit));
            foreach (var row in ResourceResults) _ = IconCache.FillAsync(row);

            ResourceCountText = result.TotalHits == 0
                ? "没有匹配结果"
                : $"共 {result.TotalHits:N0} 个，显示 {ResourceResults.Count} 个" +
                  (string.Equals(query, _resourceQuery, StringComparison.Ordinal) ? "" : $"（按 “{query}” 搜的）");

            ResourceStatus = ResourceResults.Count switch
            {
                0 when ModrinthQuery.HasCjk(_resourceQuery) =>
                    $"Modrinth 只有英文名，试试用英文：{(ModrinthQuery.SuggestEnglish(_resourceQuery) is { } en ? $"\u201c{en}\u201d" : "\u201cCreate\u201d / \u201cSodium\u201d 这样的英文名")}",
                0 => "没有结果，可以放宽「游戏版本 / 加载器」筛选再试",
                _ => "点左侧条目查看详情",
            };

            SelectedResource = ResourceResults.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ResourceStatus = "搜索失败：" + ex.Message;
            AppendLog(ex.ToString());
        }
        finally
        {
            RefreshResourceTarget();
        }
    }

    private async Task LoadResourceDetailAsync(ResourceRow? row)
    {
        ResourceVersions.Clear();
        OnPropertyChanged(nameof(HasResourceVersions));
        if (row is null)
        {
            SetResourceIcon(null);
            return;
        }

        ResourceTitle = row.Title;
        ResourceSubtitle = row.Description;
        ResourceStatus = "正在读取版本列表…";
        SetResourceIcon(null);

        var instance = ResolveInstance();
        try
        {
            _resourceProject = await _modrinth.GetProjectAsync(row.Slug).ConfigureAwait(true);
            SetResourceIcon(await IconCache.GetAsync(_resourceProject.IconUrl).ConfigureAwait(true));

            var (mcFilter, loaderFilter) = ResolveSearchFilter(instance);
            var useLoader = _resourceProject.Type is ResourceType.Mod or ResourceType.Modpack;

            var versions = await _modrinth
                .GetVersionsAsync(_resourceProject.Id, mcFilter, useLoader ? loaderFilter : null)
                .ConfigureAwait(true);

            foreach (var v in versions.OrderByDescending(v => v.IsRelease).ThenByDescending(v => v.DatePublished))
                ResourceVersions.Add(new ResourceVersionRow(v));

            OnPropertyChanged(nameof(HasResourceVersions));

            SelectedResourceVersion = ResourceVersions.FirstOrDefault();
            UpdateResourceSummary();

            ResourceStatus = ResourceVersions.Count == 0
                ? $"该资源没有适配 {instance?.MinecraftVersion ?? "当前版本"} 的版本"
                : $"共 {ResourceVersions.Count} 个版本可选";
        }
        catch (Exception ex)
        {
            ResourceStatus = "读取失败：" + ex.Message;
            AppendLog(ex.ToString());
        }
    }

    private void UpdateResourceSummary()
    {
        var v = SelectedResourceVersion?.Version;
        if (_resourceProject is null || v is null) return;

        var size = v.PrimaryFile is null ? "" : $"  ·  {SizeFormat.Of(v.PrimaryFile.Size)}";
        ResourceSubtitle = $"{_resourceProject.Description}    下载 {_resourceProject.Downloads:N0}{size}";
        RefreshResourceTarget();
    }

    private async Task InstallResourceAsync()
    {
        if (_resourceProject is null || SelectedResourceVersion is null)
        {
            ResourceStatus = "先选一个资源和一个版本";
            return;
        }

        var version = SelectedResourceVersion.Version;

        if (_resourceProject.Type == ResourceType.Modpack)
        {
            await InstallModpackAsync(version).ConfigureAwait(true);
            return;
        }

        var instance = ResolveInstance();
        if (instance is null)
        {
            ResourceStatus = "先在「版本」页选一个已安装的版本";
            return;
        }

        if (!SearchMatchesInstance(instance))
        {
            ResourceStatus = "筛选条件和当前实例不一致，装进去不会生效。先把「游戏版本 / 加载器」切回「跟随实例」。";
            return;
        }

        if (instance.Loader is null && _resourceProject.Type == ResourceType.Mod)
        {
            ResourceStatus = "当前版本是原版，装模组前请先在「版本」页给它装 Fabric / Forge / NeoForge";
            return;
        }

        IsResourceBusy = true;
        ResourceProgressValue = 0;
        ResourceProgressText = "准备中…";
        ResourceStatus = $"正在安装 {_resourceProject.Title}…";
        AppendLog($"===== 安装资源 {_resourceProject.Title} {version.VersionNumber} → {instance.VersionId} =====");

        try
        {
            var installer = new ResourceInstaller(_modrinth)
            {
                Log = s => Dispatcher.UIThread.Post(() => AppendLog(s)),
                Progress = p => Dispatcher.UIThread.Post(() => ApplyResourceProgress(p)),
            };

            var report = await Task.Run(() => installer.InstallAsync(instance, _resourceProject, version))
                .ConfigureAwait(true);

            ResourceProgressValue = 1;
            ResourceStatus = report.Success
                ? $"完成：新增 {report.Added}、替换 {report.Replaced}、跳过 {report.Skipped}" +
                  (report.Dependencies.Count > 0 ? $"，附带依赖 {report.Dependencies.Count} 个" : "")
                : $"完成但有问题：{string.Join("；", report.Errors.Take(2))}";

            AppendLog(ResourceStatus);
            foreach (var d in report.Dependencies) AppendLog("  依赖：" + d);
            foreach (var w in report.Warnings) AppendLog("  警告：" + w);
        }
        catch (Exception ex)
        {
            ResourceStatus = "安装失败：" + ex.Message;
            AppendLog(ex.ToString());
        }
        finally
        {
            IsResourceBusy = false;
        }
    }

    private async Task InstallModpackAsync(ModrinthVersion version)
    {
        IsResourceBusy = true;
        ResourceProgressValue = 0;
        ResourceStatus = $"正在安装整合包 {_resourceProject!.Title}…";
        AppendLog($"===== 安装整合包 {_resourceProject.Title} {version.VersionNumber} =====");

        try
        {
            var paths = new MinecraftPaths(TargetDir);
            var installer = new ModpackInstaller(paths, new InstallOptions
            {
                Source = _sourceKind,
                MaxThreads = _maxThreads,
            })
            {
                Log = s => Dispatcher.UIThread.Post(() => AppendLog(s)),
                Progress = (phase, p) => Dispatcher.UIThread.Post(() =>
                {
                    ApplyResourceProgress(p);
                    ResourceSpeedText = phase;
                }),
            };

            var report = await Task.Run(() => installer.InstallAsync(_resourceProject, version)).ConfigureAwait(true);

            ResourceProgressValue = 1;
            ResourceStatus = report.Success
                ? $"完成：实例 {report.InstanceVersionId}，下载 {report.FilesDownloaded} 个文件，" +
                  $"覆盖 {report.OverridesExtracted} 个"
                : $"失败：{string.Join("；", report.Errors.Take(2))}";

            AppendLog(ResourceStatus);
            foreach (var step in report.Steps) AppendLog("  步骤：" + step);
        }
        catch (Exception ex)
        {
            ResourceStatus = "安装失败：" + ex.Message;
            AppendLog(ex.ToString());
        }
        finally
        {
            IsResourceBusy = false;
        }
    }

    private void ApplyResourceProgress(DownloadProgress p)
    {
        ResourceProgressValue = p.TotalBytes > 0 ? Math.Clamp((double)p.DoneBytes / p.TotalBytes, 0, 1) : 0;
        var percent = p.TotalBytes > 0 ? p.DoneBytes * 100.0 / p.TotalBytes : 0;
        ResourceProgressText = $"{SizeFormat.Of(p.DoneBytes)} / {SizeFormat.Of(p.TotalBytes)}  ({percent:0.0}%)";
        ResourceSpeedText = SizeFormat.Speed(p.BytesPerSecond);
    }

    private async Task InstallAsync()
    {
        if (SelectedVersion is null) return;

        var row = SelectedVersion;
        var dir = TargetDir?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(dir))
        {
            StatusText = $"请先选择 .minecraft 文件夹（当前值：'{dir}'）。";
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "准备中…";
        SpeedText = "";
        CurrentFileName = "";
        StatusText = $"正在安装 {row.Id}…";
        AppendLog($"===== 开始安装 {row.Id} → {dir} =====");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var paths = new MinecraftPaths(dir);
            var options = new InstallOptions { Source = _sourceKind, MaxThreads = _maxThreads };
            void OnLog(string s) => Dispatcher.UIThread.Post(() => AppendLog(s));
            void OnProgress(string phase, DownloadProgress p) => Dispatcher.UIThread.Post(() => ApplyProgress(phase, p));

            StatusText = "下载原版…";
            var vanilla = new VanillaInstaller(paths, options) { Log = OnLog, Progress = OnProgress };
            var report = await Task.Run(() => vanilla.InstallAsync(row.Entry)).ConfigureAwait(true);
            var handled = report.Downloaded + report.Skipped;
            var failed = report.Failed;
            var resultId = row.Id;

            if (report.Success && SelectedLoader != "原版")
            {
                var loaderVersion = (SelectedLoaderVersion ?? "").Split(' ')[0];
                if (loaderVersion.Length == 0)
                {
                    StatusText = "请先选择加载器版本。";
                    return;
                }

                if (SelectedLoader == "Fabric")
                {
                    StatusText = $"安装 Fabric {loaderVersion}…";
                    var fabric = new FabricInstaller(paths, options) { Log = OnLog, Progress = OnProgress };
                    var (fabricReport, profileId) =
                        await fabric.InstallAsync(row.Id, loaderVersion).ConfigureAwait(true);
                    handled += fabricReport.Downloaded + fabricReport.Skipped;
                    failed += fabricReport.Failed;
                    resultId = profileId;
                }
                else
                {
                    StatusText = "准备 Java 运行时…";
                    var javaManager = new JavaRuntimeManager(paths) { Log = OnLog, Progress = OnProgress };
                    var component = VersionJson.GuessComponent((int)report.JavaMajor);
                    var java = await javaManager.EnsureAsync(component).ConfigureAwait(true);
                    if (java is null)
                    {
                        StatusText = "找不到可用的 Java 运行时，无法运行安装器。";
                        return;
                    }

                    StatusText = $"安装 {SelectedLoader} {loaderVersion}…";
                    var forge = new ForgeInstaller(paths, options) { Log = OnLog };
                    var (ok, message, versionId) = await forge
                        .InstallAsync(row.Id, loaderVersion, java, SelectedLoader == "NeoForge").ConfigureAwait(true);
                    AppendLog(message);
                    if (ok && versionId is not null) resultId = versionId;
                    else failed++;
                }
            }

            clock.Stop();
            var avg = DownloadBytes > 0 ? DownloadBytes / Math.Max(0.001, clock.Elapsed.TotalSeconds) : 0;
            StatusText = failed == 0
                ? $"完成：{resultId} 已写入，处理 {handled} 个文件，用时 {clock.Elapsed.TotalSeconds:0.0} 秒，平均 {SizeFormat.Of((long)avg)}/s。"
                : $"完成，但有 {failed} 个文件失败（详见日志）。";

            ProgressValue = 1;
            ProgressText = "完成";
            SpeedText = "";
            AppendLog($"===== {StatusText} =====");
            foreach (var error in report.Errors.Take(5)) AppendLog("错误：" + error);
        }
        catch (Exception ex)
        {
            StatusText = "安装失败：" + ex.Message;
            AppendLog(ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyProgress(string phase, DownloadProgress p)
    {
        DownloadBytes = p.DoneBytes;
        ProgressValue = p.TotalBytes > 0 ? Math.Clamp((double)p.DoneBytes / p.TotalBytes, 0, 1) : 0;
        var percent = p.TotalBytes > 0 ? p.DoneBytes * 100.0 / p.TotalBytes : 0;
        ProgressText = $"{phase}  {SizeFormat.Of(p.DoneBytes)} / {SizeFormat.Of(p.TotalBytes)}  ({percent:0.0}%)";
        SpeedText = SizeFormat.Speed(p.BytesPerSecond);
        if (!string.IsNullOrEmpty(p.CurrentFile)) CurrentFileName = p.CurrentFile;
    }

    public void AppendLog(string text)
    {
        if (_log.Length > 200_000) _log.Clear();
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {text}");
        OnPropertyChanged(nameof(LogText));
    }

    private static string DefaultMinecraftDir()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidate = Path.Combine(roaming, ".minecraft");
        return Directory.Exists(candidate) ? candidate : Path.Combine(roaming, ".minecraft");
    }
}

public sealed class ResourceRow(ModrinthSearchHit hit) : ObservableObject
{
    public string Slug { get; } = hit.Slug;
    public string Title { get; } = hit.Title;
    public string Description { get; } = hit.Description;
    public string IconUrl { get; } = hit.IconUrl ?? "";
    public string StatsText { get; } =
        $"{hit.Type.DisplayName()}  ·  下载 {hit.Downloads:N0}  ·  {hit.Author}";

    public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title[..1].ToUpperInvariant();

    private Avalonia.Media.Imaging.Bitmap? _icon;
    public Avalonia.Media.Imaging.Bitmap? Icon => _icon;
    public bool HasIcon => _icon is not null;

    public void SetIcon(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        _icon = bitmap;
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }
}

public sealed class ResourceVersionRow(ModrinthVersion version)
{
    public ModrinthVersion Version { get; } = version;
    public string Label { get; } =
        $"{version.VersionNumber}  ·  {version.DatePublished.LocalDateTime:yyyy-MM-dd}" +
        (version.IsRelease ? "" : $"  ·  {version.VersionType}");

    public override string ToString() => Label;
}
