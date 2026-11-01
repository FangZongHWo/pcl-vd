using System.Text.Json;
using Mcdl.Core.Versions;

namespace Mcdl.Core.Resources;

/// <summary>
/// 一个"实例"= versions/&lt;id&gt;/ 目录。按 HMCL / PCL2 的「版本隔离」约定，
/// 模组、光影、资源包都装在实例目录下，不同版本之间互不污染。
/// </summary>
public sealed class InstanceContext
{
    public required string VersionId { get; init; }
    public required string MinecraftVersion { get; init; }
    public required string Root { get; init; }          // .minecraft
    public required string InstanceDir { get; init; }   // .minecraft/versions/<id>
    public string? Loader { get; init; }                // fabric / forge / neoforge / null
    public string? LoaderVersion { get; init; }

    public string Mods => Path.Combine(InstanceDir, "mods");
    public string ShaderPacks => Path.Combine(InstanceDir, "shaderpacks");
    public string ResourcePacks => Path.Combine(InstanceDir, "resourcepacks");
    public string StateFile => Path.Combine(InstanceDir, ".mcdl-resources.json");

    public string FolderFor(ResourceType type) => Path.Combine(InstanceDir, type.Folder());

    public static InstanceContext? TryLoad(MinecraftPaths paths, string versionId, Action<string>? log = null)
    {
        var jsonPath = paths.VersionJsonPath(versionId);
        if (!File.Exists(jsonPath))
        {
            log?.Invoke($"找不到版本 {versionId} 的 JSON，无法确定要装到哪个实例");
            return null;
        }

        VersionJson? json;
        try
        {
            json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath), HttpUtil.JsonOptions);
        }
        catch (Exception ex)
        {
            log?.Invoke($"版本 JSON 解析失败：{ex.Message}");
            return null;
        }

        var mcVersion = json?.InheritsFrom ?? versionId;
        var (loader, loaderVersion) = GuessLoader(versionId, json);

        return new InstanceContext
        {
            VersionId = versionId,
            MinecraftVersion = mcVersion,
            Root = paths.Root,
            InstanceDir = paths.VersionDir(versionId),
            Loader = loader,
            LoaderVersion = loaderVersion,
        };
    }

    private static (string? Loader, string? LoaderVersion) GuessLoader(string id, VersionJson? json)
    {
        var mainClass = json?.MainClass ?? "";
        var lower = id.ToLowerInvariant();

        if (lower.StartsWith("fabric-loader") || mainClass.Contains("fabricmc", StringComparison.OrdinalIgnoreCase))
        {
            // fabric-loader-0.19.5-26.3
            var parts = id.Split('-');
            var version = parts.Length > 2 ? parts[2] : null;
            return ("fabric", version);
        }

        if (lower.Contains("neoforge") || mainClass.Contains("neoforged", StringComparison.OrdinalIgnoreCase))
        {
            var idx = lower.IndexOf("neoforge-", StringComparison.Ordinal);
            return ("neoforge", idx >= 0 ? id[(idx + "neoforge-".Length)..] : null);
        }

        if (lower.Contains("forge") || mainClass.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            var idx = lower.IndexOf("forge-", StringComparison.Ordinal);
            return ("forge", idx >= 0 ? id[(idx + "forge-".Length)..] : null);
        }

        if (lower.Contains("quilt")) return ("quilt", null);

        return (null, null);
    }
}

public sealed class InstalledResource
{
    public string ProjectId { get; set; } = "";
    public string ProjectTitle { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Type { get; set; } = "mod";
    public bool IsDependency { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ResourceState
{
    public List<InstalledResource> Installed { get; set; } = new();
    public List<string> Modpacks { get; set; } = new();
}
