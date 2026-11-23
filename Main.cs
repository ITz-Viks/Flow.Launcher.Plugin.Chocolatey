using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;

namespace Flow.Launcher.Plugin.Choco;

/// <summary>Source = null for the community repository, else the choco source name.</summary>
public sealed record Pkg(string Id, string Title, string Version, string? IconUrl, string? Status, long Downloads, string? Source = null);
public sealed record Outdated(string Id, string Current, string Available);

public static partial class Parse
{
    [GeneratedRegex(@"^([A-Za-z0-9._-]+)\|([^|\s]+)\|([^|\s]+)\|(true|false)\s*$", RegexOptions.Multiline)]
    private static partial Regex OutdatedLine();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeIdRx();

    [GeneratedRegex(@"^[0-9A-Za-z.+-]+$")]
    private static partial Regex SafeVersionRx();

    // choco outdated -r prints "id|current|available|pinned" plus retry/warning noise we must ignore
    public static List<Outdated> OutdatedOutput(string stdout) =>
        OutdatedLine().Matches(stdout).Select(m => new Outdated(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)).ToList();

    // ids end up on an elevated command line, versions in API URLs: only allow nuget characters
    public static bool SafeId(string id) => SafeIdRx().IsMatch(id);
    public static bool SafeVersion(string v) => SafeVersionRx().IsMatch(v);

    // "foo.install"/"foo.portable" is the real payload of meta package "foo": hide it when "foo" is present
    public static string? MetaBase(string id) =>
        id.EndsWith(".install", StringComparison.OrdinalIgnoreCase) ? id[..^8] :
        id.EndsWith(".portable", StringComparison.OrdinalIgnoreCase) ? id[..^9] : null;

    public static string? SniffExt(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return ".png";
        if (b.StartsWith(new byte[] { 0x00, 0x00, 0x01, 0x00 })) return ".ico";
        if (b.StartsWith(new byte[] { 0xFF, 0xD8 })) return ".jpg";
        if (b.StartsWith("GIF8"u8)) return ".gif";
        var head = Encoding.UTF8.GetString(b[..Math.Min(b.Length, 1024)]);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? ".svg" : null;
    }

    public static string Count(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000d:0.#}M",
        >= 1_000 => $"{n / 1_000d:0.#}k",
        _ => n.ToString()
    };

    static readonly XNamespace Atom = "http://www.w3.org/2005/Atom", D = "http://schemas.microsoft.com/ado/2007/08/dataservices",
        M = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    public static List<Pkg> Feed(string xml) =>
        XDocument.Parse(xml).Root!.Elements(Atom + "entry").Select(e =>
        {
            var p = e.Element(M + "properties")!;
            string? P(string n) => (string?)p.Element(D + n) is { Length: > 0 } v ? v : null;
            var id = (string)e.Element(Atom + "title")!;
            return new Pkg(id, P("Title") ?? id, P("Version") ?? "?", P("IconUrl"), P("PackageStatus"),
                long.TryParse(P("DownloadCount"), out var dl) ? dl : 0);
        }).ToList();
}

public class Main : IAsyncPlugin, IContextMenu, IPluginI18n, ISettingProvider
{
    const string Api = "https://community.chocolatey.org/api/v2/";
    const string Select = "$select=Id,Version,Title,IconUrl,PackageStatus,DownloadCount";
    const string DetailSelect = "$select=Id,Version,Title,Summary,Description,Published,Dependencies,Tags,PackageTestResultStatus,ProjectUrl,ReleaseNotes,DownloadCount,GalleryDetailsUrl";
    const string Icon = "Images\\icon.png";

    // short idle timeout: don't hold sockets/buffers while Flow sits in the tray
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    }) { Timeout = TimeSpan.FromSeconds(15) };

    // re-read every time: the env var only reaches Flow after a restart, ProgramData is choco's default
    static string ChocoRoot => Environment.GetEnvironmentVariable("ChocolateyInstall") is { Length: > 0 } env
        ? env
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
    static string LibDir => Path.Combine(ChocoRoot, "lib");
    static string ChocoExe => Path.Combine(ChocoRoot, "choco.exe"); // real exe, not the bin\ shim
    static bool ChocoInstalled => File.Exists(ChocoExe);

    PluginInitContext ctx = null!;
    Settings settings = null!;
    string iconDir = "", logo = "";

    // ponytail: small caches capped by clearing, an LRU only if that ever shows up in a profile
    readonly ConcurrentDictionary<string, List<Pkg>> searchCache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Task<Details?>> details = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string?> status = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Task<string?>> icons = new();
    readonly object gate = new();
    List<Outdated>? outdated;
    Task<List<Outdated>>? outdatedRun;
    DateTime outdatedAt, outdatedLib;
    Task<List<Source>>? sources;
    (string Dir, DateTime Stamp, List<Pkg> List) installed = ("", DateTime.MinValue, []);

    string T(string key, params object?[] args)
    {
        var s = ctx.API.GetTranslation("flowlauncher_plugin_choco_" + key);
        return args.Length == 0 ? s : string.Format(s, args);
    }

    string Kw => (ctx.CurrentPluginMetadata.ActionKeywords?.FirstOrDefault() ?? ctx.CurrentPluginMetadata.ActionKeyword) is { Length: > 0 } k && k != "*" ? k + " " : "";

    public string GetTranslatedPluginTitle() => T("plugin_name");
    public string GetTranslatedPluginDescription() => T("plugin_description");

    public Task InitAsync(PluginInitContext context)
    {
        ctx = context;
        settings = ctx.API.LoadSettingJsonStorage<Settings>();
        ProgressWindow.T = T;
        var cache = ctx.CurrentPluginMetadata.PluginCacheDirectoryPath;
        iconDir = Path.Combine(cache, "icons");
        Directory.CreateDirectory(iconDir);
        logo = Path.Combine(ctx.CurrentPluginMetadata.PluginDirectory, Icon);
        ProgressWindow.LastRunLog = Path.Combine(cache, "last-run.log");
        if (ChocoInstalled)
        {
            _ = RefreshOutdated();
            _ = Task.Run(() => FetchStatuses(Installed().Select(p => p.Id)));
        }
        _ = Task.Run(BackgroundLoop);
        return Task.CompletedTask;
    }

    public Control CreateSettingPanel() =>
        new SettingsPanel(settings, k => T(k), ChocoInstalled ? Visible(Installed()) : [], () =>
        {
            ctx.API.SaveSettingJsonStorage<Settings>();
            sources = null;
            searchCache.Clear();
        });

    public async Task<List<Result>> QueryAsync(Query query, CancellationToken ct)
    {
        if (!ChocoInstalled) return NotInstalled();
        var s = query.Search.Trim();
        var sp = s.IndexOf(' ');
        var (cmd, rest) = sp < 0 ? (s.ToLowerInvariant(), "") : (s[..sp].ToLowerInvariant(), s[(sp + 1)..].Trim());
        try
        {
            return cmd switch
            {
                "" => Home(),
                "update" or "upgrade" or "outdated" => await UpdatesView(rest, ct),
                "uninstall" or "remove" or "list" => await InstalledView(rest, ct),
                "install" => await SearchView(rest, ct),
                "export" => ExportView(),
                "import" => await ImportView(rest, ct),
                _ => await SearchView(s, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return []; }
        catch (Exception e)
        {
            ctx.API.LogException(nameof(Main), "query failed", e);
            return [new Result { Title = T("error"), SubTitle = e.Message, IcoPath = Icon }];
        }
    }

    // Offer, never auto-install. Elevated winget: the UAC prompt is the confirmation.
    List<Result> NotInstalled() =>
    [
        new Result
        {
            Title = T("not_installed"), SubTitle = T("not_installed_sub"), IcoPath = Icon,
            Action = _ => RunJob("setup", "Chocolatey", "winget",
                "install --id Chocolatey.Chocolatey -e --accept-source-agreements --accept-package-agreements --disable-interactivity",
                () => { if (!ChocoInstalled) ctx.API.OpenUrl("https://chocolatey.org/install"); return Task.CompletedTask; }),
        }
    ];

    List<Result> Home()
    {
        Result Nav(string title, string sub, string to, int score) => new()
        {
            Title = title, SubTitle = sub, IcoPath = Icon, Score = score,
            Action = _ => { ctx.API.ChangeQuery(Kw + to); return false; }
        };
        var n = outdated?.Count(o => !Hidden(o.Id, outdated.Select(x => x.Id)));
        return
        [
            Nav(T("install"), T("install_sub"), "install ", 500),
            Nav(T("update"), n is null ? T("checking_updates") : T("updates_available", n), "update ", 400),
            Nav(T("uninstall"), T("installed_count", Visible(Installed()).Count()), "uninstall ", 300),
            Nav(T("export"), T("export_sub"), "export", 200),
            Nav(T("import"), T("import_sub"), "import ", 100),
        ];
    }

    // ---------- search / install ----------

    async Task<List<Result>> SearchView(string term, CancellationToken ct)
    {
        if (term.Length == 0)
            return [new Result { Title = T("type_name"), SubTitle = T("type_name_sub"), IcoPath = Icon }];

        if (!searchCache.TryGetValue(term, out var pkgs))
        {
            await Task.Delay(150, ct); // debounce: Flow cancels ct on the next keystroke
            pkgs = await SearchAll(term, ct);
            if (searchCache.Count >= 64) searchCache.Clear();
            searchCache[term] = pkgs;
        }

        var local = Installed().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var upd = (outdated ?? []).ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        var results = pkgs.Select((p, i) =>
        {
            local.TryGetValue(p.Id, out var have);
            var state = have is null ? "" : upd.ContainsKey(p.Id) ? " · " + T("installed_update", have.Version) : " · " + T("installed");
            var origin = p.Source is null ? $"{Badge(p.Id)} · {T("downloads", Parse.Count(p.Downloads))}" : " · " + T("source_label", p.Source);
            return Item(p, $"{p.Id} · v{p.Version}{origin}{state}", 1000 - i * 10, _ => have is null ? Install(p) : Run("upgrade", p.Id));
        }).ToList();
        await ApplyIcons(results, pkgs.Select(p => p.IconUrl), ct);
        return results;
    }

    // Community: fast OData API. Other configured sources (setting): `choco search`, which handles auth and folder feeds.
    async Task<List<Pkg>> SearchAll(string term, CancellationToken ct)
    {
        var srcs = settings.AllSources ? await Sources() : null;
        var jobs = new List<Task<List<Pkg>>>();
        if (srcs is null || srcs.Any(s => s.IsCommunity)) jobs.Add(SearchCommunity(term, ct));
        foreach (var s in srcs?.Where(s => !s.IsCommunity) ?? [])
            jobs.Add(SearchSource(s, term, ct));
        var all = await Task.WhenAll(jobs);
        return all.SelectMany(x => x).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    async Task<List<Pkg>> SearchCommunity(string term, CancellationToken ct)
    {
        var url = $"{Api}Search()?$filter=IsLatestVersion&searchTerm='{Uri.EscapeDataString(term.Replace("'", "''"))}'" +
                  $"&targetFramework=''&includePrerelease=false&$top=20&{Select}";
        var pkgs = Parse.Feed(await GetString(url, ct));
        foreach (var p in pkgs) status[p.Id] = p.Status;
        return pkgs;
    }

    async Task<List<Pkg>> SearchSource(Source s, string term, CancellationToken ct)
    {
        try
        {
            var stdout = await Cli.Run(ChocoExe, ct, "search", term, "--source=" + s.Name, "-r", "--page-size=20");
            return Cli.ParseIdVersion(stdout).Select(x => new Pkg(x.Id, x.Id, x.Version, null, null, 0, s.Name)).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ctx.API.LogException(nameof(Main), $"search on source {s.Name} failed", e);
            return [];
        }
    }

    Task<List<Source>> Sources() => sources ??= Task.Run(async () =>
        Cli.ParseSources(await Cli.Run(ChocoExe, CancellationToken.None, "source", "list", "-r")));

    static async Task<string> GetString(string url, CancellationToken ct)
    {
        try { return await Http.GetStringAsync(url, ct); }
        catch (HttpRequestException) { return await Http.GetStringAsync(url, ct); } // server dropped a pooled connection: retry once
    }

    Result Item(Pkg p, string sub, int score, Func<ActionContext, bool> action) => new()
    {
        Title = p.Title, SubTitle = sub, Score = score, ContextData = p, CopyText = p.Id, Action = action,
        PreviewPanel = new Lazy<UserControl>(() => new PreviewPanel(p, () => Details(p), ProgressWindow.T, u => ctx.API.OpenUrl(u))),
    };

    // Unapproved packages haven't been reviewed by a moderator: ask first (Yes preselected).
    bool Install(Pkg p)
    {
        if (p.Source is null && p.Status is not "Approved" && !Confirm(T("confirm_install_title", p.Title), T("confirm_install", p.Id, p.Status ?? "?")))
            return false;
        return Run("install", p.Id);
    }

    bool InstallMany(List<string> ids)
    {
        var unapproved = ids.Where(id => status.GetValueOrDefault(id) is not "Approved").ToList();
        if (unapproved.Count > 0 && !Confirm(T("import_missing", ids.Count), T("confirm_install_many", string.Join("\n", unapproved))))
            return false;
        return Run("install", ids.ToArray());
    }

    bool Confirm(string caption, string text) =>
        ctx.API.ShowMsgBox(text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;

    Task<Details?> Details(Pkg p)
    {
        if (p.Source is not null) return Task.FromResult<Details?>(null);
        if (details.Count >= 32) details.Clear();
        return details.GetOrAdd($"{p.Id}@{p.Version}", _ => Task.Run(async () =>
        {
            var url = p.Version != "latest" && Parse.SafeVersion(p.Version) // import entries without a version use "latest"
                ? $"{Api}Packages(Id='{p.Id}',Version='{p.Version}')?{DetailSelect}"
                : $"{Api}FindPackagesById()?id='{p.Id}'&$filter=IsLatestVersion&{DetailSelect}";
            var xml = await GetString(url, CancellationToken.None);
            var root = XDocument.Parse(xml).Root!;
            var entry = root.Name.LocalName == "feed" ? root.Elements().FirstOrDefault(e => e.Name.LocalName == "entry") : root;
            return entry is null ? null : DetailsParser.Parse(entry.ToString());
        }));
    }

    // ---------- export / import ----------

    // Flow hides itself when it loses focus, which buries a dialog it owns: hide Flow first,
    // then show the dialog from its own invisible topmost owner so it lands in front.
    static void Pick(Microsoft.Win32.FileDialog dlg, Action<string> picked) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var owner = new Window
            {
                Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true,
                AllowsTransparency = true, Opacity = 0, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            owner.Show();
            owner.Activate();
            try { if (dlg.ShowDialog(owner) == true) picked(dlg.FileName); }
            finally { owner.Close(); }
        });

    List<Result> ExportView() =>
    [
        new Result
        {
            Title = T("export"), SubTitle = T("export_sub"), IcoPath = Icon,
            Action = _ =>
            {
                ctx.API.HideMainWindow();
                Pick(new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "packages.config", Filter = "packages.config|*.config|XML|*.xml",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                }, path => Task.Run(async () =>
                {
                    try
                    {
                        await Cli.Run(ChocoExe, CancellationToken.None, "export", "--output-file-path=" + path);
                        ctx.API.ShowMsgWithButton(T("plugin_name"), T("export_done", Cli.ParsePackagesConfig(path).Count, path),
                            () => ctx.API.OpenDirectory(Path.GetDirectoryName(path)!, path), T("open_folder"), logo);
                    }
                    catch (Exception e) { ctx.API.ShowMsgError(T("plugin_name"), e.Message); }
                }));
                return false;
            }
        }
    ];

    async Task<List<Result>> ImportView(string path, CancellationToken ct)
    {
        if (path.Length == 0)
            return [new Result
            {
                Title = T("import_pick"), SubTitle = T("import_sub"), IcoPath = Icon,
                Action = _ =>
                {
                    ctx.API.HideMainWindow();
                    Pick(new Microsoft.Win32.OpenFileDialog { Filter = "packages.config|*.config;*.xml|*.*|*.*" }, file =>
                    {
                        ctx.API.ShowMainWindow();
                        ctx.API.ChangeQuery($"{Kw}import {file}", true);
                    });
                    return false;
                }
            }];

        List<(string Id, string? Version)> entries;
        try { entries = Cli.ParsePackagesConfig(path.Trim('"')).Where(e => Parse.SafeId(e.Id)).ToList(); }
        catch (Exception e) { return [new Result { Title = T("import_bad_file", path), SubTitle = e.Message, IcoPath = Icon }]; }

        var local = Installed().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = entries.Where(e => !local.Contains(e.Id)).Select(e => e.Id).ToList();
        await FetchStatuses(missing).WaitAsync(ct);

        var results = entries.Select((e, i) =>
        {
            var have = local.Contains(e.Id);
            var p = new Pkg(e.Id, e.Id, e.Version ?? "latest", null, status.GetValueOrDefault(e.Id), 0);
            return Item(p, $"{e.Id}{Badge(e.Id)} · {(have ? T("installed") : T("import_enter"))}", 1000 - i, _ => !have && Install(p));
        }).ToList();
        foreach (var r in results) r.IcoPath = Icon;
        results.Insert(0, missing.Count == 0
            ? new Result { Title = T("import_none"), IcoPath = Icon, Score = 2000 }
            : new Result { Title = T("import_missing", missing.Count), SubTitle = string.Join(", ", missing), IcoPath = Icon, Score = 2000, Action = _ => InstallMany(missing) });
        return results;
    }

    // ---------- updates ----------

    async Task<List<Result>> UpdatesView(string filter, CancellationToken ct)
    {
        // packages changed outside the plugin (choco CLI, another tool): the cached list is wrong, wait for a fresh one
        var list = outdated is null || LibStamp() != outdatedLib ? await RefreshOutdated().WaitAsync(ct) : outdated;
        if (DateTime.UtcNow - outdatedAt > TimeSpan.FromMinutes(10)) _ = RefreshOutdated(); // stale-while-revalidate

        var local = Installed().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var ids = list.Select(o => o.Id).ToList();
        var shown = list.Where(o => !Hidden(o.Id, ids) && Match(filter, o.Id, local.GetValueOrDefault(o.Id)?.Title)).ToList();

        if (shown.Count == 0)
            return [new Result
            {
                Title = filter.Length == 0 ? T("up_to_date") : T("no_matching_updates"),
                SubTitle = T("checked_at", outdatedAt.ToLocalTime().ToString("t")), IcoPath = Icon,
                Action = _ => { outdatedAt = DateTime.MinValue; RefreshOutdated().ContinueWith(_ => ctx.API.ReQuery()); return false; }
            }];

        var results = shown.Select((o, i) =>
        {
            var pkg = local.GetValueOrDefault(o.Id) ?? new Pkg(o.Id, o.Id, o.Current, null, null, 0);
            return Item(pkg with { Version = o.Available }, $"{o.Id} · v{o.Current} → v{o.Available}{Badge(o.Id)}", 1000 - i * 10, _ => Run("upgrade", o.Id));
        }).ToList();
        await ApplyIcons(results, shown.Select(o => local.GetValueOrDefault(o.Id)?.IconUrl), ct);

        if (filter.Length == 0 && shown.Count > 1)
            results.Insert(0, new Result
            {
                Title = T("upgrade_all", shown.Count), SubTitle = "choco upgrade all -y", IcoPath = Icon, Score = 2000,
                Action = _ => Run("upgrade", "all"),
            });
        return results;
    }

    Task<List<Outdated>> RefreshOutdated()
    {
        lock (gate)
        {
            if (outdatedRun is { IsCompleted: false }) return outdatedRun;
            outdatedAt = DateTime.UtcNow;
            outdatedLib = LibStamp();
            return outdatedRun = Task.Run(async () =>
                outdated = Parse.OutdatedOutput(await Cli.Run(ChocoExe, CancellationToken.None, "outdated", "-r", "--ignore-pinned", "--ignore-unfound")));
        }
    }

    // Background check (off by default): every few minutes see if the interval has passed, then
    // notify about new updates or upgrade them, limited by the include/exclude list.
    async Task BackgroundLoop()
    {
        await Task.Delay(TimeSpan.FromMinutes(2)); // don't compete with startup
        while (true)
        {
            try { await BackgroundCheck(); }
            catch (Exception e) { ctx.API.LogException(nameof(Main), "background check failed", e); }
            await Task.Delay(TimeSpan.FromMinutes(5));
        }
    }

    async Task BackgroundCheck()
    {
        var s = settings;
        if (s.CheckHours <= 0 || !ChocoInstalled || ProgressWindow.Busy || !NetworkInterface.GetIsNetworkAvailable()) return;
        if (DateTime.UtcNow - s.LastCheck < TimeSpan.FromHours(s.CheckHours)) return;
        s.LastCheck = DateTime.UtcNow;

        outdatedAt = DateTime.MinValue;
        var list = await RefreshOutdated();
        var ids = list.Select(o => o.Id).ToList();
        var due = list.Where(o => !Hidden(o.Id, ids) && s.Selected(o.Id)).ToList();
        if (due.Count > 0 && s.AutoUpdate)
            Application.Current.Dispatcher.Invoke(() => Run("upgrade", due.Select(o => o.Id).ToArray()));
        else if (due.Count > 0 && s.Notify)
        {
            var keys = due.Select(o => $"{o.Id}@{o.Available}").ToList();
            if (!keys.All(s.LastNotified.Contains)) // only announce something new
            {
                s.LastNotified = keys;
                ctx.API.ShowMsgWithButton(T("notify_title", due.Count), string.Join(", ", due.Select(o => o.Id)),
                    () => { ctx.API.ShowMainWindow(); ctx.API.ChangeQuery(Kw + "update ", true); }, T("notify_button"), logo);
            }
        }
        ctx.API.SaveSettingJsonStorage<Settings>();
    }

    // ---------- installed / uninstall ----------

    async Task<List<Result>> InstalledView(string filter, CancellationToken ct)
    {
        var shown = Visible(Installed()).Where(p => Match(filter, p.Id, p.Title)).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var results = shown.Select((p, i) => Item(p, $"{p.Id} · v{p.Version}{Badge(p.Id)} · {T("enter_uninstall")}", 1000 - i, _ => Uninstall(p))).ToList();
        await ApplyIcons(results, shown.Select(p => p.IconUrl), ct);
        return results;
    }

    // lib's write time changes whenever a package folder is added/removed/replaced
    static DateTime LibStamp() => Directory.Exists(LibDir) ? Directory.GetLastWriteTimeUtc(LibDir) : default;

    List<Pkg> Installed()
    {
        var lib = LibDir;
        if (!Directory.Exists(lib)) return [];
        var stamp = LibStamp();
        var cache = installed;
        if (cache.Dir == lib && cache.Stamp == stamp) return cache.List;

        var list = new List<Pkg>();
        foreach (var dir in Directory.EnumerateDirectories(lib))
        {
            var nuspec = Path.Combine(dir, Path.GetFileName(dir) + ".nuspec");
            if (!File.Exists(nuspec)) continue;
            try
            {
                var md = XDocument.Load(nuspec).Descendants().First(e => e.Name.LocalName == "metadata");
                string? M(string n) => md.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value is { Length: > 0 } v ? v : null;
                var id = M("id") ?? Path.GetFileName(dir);
                list.Add(new Pkg(id, M("title") ?? id, M("version") ?? "?", M("iconUrl"), null, 0));
            }
            catch (Exception e) { ctx.API.LogException(nameof(Main), $"bad nuspec {nuspec}", e); }
        }
        installed = (lib, stamp, list);
        return list;
    }

    static IEnumerable<Pkg> Visible(List<Pkg> all) => all.Where(p => !Hidden(p.Id, all.Select(x => x.Id)));

    static bool Hidden(string id, IEnumerable<string> ids) =>
        Parse.MetaBase(id) is { } b && ids.Contains(b, StringComparer.OrdinalIgnoreCase);

    bool Uninstall(Pkg p)
    {
        if (!Confirm(T("confirm_uninstall_title", p.Title), T("confirm_uninstall", p.Title, p.Version))) return false;
        var ids = new List<string> { p.Id };
        foreach (var suffix in new[] { ".install", ".portable" })
            if (Installed().Any(x => x.Id.Equals(p.Id + suffix, StringComparison.OrdinalIgnoreCase))) ids.Add(p.Id + suffix);
        return Run("uninstall", ids.ToArray());
    }

    // ---------- shared ----------

    bool Match(string filter, string id, string? title) =>
        filter.Length == 0 || ctx.API.FuzzySearch(filter, $"{title} {id}").IsSearchPrecisionScoreMet();

    string Badge(string id) => status.TryGetValue(id, out var s) && s is { Length: > 0 }
        ? s == "Approved" ? " · ✓ " + T("approved") : $" · {s}"
        : "";

    async Task FetchStatuses(IEnumerable<string> ids)
    {
        using var sem = new SemaphoreSlim(6);
        await Task.WhenAll(ids.Where(id => !status.ContainsKey(id)).Select(async id =>
        {
            await sem.WaitAsync();
            try
            {
                var xml = await GetString($"{Api}FindPackagesById()?id='{Uri.EscapeDataString(id)}'&$filter=IsLatestVersion&{Select}", CancellationToken.None);
                status[id] = Parse.Feed(xml).FirstOrDefault()?.Status;
            }
            catch { /* offline or unlisted: no badge */ }
            finally { sem.Release(); }
        }));
    }

    // Icons live on disk; memory only holds the path. Wait briefly so most render on first paint.
    async Task ApplyIcons(List<Result> results, IEnumerable<string?> urls, CancellationToken ct)
    {
        var tasks = urls.Select(u => string.IsNullOrEmpty(u) ? Task.FromResult<string?>(null) : icons.GetOrAdd(u, DownloadIcon)).ToList();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(400, ct));
        for (var i = 0; i < results.Count; i++)
            results[i].IcoPath = tasks[i].IsCompletedSuccessfully && tasks[i].Result is { } path ? path : Icon;
    }

    async Task<string?> DownloadIcon(string url)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        var hit = Directory.EnumerateFiles(iconDir, key + ".*").FirstOrDefault();
        if (hit != null) return hit;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (Parse.SniffExt(bytes) is not { } ext) return null;
            var path = Path.Combine(iconDir, key + ext);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch { return null; }
    }

    bool Run(string verb, params string[] ids)
    {
        if (!ids.All(Parse.SafeId)) // ids end up on an elevated command line
        {
            ctx.API.ShowMsgError(T("plugin_name"), T("unsafe_id"));
            return true;
        }
        var what = ids is ["all"] ? T("all_packages") : string.Join(", ", ids);
        return RunJob(verb, what, ChocoExe, $"{verb} {string.Join(' ', ids)} -y", async () =>
        {
            searchCache.Clear();
            outdatedAt = DateTime.MinValue;
            await RefreshOutdated();
        });
    }

    bool RunJob(string verb, string what, string exe, string args, Func<Task> after)
    {
        Application.Current.Dispatcher.Invoke(() => new ProgressWindow(exe, args, verb, what, logo, after).Show());
        return true;
    }

    public List<Result> LoadContextMenus(Result selected)
    {
        if (selected.ContextData is not Pkg p) return [];
        var isInstalled = Installed().Any(x => x.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
        Result M(string title, string sub, Func<ActionContext, bool> action) => new() { Title = title, SubTitle = sub, IcoPath = Icon, Action = action };
        var menu = new List<Result>
        {
            isInstalled
                ? M(T("ctx_upgrade"), $"choco upgrade {p.Id}", _ => Run("upgrade", p.Id))
                : M(T("ctx_install"), $"choco install {p.Id}", _ => Install(p)),
        };
        if (isInstalled) menu.Add(M(T("ctx_uninstall"), $"choco uninstall {p.Id}", _ => Uninstall(p)));
        if (p.Source is null)
            menu.Add(M(T("ctx_open_page"), $"community.chocolatey.org/packages/{p.Id}", _ => { ctx.API.OpenUrl($"https://community.chocolatey.org/packages/{p.Id}"); return true; }));
        return menu;
    }
}
