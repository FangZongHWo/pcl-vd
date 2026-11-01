using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcdl.Core.Versions;

public sealed class VersionJson
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("jar")] public string? Jar { get; set; }
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; set; }
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionRef? JavaVersion { get; set; }
    [JsonPropertyName("logging")] public LoggingConfig? Logging { get; set; }
    [JsonPropertyName("libraries")] public List<Library> Libraries { get; set; } = new();
    [JsonPropertyName("releaseTime")] public string? ReleaseTime { get; set; }

    [JsonIgnore] public string RequiredJavaComponent => JavaVersion?.Component ?? GuessComponent(JavaVersion?.MajorVersion ?? 8);
    [JsonIgnore] public int RequiredJavaMajor => JavaVersion?.MajorVersion ?? 8;

    /// <summary>老版本没有 javaVersion 字段，按大版本兜底。</summary>
    public static string GuessComponent(int major) => major switch
    {
        <= 8 => "jre-legacy",
        16 => "java-runtime-alpha",
        17 => "java-runtime-gamma",
        21 => "java-runtime-delta",
        _ => "java-runtime-epsilon",
    };
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")] public Artifact? Client { get; set; }
    [JsonPropertyName("server")] public Artifact? Server { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
}

public sealed class Artifact
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class Library
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("rules")] public List<Rule>? Rules { get; set; }
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("extract")] public ExtractRules? Extract { get; set; }

    /// <summary>由 maven 坐标推出相对路径，用于没有 downloads 字段的库（Fabric 生成的 profile 就是这样）。</summary>
    public string MavenPath()
    {
        var parts = Name.Split(':');
        if (parts.Length < 3) return Name;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public Artifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, Artifact>? Classifiers { get; set; }
}

public sealed class ExtractRules
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
}

public sealed class Rule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public OsRule? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class LoggingConfig
{
    [JsonPropertyName("client")] public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")] public string? Argument { get; set; }
    [JsonPropertyName("file")] public Artifact? File { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class AssetIndex
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = new();
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>规则求值。Mojang 语义：有 rules 时初始为 false，按顺序应用命中的规则。</summary>
public static class RuleEvaluator
{
    public static string CurrentOsName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string CurrentOsArch => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "arm32",
    };

    public static bool IsAllowed(List<Rule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(Rule rule)
    {
        // 游戏特性（demo、自定义分辨率等）在纯下载场景下一律视为不启用
        if (rule.Features is { Count: > 0 })
            return rule.Features.All(kv => kv.Value == false);

        var os = rule.Os;
        if (os is null) return true;

        if (!string.IsNullOrEmpty(os.Name) && !string.Equals(os.Name, CurrentOsName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(os.Arch))
        {
            var arch = CurrentOsArch;
            // Mojang 的 x86 规则只对 32 位 Windows 生效
            if (string.Equals(os.Arch, "x86", StringComparison.OrdinalIgnoreCase))
            {
                if (arch != "x86") return false;
            }
            else if (!string.Equals(os.Arch, arch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(os.Version))
        {
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.Version.ToString(), os.Version))
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }
}
