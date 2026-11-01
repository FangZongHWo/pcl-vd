using System.Text.Json.Serialization;

namespace Mcdl.Core.Resources;

/// <summary>Modrinth 的 project_type 取值，以及它对应的安装目录。</summary>
public enum ResourceType
{
    Mod,
    Shader,
    ResourcePack,
    DataPack,
    Modpack,
}

public static class ResourceTypes
{
    public static ResourceType Parse(string? projectType) => projectType?.ToLowerInvariant() switch
    {
        "shader" => ResourceType.Shader,
        "resourcepack" => ResourceType.ResourcePack,
        "datapack" => ResourceType.DataPack,
        "modpack" => ResourceType.Modpack,
        _ => ResourceType.Mod,
    };

    public static string ApiName(this ResourceType type) => type switch
    {
        ResourceType.Shader => "shader",
        ResourceType.ResourcePack => "resourcepack",
        ResourceType.DataPack => "datapack",
        ResourceType.Modpack => "modpack",
        _ => "mod",
    };

    public static string DisplayName(this ResourceType type) => type switch
    {
        ResourceType.Shader => "光影",
        ResourceType.ResourcePack => "资源包",
        ResourceType.DataPack => "数据包",
        ResourceType.Modpack => "整合包",
        _ => "模组",
    };

    /// <summary>文件落到哪个子目录（相对实例根目录）。数据包需要先选世界，单独处理。</summary>
    public static string Folder(this ResourceType type) => type switch
    {
        ResourceType.Shader => "shaderpacks",
        ResourceType.ResourcePack => "resourcepacks",
        ResourceType.DataPack => "datapacks",
        _ => "mods",
    };
}

public sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")] public List<ModrinthSearchHit> Hits { get; set; } = new();
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public sealed class ModrinthSearchHit
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = new();
    [JsonPropertyName("client_side")] public string? ClientSide { get; set; }
    [JsonPropertyName("server_side")] public string? ServerSide { get; set; }
    [JsonPropertyName("date_modified")] public DateTimeOffset DateModified { get; set; }

    [JsonIgnore] public ResourceType Type => ResourceTypes.Parse(ProjectType);
}

public sealed class ModrinthProject
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("followers")] public long Followers { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("client_side")] public string? ClientSide { get; set; }
    [JsonPropertyName("server_side")] public string? ServerSide { get; set; }
    [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
    [JsonPropertyName("issues_url")] public string? IssuesUrl { get; set; }

    [JsonIgnore] public ResourceType Type => ResourceTypes.Parse(ProjectType);
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string? VersionType { get; set; }
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("date_published")] public DateTimeOffset DatePublished { get; set; }
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();

    /// <summary>主文件：Modrinth 约定第一个就是主文件，其余是 sources/javadoc。</summary>
    [JsonIgnore] public ModrinthFile? PrimaryFile => Files.FirstOrDefault();

    [JsonIgnore] public bool IsRelease => string.Equals(VersionType, "release", StringComparison.OrdinalIgnoreCase);

    public string Label => $"{VersionNumber}  ·  {DatePublished.LocalDateTime:yyyy-MM-dd}" +
                           (IsRelease ? "" : $"  ({VersionType})");
}

public sealed class ModrinthFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = new();

    [JsonIgnore] public string? Sha1 => Hashes.TryGetValue("sha1", out var v) ? v : null;
    [JsonIgnore] public string? Sha512 => Hashes.TryGetValue("sha512", out var v) ? v : null;
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "required";

    [JsonIgnore] public bool IsRequired => string.Equals(DependencyType, "required", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsIncompatible => string.Equals(DependencyType, "incompatible", StringComparison.OrdinalIgnoreCase);
}
