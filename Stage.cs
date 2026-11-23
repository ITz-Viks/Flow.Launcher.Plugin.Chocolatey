using System.Text.RegularExpressions;

namespace Flow.Launcher.Plugin.Choco;

/// <summary>Key = translation key (stage_*), Args = format args, Percent -1 = indeterminate.</summary>
public sealed record Stage(string Key, string[] Args, int Percent);

// Maps one line of choco output to a stage. Pure so selftest.ps1 can hit it.
public static partial class StageParser
{
    [GeneratedRegex(@"^Progress: Downloading (\S+) (\S+?)\.\.\. (\d{1,3})%")] private static partial Regex PkgDl();
    [GeneratedRegex(@"^Progress: (\d{1,3})% - (.+?)\s*$")] private static partial Regex FileDl();
    [GeneratedRegex(@"\(([\d.,]+ [KMGT]?B)\)\.?$")] private static partial Regex SizeSuffix();
    [GeneratedRegex(@"^Download of .+ completed")] private static partial Regex DlDone();
    [GeneratedRegex(@"^Downloading (.+?)\s*$")] private static partial Regex DlStart();
    [GeneratedRegex(@"^You have (\S+) v(\S+) installed\. Version (\S+) is available")] private static partial Regex Upg();
    [GeneratedRegex(@"^(Installing|Upgrading|Uninstalling) the following packages")] private static partial Regex Header();
    [GeneratedRegex(@"^Installing (.+?)\.\.\.$")] private static partial Regex Inst();
    [GeneratedRegex(@"^Extracting ")] private static partial Regex Extract();
    [GeneratedRegex(@"^Running auto uninstaller")] private static partial Regex AutoUninst();
    [GeneratedRegex(@"^(\S+) has been (?:successfully )?(installed|uninstalled|upgraded)")] private static partial Regex Done();
    [GeneratedRegex(@"^Chocolatey (installed|upgraded|uninstalled) (\d+)/(\d+) packages?")] private static partial Regex Sum();

    public static Stage? Of(string raw)
    {
        var l = raw.Trim();
        if (l.Length == 0) return null;
        Match m;
        if ((m = PkgDl().Match(l)).Success) return new("stage_pkg_download", [$"{m.Groups[1].Value} {m.Groups[2].Value}"], Pct(m.Groups[3]));
        if ((m = FileDl().Match(l)).Success)
        {
            // "Saving 1.2 MB of 15 MB" or "Completed download of C:\...\setup.exe (123.82 MB)."
            var detail = m.Groups[2].Value;
            detail = detail.StartsWith("Saving ") ? detail[7..] : SizeSuffix().Match(detail) is { Success: true } s ? s.Groups[1].Value : "";
            return new("stage_file_download", [detail.Length > 0 ? "· " + detail : ""], Pct(m.Groups[1]));
        }
        if (DlDone().IsMatch(l)) return new("stage_verifying", [], -1);
        if ((m = DlStart().Match(l)).Success) return new("stage_downloading", [m.Groups[1].Value], 0);
        if ((m = Upg().Match(l)).Success) return new("stage_upgrading_from", [m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value], -1);
        if ((m = Header().Match(l)).Success) return new("stage_" + m.Groups[1].Value.ToLowerInvariant(), [], -1);
        if ((m = Inst().Match(l)).Success) return new("stage_installing_pkg", [m.Groups[1].Value], -1);
        if (Extract().IsMatch(l)) return new("stage_extracting", [], -1);
        if (AutoUninst().IsMatch(l)) return new("stage_running_uninstaller", [], -1);
        if ((m = Done().Match(l)).Success) return new("stage_done_" + m.Groups[2].Value, [m.Groups[1].Value], -1);
        return null;
    }

    /// <summary>"Chocolatey installed 2/2 packages." → (installed, 2, 2)</summary>
    public static (string Verb, int Done, int Total)? Summary(string raw)
    {
        var m = Sum().Match(raw.Trim());
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    static int Pct(Group g) => Math.Clamp(int.Parse(g.Value), 0, 100);
}
