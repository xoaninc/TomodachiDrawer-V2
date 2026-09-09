using System.Globalization;
using System.Text.RegularExpressions;

namespace TomodachiDrawer.Core;

/// <summary>Ordering for this project's stable and rc tags. Build metadata does not affect order.</summary>
public static partial class ReleaseVersion
{
    [GeneratedRegex(
        @"^[vV]?(\d+)\.(\d+)\.(\d+)(?:-rc\.(\d+))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex TagPattern();

    public static bool TryCompare(string left, string right, out int comparison)
    {
        comparison = 0;
        if (
            !TryParse(left, out var leftCore, out var leftRc)
            || !TryParse(right, out var rightCore, out var rightRc)
        )
            return false;

        comparison = leftCore.CompareTo(rightCore);
        if (comparison != 0)
            return true;
        comparison = (leftRc, rightRc) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            (int a, int b) => a.CompareTo(b),
        };
        return true;
    }

    private static bool TryParse(string text, out Version core, out int? rc)
    {
        core = new Version(0, 0, 0);
        rc = null;
        var match = TagPattern().Match(text);
        if (!match.Success)
            return false;
        var parts = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (
                !int.TryParse(
                    match.Groups[i + 1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parts[i]
                )
            )
                return false;
        if (match.Groups[4].Success)
        {
            if (
                !int.TryParse(
                    match.Groups[4].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int number
                )
                || number < 1
            )
                return false;
            rc = number;
        }
        core = new Version(parts[0], parts[1], parts[2]);
        return true;
    }
}
