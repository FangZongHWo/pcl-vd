using System.Globalization;

namespace Mcdl.Core;

/// <summary>
/// 混合版本号。2026 年起 Mojang 把 <c>1.21.11</c> 之后改成了 <c>26.1</c> / <c>26.2</c> / <c>26.3</c>，
/// 旧格式与新格式混在同一个清单里，所以不能按字符串排序。
/// 规则：取 id 开头的「数字.数字.数字」前缀做数值比较，26 &gt; 1 自然成立；
/// <c>26w14a</c>、<c>a1.2.6</c> 这类没有点分前缀的，解析失败，由调用方按快照处理。
/// </summary>
public readonly record struct McVersion(int Major, int Minor, int Patch) : IComparable<McVersion>
{
    public static readonly McVersion V1_13 = new(1, 13, 0);

    public int CompareTo(McVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(McVersion a, McVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(McVersion a, McVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(McVersion a, McVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(McVersion a, McVersion b) => a.CompareTo(b) <= 0;

    /// <summary>从版本 id 解析。失败返回 false（快照、愚人节版本等）。</summary>
    public static bool TryParse(string? id, out McVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(id)) return false;

        var parts = new List<int>(3);
        var seg = 0;
        var seenDot = false;

        for (var i = 0; i < id.Length; i++)
        {
            var ch = id[i];
            if (char.IsAsciiDigit(ch))
            {
                seg = seg * 10 + (ch - '0');
                if (seg > 100000) return false;
            }
            else if (ch == '.' && i + 1 < id.Length && char.IsAsciiDigit(id[i + 1]))
            {
                parts.Add(seg);
                seg = 0;
                seenDot = true;
                if (parts.Count == 3) return false;
            }
            else
            {
                break;
            }
        }

        if (!seenDot) return false; // 26w14a / a1.2.6 之类
        parts.Add(seg);

        version = new McVersion(
            parts.Count > 0 ? parts[0] : 0,
            parts.Count > 1 ? parts[1] : 0,
            parts.Count > 2 ? parts[2] : 0);
        return true;
    }

    /// <summary>是否属于「1.13 及以上」（含 2026 新命名 26.x）。</summary>
    public static bool IsSupportedByApp(string? id)
        => TryParse(id, out var v) && v >= V1_13;

    public override string ToString()
        => Patch > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
