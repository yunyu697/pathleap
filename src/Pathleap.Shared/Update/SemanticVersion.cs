namespace Pathleap.Shared.Update;

/// <summary>
/// 语义化版本比较（含预发布优先级）。规则摘要：
/// <list type="bullet">
/// <item>数字段按数值比较，<c>1.0.10 &gt; 1.0.9</c>；</item>
/// <item>有预发布标识的版本低于同数字段的正式版（<c>1.0.4-alpha1 &lt; 1.0.4</c>）；</item>
/// <item>预发布标识逐段比较：数字段按数值，数字段低于字母段，字母段按 ASCII；</item>
/// <item>构建元数据（<c>+</c> 之后）不参与比较。</item>
/// </list>
/// </summary>
public static class SemanticVersion
{
    /// <summary>返回 -1 / 0 / 1；任一侧无法解析时抛 <see cref="FormatException"/>。</summary>
    public static int Compare(string left, string right)
    {
        var lpLeft = Parse(left);
        var lpRight = Parse(right);

        if (lpLeft.Major != lpRight.Major)
        {
            return lpLeft.Major > lpRight.Major ? 1 : -1;
        }

        if (lpLeft.Minor != lpRight.Minor)
        {
            return lpLeft.Minor > lpRight.Minor ? 1 : -1;
        }

        if (lpLeft.Patch != lpRight.Patch)
        {
            return lpLeft.Patch > lpRight.Patch ? 1 : -1;
        }

        return ComparePreRelease(lpLeft.PreRelease, lpRight.PreRelease);
    }

    /// <summary>远程版本是否比本地版本新（相等、本地更高、远程预发布均按语义化规则判定）。</summary>
    public static bool IsNewer(string remote, string local) => Compare(remote, local) > 0;

    private static VersionParts Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("版本号为空");
        }

        var lpText = value.Trim();
        if (lpText.Length > 1 && (lpText[0] == 'v' || lpText[0] == 'V'))
        {
            lpText = lpText[1..];
        }

        var lpBuildIndex = lpText.IndexOf('+');
        if (lpBuildIndex >= 0)
        {
            lpText = lpText[..lpBuildIndex];
        }

        var lpDashIndex = lpText.IndexOf('-');
        var lpCore = lpDashIndex >= 0 ? lpText[..lpDashIndex] : lpText;
        var lpPre = lpDashIndex >= 0 ? lpText[(lpDashIndex + 1)..] : string.Empty;

        var lpSegments = lpCore.Split('.');
        if (lpSegments.Length != 3)
        {
            throw new FormatException($"版本号必须是 major.minor.patch：{value}");
        }

        var lpMajor = ParseNumber(lpSegments[0], value);
        var lpMinor = ParseNumber(lpSegments[1], value);
        var lpPatch = ParseNumber(lpSegments[2], value);

        var lpIdentifiers = lpPre.Length == 0 ? Array.Empty<string>() : lpPre.Split('.');
        foreach (var lpIdentifier in lpIdentifiers)
        {
            if (lpIdentifier.Length == 0)
            {
                throw new FormatException($"预发布标识为空段：{value}");
            }
        }

        return new VersionParts(lpMajor, lpMinor, lpPatch, lpIdentifiers);
    }

    private static int ParseNumber(string segment, string original)
    {
        if (segment.Length == 0 || !segment.All(char.IsAsciiDigit))
        {
            throw new FormatException($"版本号含非数字段：{original}");
        }

        return int.Parse(segment);
    }

    private static int ComparePreRelease(string[] left, string[] right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        // 没有预发布标识的一方是正式版，优先级更高。
        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var lpMax = Math.Max(left.Length, right.Length);
        for (var lpIndex = 0; lpIndex < lpMax; lpIndex++)
        {
            if (lpIndex >= left.Length)
            {
                return -1;
            }

            if (lpIndex >= right.Length)
            {
                return 1;
            }

            var lpLeft = left[lpIndex];
            var lpRight = right[lpIndex];
            var lpLeftNumeric = lpLeft.All(char.IsAsciiDigit);
            var lpRightNumeric = lpRight.All(char.IsAsciiDigit);

            if (lpLeftNumeric && lpRightNumeric)
            {
                var lpLeftNumber = int.Parse(lpLeft);
                var lpRightNumber = int.Parse(lpRight);
                if (lpLeftNumber != lpRightNumber)
                {
                    return lpLeftNumber > lpRightNumber ? 1 : -1;
                }

                continue;
            }

            if (lpLeftNumeric != lpRightNumeric)
            {
                // 数字标识优先级低于字母标识。
                return lpLeftNumeric ? -1 : 1;
            }

            var lpComparison = string.CompareOrdinal(lpLeft, lpRight);
            if (lpComparison != 0)
            {
                return lpComparison > 0 ? 1 : -1;
            }
        }

        return 0;
    }

    private sealed record VersionParts(int Major, int Minor, int Patch, string[] PreRelease);
}
