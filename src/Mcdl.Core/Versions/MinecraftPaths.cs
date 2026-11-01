namespace Mcdl.Core.Versions;

/// <summary>标准 .minecraft 目录约定，和官方启动器 / Prism 完全一致，装完能被它们直接识别。</summary>
public sealed class MinecraftPaths(string root)
{
    public string Root { get; } = root;
    public string Versions => Path.Combine(Root, "versions");
    public string Libraries => Path.Combine(Root, "libraries");
    public string Assets => Path.Combine(Root, "assets");
    public string Objects => Path.Combine(Assets, "objects");
    public string Indexes => Path.Combine(Assets, "indexes");
    public string LogConfigs => Path.Combine(Assets, "log_configs");
    public string Runtime => Path.Combine(Root, "runtime");
    public string VersionDir(string id) => Path.Combine(Versions, id);
    public string VersionJsonPath(string id) => Path.Combine(VersionDir(id), id + ".json");
    public string VersionJarPath(string id) => Path.Combine(VersionDir(id), id + ".jar");

    public void EnsureBase()
    {
        Directory.CreateDirectory(Versions);
        Directory.CreateDirectory(Libraries);
        Directory.CreateDirectory(Objects);
        Directory.CreateDirectory(Indexes);
    }

    /// <summary>Forge / NeoForge 的安装器会检查这个文件，不存在就写一个最小桩。</summary>
    public void EnsureLauncherProfiles()
    {
        var path = Path.Combine(Root, "launcher_profiles.json");
        if (File.Exists(path)) return;

        EnsureBase();
        File.WriteAllText(path, """
        {
          "profiles": {},
          "settings": { "enableSnapshots": true, "keepLauncherOpen": false },
          "version": 3
        }
        """);
    }
}
