using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mcdl.Core;

namespace Mcdl.App.ViewModels;

/// <summary>
/// Modrinth 图标下载缓存。图标都是几十 KB 的小 PNG：
/// 同一个 URL 只下一次（并发请求共享同一个 Task），下载完成后回 UI 线程再赋值给绑定。
/// </summary>
public static class IconCache
{
    private static readonly Dictionary<string, Task<Bitmap?>> Tasks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static readonly object Lock = new();

    public static Task<Bitmap?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<Bitmap?>(null);

        lock (Lock)
        {
            if (Tasks.TryGetValue(url, out var existing)) return existing;
            var task = DownloadAsync(url);
            Tasks[url] = task;
            return task;
        }
    }

    private static async Task<Bitmap?> DownloadAsync(string url)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await HttpUtil.GetBytesAsync(url).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>给列表行加载图标，完成后回填（UI 线程）。</summary>
    public static async Task FillAsync(ResourceRow row)
    {
        if (row.HasIcon || string.IsNullOrEmpty(row.IconUrl)) return;

        var bitmap = await GetAsync(row.IconUrl).ConfigureAwait(false);
        if (bitmap is null) return;

        await Dispatcher.UIThread.InvokeAsync(() => row.SetIcon(bitmap));
    }
}
