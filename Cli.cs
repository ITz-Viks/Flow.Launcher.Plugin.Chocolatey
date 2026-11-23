using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Flow.Launcher.Plugin.Choco;

public sealed record Source(string Name, string Url)
{
    public bool IsCommunity => Url.Contains("community.chocolatey.org", StringComparison.OrdinalIgnoreCase);
}

// Thin wrappers around non-elevated choco calls + their output parsers (pure, used by selftest.ps1).
public static partial class Cli
{
    [GeneratedRegex(@"^([A-Za-z0-9._-]+)\|([^|\s]+)\s*$", RegexOptions.Multiline)] private static partial Regex IdVersion();

    /// <summary>"name|url|disabled|user|cert|priority|..." → enabled sources</summary>
    public static List<Source> ParseSources(string stdout) =>
        stdout.Split('\n').Select(l => l.Trim().Split('|')).Where(f => f.Length >= 3 && f[2] == "False")
            .Select(f => new Source(f[0], f[1])).ToList();

    /// <summary>"id|version" lines (choco search -r); everything else is noise</summary>
    public static List<(string Id, string Version)> ParseIdVersion(string stdout) =>
        IdVersion().Matches(stdout).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    /// <summary>packages.config → (id, version?)</summary>
    public static List<(string Id, string? Version)> ParsePackagesConfig(string path) =>
        XDocument.Load(path).Root!.Elements("package")
            .Select(e => ((string?)e.Attribute("id") ?? "", (string?)e.Attribute("version")))
            .Where(p => p.Item1.Length > 0).ToList();

    public static async Task<string> Run(string exe, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a); // proper quoting for user-typed terms
        using var p = Process.Start(psi)!;
        await using var _ = ct.Register(() => { try { p.Kill(true); } catch { } });
        var err = p.StandardError.ReadToEndAsync(CancellationToken.None); // drain so the pipe can't block choco
        var stdout = await p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await Task.WhenAll(err, p.WaitForExitAsync(CancellationToken.None));
        ct.ThrowIfCancellationRequested();
        return stdout;
    }

    // Runs elevated + hidden: starts the command with output to the log, and kills its whole tree
    // when the (non-elevated) card drops the stop file. Exit 1223 = ERROR_CANCELLED.
    public static string Wrapper(string exe, string args, string logFile, string stopFile)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return $$"""
            $p = Start-Process -FilePath 'cmd.exe' -ArgumentList {{Q($"/c \"\"{exe}\" {args} > \"{logFile}\" 2>&1\"")}} -WindowStyle Hidden -PassThru
            $null = $p.Handle
            while (-not $p.HasExited) {
              if (Test-Path -LiteralPath {{Q(stopFile)}}) { & taskkill.exe /T /F /PID $p.Id | Out-Null; $p.WaitForExit(); exit 1223 }
              Start-Sleep -Milliseconds 250
            }
            exit $p.ExitCode
            """;
    }
}
