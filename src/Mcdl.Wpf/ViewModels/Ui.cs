using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mcdl.Core;

namespace Mcdl.App.ViewModels;

/// <summary>WPF 版 UI 线程调度 + 图标缓存（Modrinth 图标是几十 KB 的小 PNG，按 URL 只下一次）。</summary>
public static class Ui
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public static Task InvokeAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}

public static class IconCache
{
    private static readonly Dictionary<string, Task<ImageSource?>> Tasks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static readonly object Lock = new();

    public static Task<ImageSource?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource?>(null);

        lock (Lock)
        {
            if (Tasks.TryGetValue(url, out var existing)) return existing;
            var task = DownloadAsync(url);
            Tasks[url] = task;
            return task;
        }
    }

    private static async Task<ImageSource?> DownloadAsync(string url)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await HttpUtil.GetBytesAsync(url).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
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

    public static async Task FillAsync(ResourceRow row)
    {
        if (row.HasIcon || string.IsNullOrEmpty(row.IconUrl)) return;

        var bitmap = await GetAsync(row.IconUrl).ConfigureAwait(false);
        if (bitmap is null) return;

        await Ui.InvokeAsync(() => row.SetIcon(bitmap));
    }
}
