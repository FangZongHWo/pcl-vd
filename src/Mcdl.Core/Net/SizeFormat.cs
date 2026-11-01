namespace Mcdl.Core.Net;

public static class SizeFormat
{
    public static string Of(long bytes) => bytes switch
    {
        < 0 => "未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.##} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Of((long)bytesPerSecond) + "/s";
}
