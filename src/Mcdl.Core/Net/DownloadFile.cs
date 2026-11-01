namespace Mcdl.Core.Net;

/// <summary>一个待下载文件。可带多个镜像地址，引擎会在多个源之间轮换。</summary>
public sealed class DownloadFile
{
    public required string Name { get; init; }
    public required string TargetPath { get; init; }
    public required IReadOnlyList<string> Urls { get; init; }
    public long Size { get; init; } = -1;
    public string? Sha1 { get; init; }

    /// <summary>是否允许分片。BMCLAPI / GitHub 之类对 Range 不友好的源会被引擎自动降级。</summary>
    public bool AllowSplit { get; init; } = true;

    public string PrimaryUrl => Urls.Count > 0 ? Urls[0] : "";
}

public sealed record DownloadProgress(
    long DoneBytes,
    long TotalBytes,
    double BytesPerSecond,
    string CurrentFile,
    int ActiveThreads);

public sealed record DownloadResult(int Downloaded, int Skipped, int Failed, IReadOnlyList<string> Errors);
