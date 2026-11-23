using System.Windows;
using System.Windows.Controls;

namespace Flow.Launcher.Plugin.Choco;

// Persisted by Flow (LoadSettingJsonStorage) as Settings.json in the plugin settings folder.
public class Settings
{
    public bool AllSources { get; set; }       // false = community repository only
    public int CheckHours { get; set; }        // 0 = background check off (default)
    public bool Notify { get; set; } = true;
    public bool AutoUpdate { get; set; }
    public bool IncludeMode { get; set; }      // false: check all except Packages, true: only Packages
    public List<string> Packages { get; set; } = [];
    public DateTime LastCheck { get; set; }
    public List<string> LastNotified { get; set; } = []; // "id@version" already announced

    public bool Selected(string id) => IncludeMode == Packages.Contains(id, StringComparer.OrdinalIgnoreCase);
}

sealed class SettingsPanel : UserControl
{
    static readonly int[] Hours = [0, 1, 6, 24];

    public SettingsPanel(Settings s, Func<string, string> t, IEnumerable<Pkg> installed, Action changed)
    {
        var root = new StackPanel { Margin = new Thickness(70, 14, 18, 14) };

        TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };
        ComboBox Combo(int selected, Action<int> onChange, params string[] items)
        {
            var c = new ComboBox { ItemsSource = items, SelectedIndex = selected, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            c.SelectionChanged += (_, _) => { onChange(c.SelectedIndex); changed(); };
            return c;
        }
        CheckBox Check(string text, bool value, Action<bool> onChange)
        {
            var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
            c.Click += (_, _) => { onChange(c.IsChecked == true); changed(); };
            return c;
        }

        root.Children.Add(Header(t("set_sources")));
        root.Children.Add(Combo(s.AllSources ? 1 : 0, i => s.AllSources = i == 1, t("set_sources_community"), t("set_sources_all")));

        root.Children.Add(Header(t("set_check")));
        root.Children.Add(Combo(Math.Max(0, Array.IndexOf(Hours, s.CheckHours)), i => s.CheckHours = Hours[i],
            t("set_check_off"), t("set_check_1"), t("set_check_6"), t("set_check_24")));
        root.Children.Add(Check(t("set_notify"), s.Notify, v => s.Notify = v));
        root.Children.Add(Check(t("set_auto"), s.AutoUpdate, v => s.AutoUpdate = v));

        root.Children.Add(Header(t("set_filter")));
        root.Children.Add(Combo(s.IncludeMode ? 1 : 0, i => s.IncludeMode = i == 1, t("set_filter_exclude"), t("set_filter_include")));

        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var p in installed.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase))
            list.Children.Add(Check($"{p.Title}  ({p.Id})", s.Packages.Contains(p.Id, StringComparer.OrdinalIgnoreCase), on =>
            {
                s.Packages.RemoveAll(x => x.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
                if (on) s.Packages.Add(p.Id);
            }));
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Content = root;
    }
}
