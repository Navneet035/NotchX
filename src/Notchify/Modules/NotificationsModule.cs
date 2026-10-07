using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notchify.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>One notification from another app, as read from Windows' notification centre.</summary>
public sealed class MirroredNotification
{
    public uint Id { get; init; }
    public string App { get; init; } = "";
    /// <summary>AppUserModelID, used to open the app (shell:AppsFolder\…).</summary>
    public string AppId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    /// <summary>For web notifications, the site that sent it ("web.whatsapp.com"); browsers show it as the attribution line.</summary>
    public string? Site { get; init; }
    public DateTimeOffset Time { get; init; }
    public ImageSource? Icon { get; init; }
}

public enum NotificationAccess { Off, NotPackaged, Denied, Error, Ready }

/// <summary>
/// Notification mirroring: other apps' notifications slide out of the notch, an unread count rides on the pill,
/// and the tab lists what's in Windows' notification centre. Reads with UserNotificationListener, which Windows
/// only allows for packaged apps (the Microsoft Store build). Nothing is stored or sent anywhere.
/// </summary>
public sealed class NotificationsModule : NotchModule
{
    private const string StoreLink = "ms-windows-store://pdp/?productid=9N2LWTDSX986";
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly HashSet<uint> _unread = new();
    private static readonly Dictionary<string, ImageSource?> IconCache = new();
    private UserNotificationListener? _listener;
    private bool _polling;
    // The first read only learns what's already there, so starting NotchX doesn't replay old notifications.
    private bool _primed;

    public NotificationsModule() => _poll.Tick += async (_, _) => await PollAsync();

    public override string Id => "notifications";
    public override string Title => "Notifications";
    public override string ShortTitle => "Alerts";
    public override string Glyph => Glyphs.Bell;
    public override string Description => "Other apps' notifications slide out of the notch, with an unread count on the pill (Microsoft Store version).";
    // Only the packaged build can read notifications; don't add a tab that can't work.
    public override bool DefaultEnabled => PackageInfo.IsPackaged;

    public ObservableCollection<MirroredNotification> Items { get; } = new();
    public NotificationAccess Access { get; private set; } = NotificationAccess.Off;
    public string? Error { get; private set; }

    /// <summary>Raised on the UI thread when the list or access changes.</summary>
    public event Action? Changed;

    protected override void Start() => _ = ConnectAsync(ask: true);

    protected override void Stop()
    {
        _poll.Stop();
        _listener = null;
        Items.Clear();
        _unread.Clear();
        Access = NotificationAccess.Off;
        Notch.Hub.Remove(Id);
        Changed?.Invoke();
    }

    /// <summary>Ask Windows for access (once — after that it's in Settings › Privacy › Notifications) and start reading.</summary>
    public async Task ConnectAsync(bool ask)
    {
        if (!PackageInfo.IsPackaged) { SetAccess(NotificationAccess.NotPackaged); return; }
        try
        {
            _listener = UserNotificationListener.Current;
            var status = _listener.GetAccessStatus();
            if (status == UserNotificationListenerAccessStatus.Unspecified && ask)
                status = await _listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                _listener = null;
                SetAccess(NotificationAccess.Denied);
                return;
            }
            _primed = false;
            SetAccess(NotificationAccess.Ready);
            await PollAsync();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Log.Error("notification listener", ex);
            _listener = null;
            Error = ex.Message;
            SetAccess(NotificationAccess.Error);
        }
    }

    private void SetAccess(NotificationAccess access)
    {
        Access = access;
        Changed?.Invoke();
    }

    public static string AccessText(NotificationAccess access) => access switch
    {
        NotificationAccess.NotPackaged => "Windows only lets apps from the Microsoft Store read notifications. Install NotchX from the Store to use this.",
        NotificationAccess.Denied => "NotchX isn't allowed to read notifications. Turn on Notification access for NotchX in Windows Settings › Privacy & security › Notifications.",
        NotificationAccess.Error => "Windows didn't let NotchX read notifications.",
        NotificationAccess.Off => "Notifications are switched off in Settings › Features.",
        _ => "Reading notifications from Windows' notification centre. They stay on this PC.",
    };

    private async Task PollAsync()
    {
        if (_polling || _listener == null) return;
        _polling = true;
        try
        {
            var all = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new List<MirroredNotification>();
            foreach (var n in all)
            {
                if (Items.FirstOrDefault(i => i.Id == n.Id) is { } known) { current.Add(known); continue; }
                var m = await ReadAsync(n);
                if (m == null || IsMuted(m.App)) continue;
                current.Add(m);
                if (_primed) Announce(m);
            }
            current = current.OrderByDescending(m => m.Time).ToList();
            if (!current.Select(c => c.Id).SequenceEqual(Items.Select(i => i.Id)))
            {
                Items.Clear();
                foreach (var c in current) Items.Add(c);
                Changed?.Invoke();
            }
            // Gone from Windows (read or dismissed there) or seen in the open notch = no longer unread.
            _unread.IntersectWith(current.Select(c => c.Id));
            if (Notch.Shell.IsExpanded) _unread.Clear();
            _primed = true;
            UpdatePill();
        }
        catch (Exception ex) { Log.Info("notifications: " + ex.Message); }
        finally { _polling = false; }
    }

    private static async Task<MirroredNotification?> ReadAsync(UserNotification n)
    {
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList() ?? new List<string>();
            string app = "", appId = "";
            try { app = n.AppInfo.DisplayInfo.DisplayName; } catch { }
            try { appId = n.AppInfo.AppUserModelId; } catch { }
            if (string.IsNullOrWhiteSpace(app)) app = string.IsNullOrEmpty(appId) ? "App" : appId.Split('!')[0].Split('.').Last();
            string? site = null;
            if (texts.Count > 1 && SiteRx.IsMatch(texts[^1]))
            {
                site = texts[^1];
                texts.RemoveAt(texts.Count - 1);
            }
            return new MirroredNotification
            {
                Id = n.Id,
                App = app,
                AppId = appId,
                Site = site,
                Title = texts.FirstOrDefault() ?? app,
                Body = string.Join("\n", texts.Skip(1)),
                Time = n.CreationTime,
                Icon = await IconAsync(n, appId),
            };
        }
        catch (Exception ex)
        {
            Log.Info("read notification: " + ex.Message);
            return null;
        }
    }

    private static async Task<ImageSource?> IconAsync(UserNotification n, string key)
    {
        if (IconCache.TryGetValue(key, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            var logo = n.AppInfo.DisplayInfo.GetLogo(new global::Windows.Foundation.Size(64, 64));
            using var winStream = await logo.OpenReadAsync();
            var copy = new MemoryStream();
            await winStream.AsStreamForRead().CopyToAsync(copy);
            copy.Position = 0;
            icon = Ui.LoadImage(copy, 64);
        }
        catch { }
        IconCache[key] = icon;
        return icon;
    }

    private static bool IsMuted(string app) =>
        SettingsStore.Current.Notifications.MutedApps.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase));

    private void Announce(MirroredNotification m)
    {
        _unread.Add(m.Id);
        var s = SettingsStore.Current.Notifications;
        if (!s.ShowIsland || (s.RespectDoNotDisturb && Notch.Hub.QuietMode)) return;
        var title = m.Title == m.App ? m.App : $"{m.App} · {m.Title}";
        var body = m.Body.Replace('\n', ' ');
        if (body.Length > 140) body = body[..140] + "…";
        Notch.Hub.Show(new Island
        {
            Key = "notification:" + m.Id,
            Glyph = Glyphs.Bell,
            Image = m.Icon,
            Title = s.ShowText ? title : m.App,
            Message = s.ShowText ? (body.Length > 0 ? body : null) : "New notification",
            Accent = Ui.Accent,
            Duration = TimeSpan.FromSeconds(Math.Clamp(s.Seconds, 2, 30)),
            OpenTab = Id,
            Actions =
            {
                new IslandAction("Dismiss", () => Dismiss(m), false, Glyphs.Close),
                new IslandAction("Open", () => Open(m), true),
            },
        });
    }

    private void UpdatePill()
    {
        if (_unread.Count == 0 || !SettingsStore.Current.Notifications.UnreadOnPill) { Notch.Hub.Remove(Id); return; }
        Notch.Hub.Upsert(Id, a =>
        {
            a.Glyph = Glyphs.Bell;
            a.Accent = Ui.Red;
            a.Text = _unread.Count.ToString();
            a.Detail = _unread.Count == 1 ? "1 new notification" : $"{_unread.Count} new notifications";
            a.Priority = 20;
            a.OpenTab = Id;
        });
    }

    private static readonly System.Text.RegularExpressions.Regex SiteRx = new(@"^([a-z0-9-]+\.)+[a-z]{2,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public void Open(MirroredNotification m)
    {
        Notch.Shell.Collapse();
        // Launching a browser opens a new tab, so for web notifications go back to the tab that sent it instead.
        if (BrowserTabs.ProcessFor(m.App) is { } browser)
        {
            _ = Task.Run(() => BrowserTabs.Focus(browser, m.Site));
            return;
        }
        if (string.IsNullOrEmpty(m.AppId)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{m.AppId}") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open notification app", ex); }
    }

    /// <summary>Remove from the notch and from Windows' notification centre.</summary>
    public void Dismiss(MirroredNotification m)
    {
        try { _listener?.RemoveNotification(m.Id); } catch (Exception ex) { Log.Info("dismiss notification: " + ex.Message); }
        Items.Remove(m);
        _unread.Remove(m.Id);
        Notch.Hub.DismissByKey("notification:" + m.Id);
        UpdatePill();
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        foreach (var m in Items.ToList()) Dismiss(m);
    }

    public void Mute(string app)
    {
        var muted = SettingsStore.Current.Notifications.MutedApps;
        if (!muted.Contains(app, StringComparer.OrdinalIgnoreCase)) muted.Add(app);
        SettingsStore.NotifyChanged();
        foreach (var m in Items.Where(i => i.App.Equals(app, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Items.Remove(m);
            _unread.Remove(m.Id);
        }
        UpdatePill();
        Changed?.Invoke();
        Notch.Hub.Notify(Glyphs.Bell, $"{app} muted", "Unmute it in Settings › Notifications", Ui.Gray, IslandPriority.Normal, 2.5);
    }

    private static string Age(DateTimeOffset t) => (DateTimeOffset.Now - t) switch
    {
        var d when d.TotalMinutes < 1 => "now",
        var d when d.TotalHours < 1 => $"{(int)d.TotalMinutes}m",
        var d when d.TotalDays < 1 => $"{(int)d.TotalHours}h",
        var d => $"{(int)d.TotalDays}d",
    };

    public override FrameworkElement CreateView()
    {
        var list = new StackPanel();
        var count = Text("", "Caption");
        count.VerticalAlignment = VerticalAlignment.Center;
        var clear = Chip("Clear all", (_, _) => ClearAll(), Glyphs.Delete);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(clear, Dock.Right);
        top.Children.Add(clear);
        top.Children.Add(count);
        var message = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };

        void Rebuild()
        {
            list.Children.Clear();
            message.Children.Clear();
            var ready = Access == NotificationAccess.Ready;
            top.Visibility = ready && Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            count.Text = Items.Count == 1 ? "1 notification" : $"{Items.Count} notifications";

            if (!ready || Items.Count == 0)
            {
                message.Children.Add(new TextBlock { Text = Glyphs.Bell, Style = S("Icon"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
                var text = Text(ready ? "No notifications" : AccessText(Access) + (Error != null ? $" ({Error})" : ""), "Caption");
                text.TextWrapping = TextWrapping.Wrap;
                text.TextAlignment = TextAlignment.Center;
                message.Children.Add(text);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
                if (Access == NotificationAccess.NotPackaged)
                    buttons.Children.Add(Chip("Get it from the Store", (_, _) => Ui.OpenUrl(StoreLink), Glyphs.Download, accent: true));
                if (Access is NotificationAccess.Denied or NotificationAccess.Error)
                {
                    buttons.Children.Add(Chip("Open Windows settings", (_, _) => Ui.OpenUrl("ms-settings:privacy-notifications"), Glyphs.Settings, accent: true));
                    buttons.Children.Add(Chip("Try again", (_, _) => _ = ConnectAsync(ask: true), Glyphs.Refresh));
                }
                if (buttons.Children.Count > 0) message.Children.Add(buttons);
                message.Visibility = Visibility.Visible;
                return;
            }

            message.Visibility = Visibility.Collapsed;
            var showText = SettingsStore.Current.Notifications.ShowText;
            foreach (var item in Items)
            {
                var m = item;
                var icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0) };
                if (m.Icon != null) icon.Background = new ImageBrush(m.Icon) { Stretch = Stretch.UniformToFill };
                else icon.Child = Icon(Glyphs.Bell, 14);

                var lines = new StackPanel();
                lines.Children.Add(Text($"{m.App}  ·  {Age(m.Time)}", "Caption", 10));
                var title = Text(showText ? m.Title : m.App, "Body");
                title.FontWeight = FontWeights.SemiBold;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                lines.Children.Add(title);
                if (showText && m.Body.Length > 0)
                {
                    var body = Text(m.Body, "Caption");
                    body.TextWrapping = TextWrapping.Wrap;
                    body.TextTrimming = TextTrimming.CharacterEllipsis;
                    body.MaxHeight = 34;
                    lines.Children.Add(body);
                }

                var dismiss = IconButton(Glyphs.Close, "Dismiss", (_, _) => Dismiss(m));
                dismiss.VerticalAlignment = VerticalAlignment.Top;
                var row = Columns((icon, Auto), (lines, Star()), (dismiss, Auto));
                var card = Card(row, new Thickness(0, 0, 0, 6));
                card.Cursor = Cursors.Hand;
                card.ToolTip = string.IsNullOrEmpty(m.AppId) ? null : $"Open {m.App}";
                card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not DependencyObject d || !IsInside(d, dismiss)) Open(m); };
                var menu = new ContextMenu();
                var open = new MenuItem { Header = $"Open {m.App}" };
                open.Click += (_, _) => Open(m);
                var mute = new MenuItem { Header = $"Mute {m.App}" };
                mute.Click += (_, _) => Mute(m.App);
                var remove = new MenuItem { Header = "Dismiss" };
                remove.Click += (_, _) => Dismiss(m);
                menu.Items.Add(open);
                menu.Items.Add(mute);
                menu.Items.Add(remove);
                card.ContextMenu = menu;
                list.Children.Add(card);
            }
        }

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(Scroll(list));
        var root = new Grid();
        root.Children.Add(dock);
        root.Children.Add(message);

        void OnChanged() => Ui.Post(Rebuild);
        root.Loaded += (_, _) => { Changed -= OnChanged; Changed += OnChanged; Rebuild(); };
        root.Unloaded += (_, _) => Changed -= OnChanged;
        // Opening the tab reads everything.
        root.IsVisibleChanged += (_, _) => { if (root.IsVisible && _unread.Count > 0) { _unread.Clear(); UpdatePill(); } };
        Rebuild();
        return root;
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == container) return true;
        return false;
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Notifications", Glyphs.Bell, () => Notch.Shell.OpenTab(Id), null, "alerts inbox messages"),
        new PaletteCommand("Clear all notifications", Glyphs.Delete, ClearAll, null, "dismiss alerts"),
    };
}

/// <summary>
/// Web notifications (WhatsApp Web, Gmail…) belong to the browser. Windows gives no way to click the real notification,
/// so find the tab for the site through UI Automation, switch to it and bring its window forward.
/// </summary>
internal static class BrowserTabs
{
    private static readonly (string App, string Process)[] Browsers =
    {
        ("Chrome", "chrome"), ("Edge", "msedge"), ("Brave", "brave"), ("Firefox", "firefox"),
        ("Opera", "opera"), ("Vivaldi", "vivaldi"), ("Arc", "arc"),
    };

    // Tabs are named after the page, not the domain, for these.
    private static readonly Dictionary<string, string> TabNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mail.google.com"] = "Gmail",
        ["calendar.google.com"] = "Calendar",
        ["chat.google.com"] = "Chat",
        ["teams.microsoft.com"] = "Teams",
        ["teams.live.com"] = "Teams",
        ["outlook.live.com"] = "Outlook",
        ["outlook.office.com"] = "Outlook",
    };

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase) { "www", "web", "m", "app", "mail", "com", "co", "org", "net" };

    /// <summary>The browser's process name when the notification came from a browser, else null.</summary>
    public static string? ProcessFor(string app) =>
        Browsers.FirstOrDefault(b => app.Contains(b.App, StringComparison.OrdinalIgnoreCase)).Process;

    /// <summary>"web.whatsapp.com" → WhatsApp's tab; "mail.google.com" → Gmail's.</summary>
    private static List<string> Keywords(string? site)
    {
        var keys = new List<string>();
        if (string.IsNullOrEmpty(site)) return keys;
        if (TabNames.TryGetValue(site, out var known)) keys.Add(known);
        var labels = site.Split('.');
        keys.AddRange(labels.Take(labels.Length - 1).Where(l => l.Length > 2 && !Generic.Contains(l)));
        return keys;
    }

    /// <summary>Runs off the UI thread: UI Automation over a browser window can take a moment.</summary>
    public static void Focus(string process, string? site)
    {
        try
        {
            var pids = Process.GetProcessesByName(process).Select(p => (uint)p.Id).ToHashSet();
            var windows = new List<IntPtr>();
            var text = new System.Text.StringBuilder(256);
            Native.EnumWindows((h, _) =>
            {
                text.Clear();
                if (Native.IsWindowVisible(h) && Native.GetWindowText(h, text, text.Capacity) > 0 &&
                    Native.GetWindowThreadProcessId(h, out var pid) != 0 && pids.Contains(pid))
                    windows.Add(h);
                return true;
            }, IntPtr.Zero);

            if (windows.Count == 0)
            {
                // The browser was closed since: open the site itself rather than a blank browser.
                if (site != null) Ui.OpenUrl("https://" + site);
                return;
            }

            var keys = Keywords(site);
            if (keys.Count > 0)
            {
                var isTab = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.TabItem);
                // EnumWindows lists front to back, so the most recently used window wins.
                foreach (var w in windows)
                {
                    var tabs = System.Windows.Automation.AutomationElement.FromHandle(w)
                        .FindAll(System.Windows.Automation.TreeScope.Descendants, isTab);
                    foreach (System.Windows.Automation.AutomationElement tab in tabs)
                    {
                        var name = tab.Current.Name ?? "";
                        if (!keys.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                        if (tab.TryGetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern, out var select))
                            ((System.Windows.Automation.SelectionItemPattern)select).Select();
                        else if (tab.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var invoke))
                            ((System.Windows.Automation.InvokePattern)invoke).Invoke();
                        Bring(w);
                        return;
                    }
                }
            }
            Bring(windows[0]);
        }
        catch (Exception ex) { Log.Info("focus browser tab: " + ex.Message); }
    }

    private static void Bring(IntPtr window)
    {
        if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
        if (!Native.SetForegroundWindow(window)) Native.SwitchToThisWindow(window, true);
    }
}
