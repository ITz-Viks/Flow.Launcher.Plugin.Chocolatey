using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Xml.Linq;

namespace Flow.Launcher.Plugin.Choco;

public sealed record Details(string Title, string Version, string? Authors, string? Summary, string? Description, DateTime? Published,
    long Downloads, string? Dependencies, string? Tags, string? Tests, string? ProjectUrl, string? ReleaseNotes, string? Gallery);

public static class DetailsParser
{
    public static Details Parse(string xml)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom", d = "http://schemas.microsoft.com/ado/2007/08/dataservices",
            m = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
        var e = XDocument.Parse(xml).Root!;
        var p = e.Element(m + "properties")!;
        string? P(string n) => (string?)p.Element(d + n) is { Length: > 0 } v ? v.Trim() : null;
        return new Details(P("Title") ?? (string)e.Element(atom + "title")!, P("Version") ?? "?",
            (string?)e.Element(atom + "author")?.Element(atom + "name"), (string?)e.Element(atom + "summary"), P("Description"),
            DateTime.TryParse(P("Published"), out var pub) ? pub : null, long.TryParse(P("DownloadCount"), out var dl) ? dl : 0,
            P("Dependencies"), P("Tags"), P("PackageTestResultStatus"), P("ProjectUrl"), P("ReleaseNotes"), P("GalleryDetailsUrl"));
    }

    /// <summary>"a:1.0:|b:[2.0, ):" → "a 1.0, b [2.0, )"</summary>
    public static string Deps(string raw) => string.Join(", ", raw.Split('|', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split(':')).Select(f => f.Length > 1 && f[1].Length > 0 ? $"{f[0]} {f[1]}" : f[0]));
}

// Shown in Flow's preview pane (F1). Built only when the pane opens; fetches one package's details.
sealed class PreviewPanel : UserControl
{
    readonly StackPanel root = new() { Margin = new Thickness(8, 4, 8, 8) };
    readonly Func<string, object?[], string> t;

    public PreviewPanel(Pkg pkg, Func<Task<Details?>> fetch, Func<string, object?[], string> t, Action<string> open)
    {
        this.t = t;
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Line(pkg.Title, title: true);
        Line($"{pkg.Id} · v{pkg.Version}");
        var loading = Line(T("preview_loading"));

        Loaded += async (_, _) =>
        {
            Details? d = null;
            try { d = await fetch(); } catch { /* offline: say so below */ }
            root.Children.Remove(loading);
            if (d is null) { Line(T(pkg.Source is null ? "preview_failed" : "preview_unavailable")); return; }

            if (d.Authors is { } a) Line(a);
            var desc = d.Description ?? "";
            // many packages repeat the summary as the description's first line
            Text(d.Summary is { Length: > 0 } s && !desc.StartsWith(s.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ? s + "\n\n" + desc : desc);
            Fact("preview_published", d.Published?.ToLocalTime().ToString("d"));
            Fact("preview_downloads", d.Downloads > 0 ? Parse.Count(d.Downloads) : null);
            Fact("preview_dependencies", d.Dependencies is { } deps ? DetailsParser.Deps(deps) : T("preview_none"));
            Fact("preview_tests", d.Tests);
            Fact("preview_tags", d.Tags);
            if (d.ProjectUrl is { } u) Link(T("preview_project"), u, open);
            if (d.ReleaseNotes is { } rn)
            {
                if (Uri.IsWellFormedUriString(rn, UriKind.Absolute)) Link(T("preview_release_notes"), rn, open);
                else { Line(T("preview_release_notes"), title: true); Text(rn); }
            }
            if (d.Gallery is { } g) Link(T("ctx_open_page"), g, open);
        };
    }

    string T(string key) => t(key, []);

    TextBlock Line(string text, bool title = false)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, title ? 8 : 2, 0, 2) };
        tb.SetResourceReference(StyleProperty, title ? "PreviewItemTitleStyle" : "PreviewItemSubTitleStyle"); // follow Flow's theme
        root.Children.Add(tb);
        return tb;
    }

    void Text(string text)
    {
        if (text.Length == 0) return;
        // ponytail: markdown shown as plain text minus heading/emphasis marks; render it if anyone asks
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^#{1,6}\s*|\*\*|__", "");
        Line(text.Length > 4000 ? text[..4000] + "…" : text).Margin = new Thickness(0, 8, 0, 8);
    }

    void Fact(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Line($"{T(key)}: {value}");
    }

    void Link(string text, string url, Action<string> open)
    {
        var tb = Line("");
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, e) => { open(e.Uri.AbsoluteUri); e.Handled = true; };
        tb.Inlines.Add(link);
    }
}
