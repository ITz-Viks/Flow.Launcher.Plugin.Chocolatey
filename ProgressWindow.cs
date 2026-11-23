using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Flow.Launcher.Plugin.Choco;

// Toast-style progress card. choco runs elevated + hidden, writing to a temp log we tail.
sealed class ProgressWindow : Window
{
    static readonly SemaphoreSlim Queue = new(1, 1); // choco holds a global lock: one task at a time
    static readonly List<ProgressWindow> Open = [];
    static bool terminalOn;                          // toggle remembered for the session
    public static string? LastRunLog;
    public static Func<string, object?[], string> T = (k, _) => k; // set by Main to Flow's translator
    static string L(string key, params object?[] a) => T(key, a);
    const int MaxLog = 200_000;                      // chars kept for the terminal view

    static readonly Brush Card = B("#F21C1D21"), Edge = B("#22FFFFFF"), Fg = B("#FFEDEDED"), Dim = B("#FF9BA1A8"),
        Accent = B("#FF80B5E3"), Ok = B("#FF56C26A"), Bad = B("#FFE5534B"), Track = B("#22FFFFFF"), TermBg = B("#FF0D0D0F"),
        Hover = B("#18FFFFFF"), Warn = B("#FFF2B33D");

    readonly string exe, args, verb, what;
    readonly Func<Task> onDone;
    readonly TextBlock title = new(), stage = new(), pct = new();
    readonly ProgressBar bar = new();
    readonly TextBox term = new();
    readonly ToggleButton toggle;
    readonly Button cancel;
    readonly string stopFile = Path.Combine(Path.GetTempPath(), $"flow-choco-{Guid.NewGuid():N}.stop");
    bool cancelled;
    readonly StringBuilder log = new();
    readonly DispatcherTimer autoClose = new() { Interval = TimeSpan.FromSeconds(6) };
    string partial = "";
    string? firstError;
    bool lastWasProgress, finished, checksumFailed;
    readonly string logoPath;
    // amber strip shown when the only way forward is skipping the checksum check
    readonly Border warning = new()
    {
        Background = B("#26F2B33D"), BorderBrush = B("#80F2B33D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8, 10, 6), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed,
    };
    double anchorBottom;

    public static bool Busy => Queue.CurrentCount == 0;

    /// <param name="verb">install|upgrade|uninstall|setup: picks the title strings</param>
    public ProgressWindow(string exe, string args, string verb, string what, string logoPath, Func<Task> onDone)
    {
        this.exe = exe;
        this.args = args;
        this.onDone = onDone;
        this.verb = verb;
        this.what = what;
        this.logoPath = logoPath;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var logo = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(logoPath);
            bmp.DecodePixelWidth = 80; // don't keep a full-size bitmap around
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            logo.Source = bmp;
        }
        catch { /* no logo is fine */ }

        title.Text = L($"title_{verb}", what);
        title.Foreground = Fg; title.FontSize = 14; title.FontWeight = FontWeights.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        stage.Foreground = Dim; stage.FontSize = 12; stage.Margin = new Thickness(0, 2, 0, 8);
        stage.TextTrimming = TextTrimming.CharacterEllipsis;
        pct.Foreground = Dim; pct.FontSize = 12; pct.Width = 38; pct.TextAlignment = TextAlignment.Right;
        pct.VerticalAlignment = VerticalAlignment.Center;
        bar.Height = 6; bar.Foreground = Accent; bar.Background = Track; bar.BorderThickness = new Thickness(0);
        bar.VerticalAlignment = VerticalAlignment.Center;

        var close = FlatButton<Button>("✕", 12);
        close.Click += (_, _) => Close();
        toggle = FlatButton<ToggleButton>(">_  " + L("terminal"), 11.5);
        toggle.Margin = new Thickness(-6, 6, 0, 0);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.IsChecked = terminalOn;
        toggle.Click += (_, _) => { terminalOn = toggle.IsChecked == true; ShowTerminal(terminalOn); };
        cancel = FlatButton<Button>(L("cancel"), 11.5);
        cancel.Margin = new Thickness(0, 6, -6, 0);
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Click += (_, _) =>
        {
            cancelled = true;
            cancel.IsEnabled = false;
            SetStage(L("stage_cancelling"), -1);
            try { File.WriteAllText(stopFile, ""); } catch { }
        };

        term.IsReadOnly = true;
        term.Background = TermBg; term.Foreground = B("#FFCCCCCC"); term.BorderThickness = new Thickness(0);
        term.FontFamily = new FontFamily("Cascadia Mono, Consolas"); term.FontSize = 11.5;
        term.Height = 240; term.Margin = new Thickness(0, 8, 0, 0); term.Padding = new Thickness(8);
        term.TextWrapping = TextWrapping.Wrap;
        term.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        term.Visibility = Visibility.Collapsed;

        var head = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(title);

        var barRow = new DockPanel();
        DockPanel.SetDock(pct, Dock.Right);
        barRow.Children.Add(pct);
        barRow.Children.Add(bar);

        var right = new StackPanel();
        right.Children.Add(head);
        right.Children.Add(stage);
        right.Children.Add(barRow);
        var buttons = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(cancel);
        buttons.Children.Add(toggle);
        right.Children.Add(buttons);

        var top = new DockPanel();
        DockPanel.SetDock(logo, Dock.Left);
        top.Children.Add(logo);
        top.Children.Add(right);

        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(warning);
        body.Children.Add(term);

        Content = new Border
        {
            Background = Card, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 12, 14), Margin = new Thickness(12), Child = body,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 },
        };

        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) { DragMove(); anchorBottom = Top + ActualHeight; } };
        SizeChanged += (_, _) => Top = anchorBottom - ActualHeight; // grow upwards when the terminal opens
        autoClose.Tick += (_, _) => { if (!IsMouseOver && toggle.IsChecked != true) Close(); };
        Closed += (_, _) => { autoClose.Stop(); Open.Remove(this); };
        Loaded += async (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            anchorBottom = wa.Bottom - Open.Count * 118; // stack above cards that are already open
            Open.Add(this);
            Left = wa.Right - ActualWidth;
            Top = anchorBottom - ActualHeight;
            if (terminalOn) ShowTerminal(true);
            await RunAsync();
        };
    }

    async Task RunAsync()
    {
        SetStage(L("stage_queued"), -1);
        await Queue.WaitAsync();
        var logFile = Path.Combine(Path.GetTempPath(), $"flow-choco-{Guid.NewGuid():N}.log");
        try
        {
            if (cancelled) { Finish(false, L("cancelled")); return; }
            File.WriteAllText(logFile, "");
            SetStage(L("stage_uac"), -1);
            Process? p;
            try
            {
                var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var script = Encoding.Unicode.GetBytes(Cli.Wrapper(exe, args, logFile, stopFile));
                p = Process.Start(new ProcessStartInfo(ps, "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(script))
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Win32Exception)
            {
                Finish(false, L("uac_declined"));
                return;
            }
            SetStage(L("stage_starting"), -1);

            using (p)
            using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                (string, int, int)? summary = null;
                while (true)
                {
                    var exited = p?.HasExited ?? true;
                    var chunk = await reader.ReadToEndAsync();
                    if (chunk.Length > 0) summary = Consume(chunk) ?? summary;
                    if (exited) break;
                    await Task.Delay(150);
                }
                if (partial.Length > 0) summary = Consume("\n") ?? summary;

                int? exit = null;
                try { exit = p?.ExitCode; } catch { /* handle may not grant query rights */ }
                if (cancelled || exit == 1223) { Finish(false, L("cancelled")); return; }
                var ok = exit is 0 or 1641 or 3010 || (exit is null && summary is { } s && s.Item2 == s.Item3);
                // "upgrade all" counts every installed package as the total ("2/17"): on success only the done count matters
                var detail = summary is not { } sm ? "" : !ok ? L("summary", sm.Item2, sm.Item3) : sm.Item2 > 0 ? L("summary", sm.Item2, sm.Item2) : "";
                if (exit is 1641 or 3010) detail += " · " + L("reboot_required");
                Finish(ok, ok ? detail : firstError ?? (exit is { } e ? L("failed_exit", e) : L("failed")));
            }
        }
        catch (Exception ex) { Finish(false, ex.Message); }
        finally
        {
            Queue.Release();
            try
            {
                if (LastRunLog != null) File.Copy(logFile, LastRunLog, true); // keep only the last run, for debugging
                File.Delete(logFile);
                File.Delete(stopFile);
            }
            catch { }
            _ = onDone();
        }
    }



    // Splits on \r and \n (choco redraws progress with \r), updates stage, appends to terminal buffer.
    (string, int, int)? Consume(string chunk)
    {
        (string, int, int)? summary = null;
        var text = partial + chunk;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            var line = text[start..i];
            start = i + 1;
            if (line.Length == 0) continue;

            var isProgress = line.StartsWith("Progress:", StringComparison.Ordinal);
            if (isProgress && lastWasProgress) // overwrite the previous progress line like a real console
            {
                var cut = log.Length - 1;
                while (cut > 0 && log[cut - 1] != '\n') cut--;
                log.Length = Math.Max(cut, 0);
            }
            log.Append(line).Append('\n');
            lastWasProgress = isProgress;

            if (StageParser.Of(line) is { } st) SetStage(L(st.Key, st.Args), st.Percent);
            if (firstError == null && line.StartsWith("ERROR:", StringComparison.Ordinal)) firstError = line[6..].Trim();
            if (line.Contains("--ignore-checksums") || line.Contains("hashes do not match", StringComparison.OrdinalIgnoreCase)) checksumFailed = true;
            if (StageParser.Summary(line) is { } sm) summary = sm;
        }
        partial = text[start..];
        if (log.Length > MaxLog) log.Remove(0, log.Length - MaxLog);
        if (term.Visibility == Visibility.Visible) RefreshTerminal();
        return summary;
    }

    void SetStage(string text, int percent)
    {
        stage.Text = text;
        bar.IsIndeterminate = percent < 0;
        if (percent >= 0) bar.Value = percent;
        pct.Text = percent >= 0 ? $"{percent}%" : "";
    }

    void Finish(bool ok, string detail)
    {
        finished = true;
        cancel.Visibility = Visibility.Collapsed;
        title.Text = (ok ? "✓  " : "✕  ") + L(ok ? $"done_{verb}" : $"failed_{verb}", what);
        bar.IsIndeterminate = false;
        bar.Value = 100;
        bar.Foreground = ok ? Ok : Bad;
        pct.Text = "";
        stage.Text = detail;
        if (ok) autoClose.Start();
        else if (CanRetryWithoutChecksums()) ShowChecksumRetry();
        else if (toggle.IsChecked != true) { toggle.IsChecked = true; ShowTerminal(true); }
    }

    // Only for a checksum failure of named packages (never "upgrade all"), and never twice.
    bool CanRetryWithoutChecksums() =>
        checksumFailed && verb is "install" or "upgrade" && !args.StartsWith(verb + " all ") && !args.Contains("--ignore-checksums");

    void ShowChecksumRetry()
    {
        var retry = FlatButton<Button>("⟳  " + L("retry_ignore_checksums"), 12);
        retry.Foreground = Warn;
        retry.HorizontalAlignment = HorizontalAlignment.Left;
        retry.Margin = new Thickness(-6, 6, 0, 0);
        retry.Click += (_, _) =>
        {
            new ProgressWindow(exe, args.Replace(" -y", " --ignore-checksums -y"), verb, what, logoPath, onDone).Show();
            Close();
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "⚠  " + L("checksum_warning"), Foreground = Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(retry);
        warning.Child = text;
        warning.Visibility = Visibility.Visible;
    }

    void ShowTerminal(bool on)
    {
        term.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) RefreshTerminal();
        else term.Clear(); // drop the TextBox copy; the StringBuilder keeps the log
        if (!on && finished) autoClose.Start();
    }

    void RefreshTerminal()
    {
        var atEnd = term.VerticalOffset + term.ViewportHeight >= term.ExtentHeight - 4;
        term.Text = log.ToString();
        if (atEnd) term.ScrollToEnd();
    }

    static T FlatButton<T>(string text, double size) where T : ButtonBase, new()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);

        var btn = new T
        {
            Content = text, FontSize = size, Foreground = Dim, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top, Template = new ControlTemplate(typeof(T)) { VisualTree = border },
        };
        btn.MouseEnter += (_, _) => { btn.Background = Hover; btn.Foreground = Fg; };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = Brushes.Transparent;
            btn.Foreground = btn is ToggleButton { IsChecked: true } ? Accent : Dim;
        };
        if (btn is ToggleButton tb)
        {
            tb.Checked += (_, _) => tb.Foreground = Accent;
            tb.Unchecked += (_, _) => tb.Foreground = tb.IsMouseOver ? Fg : Dim;
        }
        return btn;
    }

    static Brush B(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
