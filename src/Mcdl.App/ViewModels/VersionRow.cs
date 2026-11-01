using Mcdl.Core;

namespace Mcdl.App.ViewModels;

public sealed class VersionRow
{
    public VersionRow(VersionEntry entry, bool isLatest)
    {
        Entry = entry;
        Id = entry.Id;
        IsLatest = isLatest;
        TypeLabel = entry.IsRelease ? (isLatest ? "最新" : "正式") : "快照";
        SubText = entry.ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd");
    }

    public VersionEntry Entry { get; }
    public string Id { get; }
    public string SubText { get; }
    public string TypeLabel { get; }
    public bool IsLatest { get; }
}
