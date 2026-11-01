namespace Mcdl.Core.Versions;

public enum SourceKind
{
    /// <summary>官方优先，失败自动回落镜像。</summary>
    Auto = 0,
    Official = 1,
    BmclApi = 2,
}

/// <summary>一个待下载文件的候选地址 + 是否允许分片。</summary>
public readonly record struct SourceFile(IReadOnlyList<string> Urls, bool AllowSplit);

/// <summary>
/// 下载源。全部走 BMCLAPI 时，Maven 端点对大文件的 Range 支持不可靠（实测会卡住），
/// 所以镜像优先时库文件一律单线程；资源对象和客户端 jar 的 Range 实测正常，仍然分片。
/// </summary>
public sealed class SourceSet
{
    public const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string MirrorManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    public const string OfficialLibraries = "https://libraries.minecraft.net/";
    public const string MirrorMaven = "https://bmclapi2.bangbang93.com/maven/";
    public const string OfficialResources = "https://resources.download.minecraft.net/";
    public const string MirrorAssets = "https://bmclapi2.bangbang93.com/assets/";
    public const string OfficialPistonMeta = "https://piston-meta.mojang.com";
    public const string MirrorBase = "https://bmclapi2.bangbang93.com";
    public const string FabricMeta = "https://meta.fabricmc.net/v2";
    public const string MirrorFabricMeta = "https://bmclapi2.bangbang93.com/fabric-meta/v2";

    public SourceKind Kind { get; }

    private SourceSet(SourceKind kind) => Kind = kind;

    public static SourceSet Of(SourceKind kind) => new(kind);

    /// <summary>镜像优先（用户显式选了 BMCLAPI）。</summary>
    public bool MirrorFirst => Kind == SourceKind.BmclApi;

    /// <summary>是否把镜像放进候选列表（Auto / BmclApi）。</summary>
    public bool UseMirror => Kind != SourceKind.Official;

    public string DisplayName => Kind switch
    {
        SourceKind.BmclApi => "BMCLAPI 镜像",
        SourceKind.Official => "官方（Mojang）",
        _ => "自动",
    };

    private IReadOnlyList<string> Ordered(string official, string? mirror)
    {
        if (!UseMirror || mirror is null) return new[] { official };
        return MirrorFirst ? new[] { mirror, official } : new[] { official, mirror };
    }

    public IReadOnlyList<string> ManifestUrls => Ordered(OfficialManifest, MirrorManifest);

    public IReadOnlyList<string> VersionJsonUrls(string id, string? officialUrl)
    {
        var official = string.IsNullOrWhiteSpace(officialUrl)
            ? $"{OfficialPistonMeta}/version/{Uri.EscapeDataString(id)}/json"
            : officialUrl;
        return Ordered(official, $"{MirrorBase}/version/{Uri.EscapeDataString(id)}/json");
    }

    /// <summary>Maven 上的依赖库。</summary>
    public SourceFile Library(string relativePath)
        => new(Ordered(OfficialLibraries + relativePath, MirrorMaven + relativePath), !MirrorFirst);

    /// <summary>Fabric 之类自带 maven 根的库。</summary>
    public SourceFile MavenOn(string mavenRoot, string relativePath)
    {
        var official = mavenRoot.TrimEnd('/') + "/" + relativePath;
        return new SourceFile(Ordered(official, MirrorMaven + relativePath), !MirrorFirst);
    }

    /// <summary>资源对象（assets/objects）。Range 实测可用，始终允许分片。</summary>
    public SourceFile Asset(string hash)
    {
        var suffix = $"{hash[..2]}/{hash}";
        return new SourceFile(Ordered(OfficialResources + suffix, MirrorAssets + suffix), true);
    }

    /// <summary>piston-meta / piston-data / launchermeta 上的整包文件（客户端 jar、资源索引、日志配置）。</summary>
    public SourceFile Packaged(string officialUrl)
    {
        string mirror;
        if (officialUrl.StartsWith(OfficialPistonMeta, StringComparison.OrdinalIgnoreCase))
            mirror = MirrorBase + officialUrl[OfficialPistonMeta.Length..];
        else if (officialUrl.StartsWith("https://piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            mirror = MirrorBase + officialUrl["https://piston-data.mojang.com".Length..];
        else if (officialUrl.StartsWith("https://launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
            mirror = MirrorBase + officialUrl["https://launchermeta.mojang.com".Length..];
        else
            mirror = officialUrl;

        return new SourceFile(Ordered(officialUrl, mirror), true);
    }

    public IReadOnlyList<string> FabricMetaBases => Ordered(FabricMeta, MirrorFabricMeta);

    public IReadOnlyList<string> ForgeMetadataUrls =>
        Ordered("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
                MirrorMaven + "net/minecraftforge/forge/maven-metadata.xml");

    public IReadOnlyList<string> NeoForgeMetadataUrls =>
        new[] { "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml" };
}
