using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Notchify.Core;

namespace Notchify.Shell;

public enum NotchState { Collapsed, Island, Expanded }

/// <summary>
/// The notch itself: one transparent, top-most, click-through-where-empty window at the top of the screen.
/// The pill morphs between three states — collapsed (live activities), island (notifications/HUDs) and
/// expanded (tabs) — with spring-like size animations.
/// </summary>
public partial class NotchWindow : Window, INotchShell
{
    private readonly Dictionary<string, FrameworkElement> _views = new();
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private IntPtr _hwnd;
    private NotchState _state = NotchState.Collapsed;
    private string _tab = "home";
    private bool _hiddenForFullscreen;
    private bool _peeking;
    private readonly GlassBackdrop _glass = new();
    private bool _glassActive;
    private Brush _collapsedBrush = Brushes.Black;
    private Brush _panelBrush = Brushes.Black;
    private bool? _pinnedBeforeEdit;

    public NotchWindow()
    {
        InitializeComponent();
        ActivityStrip.ItemsSource = Notch.Hub.Activities;

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); OnHoverDwell(); };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); TryAutoCollapse(); };
        _clockTimer.Tick += (_, _) => UpdateHeader();
        _topmostTimer.Tick += (_, _) => EnsureTopmost();

        Pill.MouseEnter += Pill_MouseEnter;
        Pill.MouseLeave += Pill_MouseLeave;
        Pill.MouseLeftButtonUp += Pill_Click;
        Pill.MouseWheel += Pill_MouseWheel;
        Pill.DragEnter += Pill_DragEnter;
        Pill.Drop += Pill_Drop;
        Pill.SizeChanged += (_, _) => { UpdateClip(); UpdateGlass(); };
        Pill.ContextMenu = new ContextMenu();
        Pill.ContextMenuOpening += Pill_ContextMenuOpening;
        LayoutEditor.Changed += OnLayoutEditChanged;
        Header.MouseWheel += Header_MouseWheel;
        Header.SizeChanged += Header_SizeChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => { if (_state == NotchState.Expanded && !_dragging && !CursorOverPill() && PinToggle.IsChecked != true) Collapse(); };
        // Any drag that starts inside the notch (windows between desktops, shelf files, launcher tiles, Home cards…)
        // keeps it open until the drop: during a drag Windows stops reporting that the mouse is over us.
        AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag), true);

        Notch.Hub.PropertyChanged += Hub_PropertyChanged;
        Notch.Hub.Activities.CollectionChanged += Activities_CollectionChanged;
        Notch.Modules.Changed += () => { RebuildTabs(); };
        Notch.Battery.PropertyChanged += (_, _) => UpdateHeader();
        Notch.KeepAwake.PropertyChanged += (_, _) => CaffeineToggle.IsChecked = Notch.KeepAwake.IsActive;
        SettingsStore.Changed += ApplySettings;
    }

    // ---------------- Window plumbing ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW); // keep out of Alt+Tab
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        Native.AddClipboardFormatListener(_hwnd);
        Notch.Hotkeys.Attach(_hwnd);
        Notch.Foreground.Start(_hwnd);
        Notch.Foreground.FullscreenChanged += OnFullscreenChanged;

        ApplySettings();
        RebuildTabs();
        UpdateHeader();
        _clockTimer.Start();
        _topmostTimer.Start();
        GoCollapsed(animate: false);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY:
                handled = Notch.Hotkeys.Handle(wParam.ToInt32());
                break;
            case Native.WM_CLIPBOARDUPDATE:
                Notch.Clipboard.OnClipboardChanged();
                break;
            case Native.WM_DEVICECHANGE:
                Notch.Drives.OnDeviceChange();
                break;
            case Native.WM_POWERBROADCAST:
                Notch.Battery.Refresh();
                break;
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED when !_positioning:
                Dispatcher.BeginInvoke(PositionWindow, DispatcherPriority.Background);
                break;
            case Native.WM_SETTINGCHANGE when lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
                // Windows "Transparency effects" toggled: switch between glass and the solid fallback.
                Dispatcher.BeginInvoke(ApplySettings, DispatcherPriority.Background);
                break;
            case Native.WM_MOUSEHWHEEL when _state == NotchState.Expanded:
                // Two-finger horizontal swipe on a touchpad switches tabs.
                var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                _hwheelAccum += delta;
                if (Math.Abs(_hwheelAccum) >= 240) { SwitchTab(_hwheelAccum > 0 ? 1 : -1); _hwheelAccum = 0; }
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private int _hwheelAccum;
    private double _targetW, _targetH;
    private bool _positioning;

    protected override void OnClosed(EventArgs e)
    {
        Native.RemoveClipboardFormatListener(_hwnd);
        Notch.Foreground.Stop();
        _glass.Close();
        base.OnClosed(e);
    }

    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

    /// <summary>Place the (transparent) window centred at the top of the chosen display.</summary>
    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        _positioning = true;
        try
        {
            var a = SettingsStore.Current.Appearance;
            var screens = System.Windows.Forms.Screen.AllScreens;
            var screen = a.DisplayIndex >= 0 && a.DisplayIndex < screens.Length ? screens[a.DisplayIndex] : System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
            var b = screen.Bounds;
            var hmon = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + 1 }, Native.MONITOR_DEFAULTTONEAREST);
            var scale = GetDpiForMonitor(hmon, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

            var wDip = Math.Max(a.ExpandedWidth, 460) + 60;
            // + room for up to three extra rows of wrapped tabs (empty window pixels are click-through).
            var hDip = Math.Max(a.ExpandedHeight, 180) + a.TopOffset + 40 + 140;
            var wPx = (int)(wDip * scale);
            var hPx = (int)(hDip * scale);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.Left + (b.Width - wPx) / 2, b.Top, wPx, hPx, Native.SWP_NOACTIVATE);
            Width = wDip;
            Height = hDip;
        }
        finally { _positioning = false; }
        UpdateGlass();
    }

    private void EnsureTopmost()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _glass.KeepBelow(_hwnd);
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        if (!SettingsStore.Current.Behavior.HideInFullscreen) return;
        _hiddenForFullscreen = fullscreen;
        PillHost.Visibility = fullscreen && _state != NotchState.Island ? Visibility.Hidden : Visibility.Visible;
        UpdateGlass();
    }

    public void ApplySettings()
    {
        var s = SettingsStore.Current;
        var a = s.Appearance;
        Application.Current.Resources["AccentBrush"] = Ui.Brush(a.AccentColor);
        Application.Current.Resources["NotchBrush"] = Ui.Brush(a.BackgroundColor);
        ApplyTabTint(a);
        Sheen.Opacity = Math.Clamp(a.GlassIntensity, 0, 1);
        Rim.Opacity = Math.Clamp(a.GlassIntensity, 0, 1);
        PillHost.Margin = new Thickness(0, a.TopOffset, 0, 0);

        // Fill: the collapsed pill stays a solid notch colour; the open notch and islands get the chosen
        // background, made translucent over a live blur when glass is on and Windows allows it.
        _glassActive = a.Glass && GlassBackdrop.SystemAllowsBlur;
        _collapsedBrush = Ui.Brush(a.BackgroundColor);
        _panelBrush = BuildPanelBrush(a, _glassActive);
        BgImage.Source = a.BackgroundType == "Image" && !string.IsNullOrWhiteSpace(a.BackgroundImage) ? Ui.LoadImage(a.BackgroundImage, 1600) : null;
        BgImage.Opacity = Math.Clamp(a.BackgroundImageOpacity, 0, 1);

        // Text & control size inside the notch.
        var scale = Math.Clamp(a.UiScale, 0.7, 1.6);
        Transform zoom = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
        CompactLayer.LayoutTransform = zoom;
        IslandLayer.LayoutTransform = zoom;
        ExpandedLayer.LayoutTransform = zoom;

        if (_hwnd != IntPtr.Zero)
        {
            var affinity = s.Behavior.HideFromScreenCapture ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE;
            Native.SetWindowDisplayAffinity(_hwnd, affinity);
            _glass.SetExcludedFromCapture(s.Behavior.HideFromScreenCapture);
        }
        var tabLook = a.TabLabels + "|" + a.TabOverflow;
        if (tabLook != _tabLabels)
        {
            var first = _tabLabels == null;
            _tabLabels = tabLook;
            if (!first) RebuildTabs();
        }
        UpdateHeader(); // clock format may have changed
        PositionWindow();
        RefreshSize(animate: false);
        UpdateFill();
    }

    private string? _tabLabels;

    /// <summary>
    /// The selected tab's capsule colour and strength. A strong tint would hide text of the same colour,
    /// so past halfway the name switches to white or black, whichever reads better on it.
    /// </summary>
    private static void ApplyTabTint(AppearanceSettings a)
    {
        var tint = Ui.Color(string.IsNullOrWhiteSpace(a.TabTintColor) ? a.AccentColor : a.TabTintColor);
        tint.A = 255;
        var strength = Math.Clamp(a.TabTintStrength, 0.05, 1);
        var fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(strength * 255), tint.R, tint.G, tint.B));
        fill.Freeze();
        Color text = tint;
        if (strength >= 0.5)
        {
            var luminance = (0.299 * tint.R + 0.587 * tint.G + 0.114 * tint.B) / 255;
            text = luminance > 0.6 ? Colors.Black : Colors.White;
        }
        var textBrush = new SolidColorBrush(text);
        textBrush.Freeze();
        Application.Current.Resources["TabTintFillBrush"] = fill;
        Application.Current.Resources["TabTintTextBrush"] = textBrush;
    }

    private static Brush BuildPanelBrush(AppearanceSettings a, bool glass)
    {
        Color Tint(string hex)
        {
            var c = Ui.Color(hex);
            return glass ? Color.FromArgb((byte)Math.Round(Math.Clamp(a.TintOpacity, 0, 1) * 255), c.R, c.G, c.B) : c;
        }
        Brush brush;
        if (a.BackgroundType == "Gradient")
        {
            var rad = a.GradientAngle * Math.PI / 180;
            var dx = Math.Cos(rad) / 2;
            var dy = Math.Sin(rad) / 2;
            brush = new LinearGradientBrush(Tint(a.BackgroundColor), Tint(a.GradientColor), new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
        }
        else brush = new SolidColorBrush(Tint(a.BackgroundColor));
        brush.Freeze();
        return brush;
    }

    /// <summary>Pick the fill for the current state and show or hide the blur behind it.</summary>
    private void UpdateFill()
    {
        var open = _state != NotchState.Collapsed;
        Pill.Background = open ? _panelBrush : _collapsedBrush;
        BgImage.Visibility = open && BgImage.Source != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateClip();
        UpdateGlass();
    }

    private void UpdateGlass()
    {
        if (_hwnd == IntPtr.Zero) return;
        var want = _glassActive && _state != NotchState.Collapsed && PillHost.Visibility == Visibility.Visible && IsVisible;
        if (want) _glass.Show(_hwnd, Pill, Pill.CornerRadius);
        else _glass.Hide();
    }

    // ---------------- State machine ----------------

    public bool IsExpanded => _state == NotchState.Expanded;

    public bool KeepOpen
    {
        get => PinToggle.IsChecked == true;
        set => PinToggle.IsChecked = value;
    }
    public IntPtr LastExternalWindow => Notch.Foreground.LastExternal;

    /// <summary>Open on the user's default page (Settings › General › Open on).</summary>
    public void Expand() => ExpandTo(null);

    private void ExpandTo(string? tab)
    {
        if (_state == NotchState.Expanded) return;
        _peeking = false;
        _state = NotchState.Expanded;
        PillHost.Visibility = Visibility.Visible;
        _tab = tab ?? DefaultTab();
        ShowTab(_tab);
        ShowLayer(ExpandedLayer);
        RefreshSize(animate: true);
        UpdateHeader();
    }

    /// <summary>Which tab the notch opens on: a fixed tab, the last one used, or "smart".</summary>
    private string DefaultTab()
    {
        var b = SettingsStore.Current.Behavior;
        var visible = Notch.Modules.Tabs.Select(t => t.Id).ToList();
        string Pick(params string[] ids) => ids.FirstOrDefault(visible.Contains) ?? visible.FirstOrDefault() ?? "home";
        return b.DefaultTab switch
        {
            "last" => Pick(b.LastTab, _tab),
            "smart" => Notch.Media.IsPlaying ? Pick("music", "home")
                : Notch.Modules.Get("timer") is Modules.TimerModule { Running: true } ? Pick("timer", "home")
                : Pick("home"),
            var id => Pick(id),
        };
    }

    public void Collapse()
    {
        _collapseTimer.Stop();
        _peeking = false;
        LayoutEditor.IsEditing = false;
        if (Notch.Hub.Current != null) GoIsland(Notch.Hub.Current);
        else GoCollapsed(animate: true);
    }

    public void Toggle()
    {
        if (_state == NotchState.Expanded) Collapse();
        else
        {
            Expand();
            Activate();
        }
    }

    public void OpenTab(string moduleId)
    {
        if (_state != NotchState.Expanded) ExpandTo(moduleId); else ShowTab(moduleId);
        Activate();
    }

    private void GoCollapsed(bool animate)
    {
        _state = NotchState.Collapsed;
        ShowLayer(CompactLayer);
        RefreshSize(animate);
        if (_hiddenForFullscreen) PillHost.Visibility = Visibility.Hidden;
    }

    private void GoIsland(Island island)
    {
        if (_state == NotchState.Expanded) return; // the open panel wins; island waits its turn
        _state = NotchState.Island;
        PillHost.Visibility = Visibility.Visible;
        IslandLayer.Content = island.CustomContent ?? (object)island;
        ShowLayer(IslandLayer);
        RefreshSize(animate: true);
    }

    private void ShowLayer(UIElement target)
    {
        foreach (var layer in new UIElement[] { CompactLayer, IslandLayer, ExpandedLayer })
        {
            if (layer == target)
            {
                layer.Visibility = Visibility.Visible;
                Fade(layer, 0, 1, 220, 70);
            }
            else layer.Visibility = Visibility.Collapsed;
        }
        UpdateFill();
    }

    /// <summary>Rise into place: a few pixels up while fading in (used when switching tabs).</summary>
    private static void Rise(UIElement el, double distance, int ms)
    {
        if (!SettingsStore.Current.Appearance.AnimationsEnabled) return;
        var speed = Math.Max(0.25, SettingsStore.Current.Appearance.AnimationSpeed);
        if (el.RenderTransform is not TranslateTransform t) el.RenderTransform = t = new TranslateTransform();
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(ms / speed))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static void Fade(UIElement el, double from, double to, int ms, int delay)
    {
        if (!SettingsStore.Current.Appearance.AnimationsEnabled) { el.Opacity = to; return; }
        var speed = Math.Max(0.25, SettingsStore.Current.Appearance.AnimationSpeed);
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms / speed))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay / speed),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Recompute the pill's target size for the current state and animate to it.</summary>
    private void RefreshSize(bool animate)
    {
        var a = SettingsStore.Current.Appearance;
        double w, h;
        switch (_state)
        {
            case NotchState.Expanded:
                w = a.ExpandedWidth;
                h = a.ExpandedHeight + _headerExtra;
                break;
            case NotchState.Island:
                w = Math.Max(a.CollapsedWidth + 120, 380) * (IslandLayer.Content is Island { Progress: not null } ? SettingsStore.Current.Huds.HudScale : 1);
                IslandLayer.Measure(new Size(w, double.PositiveInfinity));
                h = Math.Max(a.CollapsedHeight + 20, IslandLayer.DesiredSize.Height);
                if (IslandLayer.Content is FrameworkElement custom)
                {
                    custom.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    w = Math.Max(w, custom.DesiredSize.Width);
                    h = Math.Max(h, custom.DesiredSize.Height);
                }
                break;
            default:
                ActivityStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var content = ActivityStrip.DesiredSize.Width + 28;
                w = Math.Max(a.CollapsedWidth, content);
                h = a.CollapsedHeight;
                break;
        }
        w = Math.Min(w, Width - 20);
        // Ignore sub-pixel jitter from ticking timers so the pill doesn't wobble every second.
        if (animate && _state == NotchState.Collapsed && Math.Abs(w - _targetW) < 6 && Math.Abs(h - _targetH) < 1) return;
        _targetW = w;
        _targetH = h;

        var radius = _state == NotchState.Collapsed ? Math.Min(a.CornerRadius, h / 2) : a.CornerRadius + 8;
        Pill.CornerRadius = a.Style == "Pill" ? new CornerRadius(radius) : new CornerRadius(0, 0, radius, radius);
        UpdateClip();
        UpdateGlass();

        if (!animate || !a.AnimationsEnabled)
        {
            Pill.BeginAnimation(WidthProperty, null);
            Pill.BeginAnimation(HeightProperty, null);
            Pill.Width = w; Pill.Height = h;
            return;
        }
        var speed = Math.Max(0.25, a.AnimationSpeed);
        var growing = h > Pill.ActualHeight + 1;
        IEasingFunction ease = growing
            ? new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut } // a little overshoot feels springy
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds((growing ? 420 : 300) / speed);
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
    }

    private void UpdateClip()
    {
        var w = Pill.ActualWidth; var h = Pill.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = Pill.CornerRadius;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double tl = Math.Min(r.TopLeft, h / 2), tr = Math.Min(r.TopRight, h / 2), br = Math.Min(r.BottomRight, h / 2), bl = Math.Min(r.BottomLeft, h / 2);
            ctx.BeginFigure(new Point(tl, 0), true, true);
            ctx.LineTo(new Point(w - tr, 0), false, false);
            if (tr > 0) ctx.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(w, h - br), false, false);
            if (br > 0) ctx.ArcTo(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(bl, h), false, false);
            if (bl > 0) ctx.ArcTo(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(0, tl), false, false);
            if (tl > 0) ctx.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, false, false);
        }
        geo.Freeze();
        PillContent.Clip = geo;
        // Over glass the shadow must stay outside the pill, or it darkens the frosted area.
        ShadowHost.Clip = _glassActive && _state != NotchState.Collapsed
            ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-80, -80, w + 160, h + 160)), geo)
            : null;
    }

    // ---------------- Islands & activities ----------------

    private void Hub_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActivityHub.Current)) return;
        var island = Notch.Hub.Current;
        if (island != null) GoIsland(island);
        else if (_state == NotchState.Island) GoCollapsed(animate: true);
    }

    private void Activities_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (LiveActivity a in e.NewItems)
                a.PropertyChanged += (_, pe) =>
                {
                    if (pe.PropertyName is nameof(LiveActivity.Text) or nameof(LiveActivity.HasImage) or nameof(LiveActivity.HasProgress))
                        NotifyActivityLayoutChanged();
                };
        if (_state == NotchState.Collapsed)
            Dispatcher.BeginInvoke(() => RefreshSize(animate: true), DispatcherPriority.Loaded);
    }

    /// <summary>Activities update their text in place; re-measure the pill now and then.</summary>
    public void NotifyActivityLayoutChanged()
    {
        if (_state == NotchState.Collapsed) RefreshSize(animate: true);
    }

    private void Activity_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LiveActivity { OpenTab: { } tab })
        {
            OpenTab(tab);
            e.Handled = true;
        }
    }

    private void IslandAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not IslandAction action) return;
        var island = Notch.Hub.Current;
        try { action.Run(); }
        catch (Exception ex) { Log.Error("island action", ex); }
        if (island != null && Notch.Hub.Current == island) Notch.Hub.Dismiss(island);
        e.Handled = true;
    }

    private void IslandImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((sender as FrameworkElement)?.Tag is Island { DragFiles: { Length: > 0 } files })
        {
            Notch.Hub.HoldCurrent = true;
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, files), DragDropEffects.Copy | DragDropEffects.Move);
            Notch.Hub.HoldCurrent = false;
        }
    }

    // ---------------- Mouse & gestures ----------------

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_state == NotchState.Island) Notch.Hub.HoldCurrent = true;
        var b = SettingsStore.Current.Behavior;
        if ((b.HoverToOpen || b.PeekOnHover) && _state != NotchState.Expanded)
        {
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, b.HoverDelayMs));
            _hoverTimer.Start();
        }
    }

    private void OnHoverDwell()
    {
        if (!Pill.IsMouseOver || _state == NotchState.Expanded) return;
        var b = SettingsStore.Current.Behavior;
        if (b.PeekOnHover && _state == NotchState.Collapsed && Notch.Media.HasSession && PeekFactory != null)
        {
            // Minimal now-playing peek: transport controls without opening the panel.
            _peeking = true;
            Notch.Hub.Show(new Island { Key = "peek", CustomContent = PeekFactory(), Sticky = true, Priority = IslandPriority.Low });
            return;
        }
        if (b.HoverToOpen && _state == NotchState.Collapsed) Expand();
    }

    /// <summary>Supplied by the Now Playing module.</summary>
    public static Func<FrameworkElement>? PeekFactory { get; set; }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_state == NotchState.Island) Notch.Hub.HoldCurrent = false;
        if (_peeking) { _peeking = false; Notch.Hub.DismissByKey("peek"); }
        var b = SettingsStore.Current.Behavior;
        if (_state == NotchState.Expanded && b.AutoCollapse && PinToggle.IsChecked != true && !_dragging)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, b.AutoCollapseDelayMs));
            _collapseTimer.Start();
        }
    }

    private bool _dragging;

    private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        var ending = e.EscapePressed || (e.KeyStates & DragDropKeyStates.LeftMouseButton) == 0;
        if (!ending)
        {
            if (!_dragging) { _dragging = true; _collapseTimer.Stop(); }
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        // Dropped: give a moment to start the next drag before closing (at least a second).
        var b = SettingsStore.Current.Behavior;
        if (_state == NotchState.Expanded && b.AutoCollapse && PinToggle.IsChecked != true)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, b.AutoCollapseDelayMs));
            _collapseTimer.Start();
        }
    }

    /// <summary>
    /// Mouse over the pill, checked against the real cursor position: WPF's IsMouseOver goes stale during and
    /// right after drag and drop.
    /// </summary>
    private bool CursorOverPill()
    {
        if (Pill.IsMouseOver) return true;
        if (!Pill.IsVisible || !Native.GetCursorPos(out var p)) return false;
        try
        {
            var topLeft = Pill.PointToScreen(new Point(0, 0));
            var bottomRight = Pill.PointToScreen(new Point(Pill.ActualWidth, Pill.ActualHeight));
            return p.X >= topLeft.X && p.X <= bottomRight.X && p.Y >= topLeft.Y && p.Y <= bottomRight.Y;
        }
        catch { return false; }
    }

    private void TryAutoCollapse()
    {
        if (_dragging || CursorOverPill() || _state != NotchState.Expanded) return;
        // Don't yank the panel away while the user is typing in it or a popup (combo box) is open.
        if (IsActive && Keyboard.FocusedElement is TextBox) return;
        if (Mouse.Captured != null) return;
        Collapse();
    }

    private void Pill_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        switch (_state)
        {
            case NotchState.Collapsed:
                Expand();
                Activate();
                break;
            case NotchState.Island when Notch.Hub.Current is { } island:
                if (_peeking) { _peeking = false; Notch.Hub.Dismiss(island); Expand(); Activate(); break; }
                if (island.HasActions) break; // let the buttons do the work
                Notch.Hub.Dismiss(island);
                if (island.OpenTab != null) OpenTab(island.OpenTab); else { Expand(); Activate(); }
                break;
        }
    }

    private void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_state == NotchState.Expanded) return; // let content scroll
        if (SettingsStore.Current.Behavior.ScrollOnPillChangesVolume)
        {
            Notch.Audio.Step(e.Delta > 0 ? 0.02 : -0.02);
            e.Handled = true;
        }
    }

    private void Header_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (LayoutEditor.IsEditing) return; // every chip is on screen (they wrap); nothing to scroll
        if (!SettingsStore.Current.Behavior.ScrollToSwitchTabs) return;
        SwitchTab(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Pill_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (_state != NotchState.Expanded || (_tab != "shelf" && _tab != "launcher" && _tab != "documents")) OpenTab("shelf");
        e.Effects = DragDropEffects.Copy;
    }

    private void Pill_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            Notch.Shelf.Add(files);
            OpenTab("shelf");
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Collapse(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9)
        {
            var tabs = Notch.Modules.Tabs.ToList();
            var i = e.Key - Key.D1;
            if (i < tabs.Count) ShowTab(tabs[i].Id);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Tab)
        {
            SwitchTab(1);
            e.Handled = true;
        }
    }

    // ---------------- Tabs ----------------

    private readonly List<(RadioButton Button, NotchModule Module)> _tabButtons = new();
    private RadioButton? _moreButton;
    private List<NotchModule> _overflow = new();

    private void RebuildTabs()
    {
        TabStrip.Children.Clear();
        _tabButtons.Clear();
        _moreButton = null;
        _overflow.Clear();
        var appearance = SettingsStore.Current.Appearance;
        var labels = appearance.TabLabels;
        var withLabels = labels != "Never";
        _rowHeight = withLabels ? 44 : 32;
        HeaderRight.Height = _rowHeight;
        Header.MinHeight = _rowHeight;
        // One fixed row with "More ▾", or as many rows as the tabs need (always while editing, so every tab can be arranged).
        var wrap = LayoutEditor.IsEditing || appearance.TabOverflow != "Menu";
        Header.Height = wrap ? double.NaN : _rowHeight;
        if (LayoutEditor.IsEditing) { BuildEditableTabs(); return; }
        var index = 1;
        foreach (var m in Notch.Modules.Tabs)
        {
            var id = m.Id;
            var rb = new RadioButton
            {
                Style = (Style)FindResource("TabButton"),
                GroupName = "tabs",
                Tag = id,
                ToolTip = index <= 9 ? $"{m.Title}  (Ctrl+{index})" : m.Title,
                IsChecked = id == _tab,
            };
            SetTabContent(rb, m.Glyph, m.ShortTitle, labels, rb);
            rb.Checked += (_, _) => ShowTab(id);
            TabStrip.Children.Add(rb);
            _tabButtons.Add((rb, m));
            index++;
        }

        // Tabs that don't fit go into "More ▾" at the end of the row.
        _moreButton = new RadioButton { Style = (Style)FindResource("TabButton"), Visibility = Visibility.Collapsed, ToolTip = "More tabs" };
        _moreButton.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; OpenMoreMenu(); };
        TabStrip.Children.Add(_moreButton);

        if (Notch.Modules.Tabs.All(t => t.Id != _tab)) _tab = Notch.Modules.Tabs.FirstOrDefault()?.Id ?? "home";
        foreach (var stale in _views.Keys.Where(k => Notch.Modules.Tabs.All(t => t.Id != k)).ToList()) _views.Remove(stale);
        if (_state == NotchState.Expanded) ShowTab(_tab);
        Dispatcher.BeginInvoke(LayoutTabs, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Icon, with the name underneath unless labels are off ("Selected" = only while <paramref name="checkedSource"/> is on).</summary>
    private static void SetTabContent(RadioButton rb, string glyph, string label, string labels, RadioButton checkedSource)
    {
        if (labels == "Never")
        {
            rb.Content = glyph;
            return;
        }
        var text = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 9.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 64,
            Margin = new Thickness(0, 1, 0, 0),
        };
        if (labels == "Selected")
            text.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(RadioButton.IsChecked))
                { Source = checkedSource, Converter = Notchify.Controls.Converters.Visible });
        rb.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center },
                text,
            },
        };
        rb.Width = double.NaN;
        rb.MinWidth = 40;
        rb.Height = 40;
        rb.Padding = new Thickness(6, 0, 6, 0);
    }

    private void TabScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) LayoutTabs();
    }

    private double _rowHeight = 44;
    private double _headerExtra;

    /// <summary>When the tabs wrap onto more rows, grow the open notch by that much so the page keeps its space.</summary>
    private void Header_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged) return;
        var extra = Math.Max(0, Header.ActualHeight - _rowHeight);
        if (Math.Abs(extra - _headerExtra) < 0.5) return;
        _headerExtra = extra;
        if (_state == NotchState.Expanded) RefreshSize(animate: true);
    }

    /// <summary>Show as many tabs as fit beside the clock; the rest move into the "More" menu (or wrap, if that's the setting).</summary>
    private void LayoutTabs()
    {
        if (LayoutEditor.IsEditing || _moreButton == null || _tabButtons.Count == 0) return;
        if (SettingsStore.Current.Appearance.TabOverflow != "Menu")
        {
            foreach (var (rb, _) in _tabButtons) rb.Visibility = Visibility.Visible;
            _overflow.Clear();
            _moreButton.Visibility = Visibility.Collapsed;
            return;
        }
        // A couple of pixels' slack so rounding never pushes the last tab onto a second row.
        var available = TabScroller.ActualWidth - 2;
        if (available <= 0) return;

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var widths = new List<double>();
        foreach (var (rb, _) in _tabButtons)
        {
            rb.Visibility = Visibility.Visible;
            rb.Measure(infinite);
            widths.Add(rb.DesiredSize.Width + rb.Margin.Left + rb.Margin.Right);
        }
        var fit = _tabButtons.Count;
        if (widths.Sum() > available)
        {
            // Leave room for the More button, sized for its widest look (it shows the open tab's name when that's hidden).
            var room = available - MoreButtonWidth;
            fit = 0;
            var used = 0.0;
            while (fit < _tabButtons.Count && used + widths[fit] <= room) used += widths[fit++];
        }
        for (var i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].Button.Visibility = i < fit ? Visibility.Visible : Visibility.Collapsed;
        _overflow = _tabButtons.Skip(fit).Select(t => t.Module).ToList();
        _moreButton.Visibility = _overflow.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMoreButton();
    }

    private double MoreButtonWidth => SettingsStore.Current.Appearance.TabLabels == "Never" ? 34 : 70;

    /// <summary>"More ▾", or the open tab's icon and name when it's one of the hidden ones.</summary>
    private void UpdateMoreButton()
    {
        if (_moreButton == null) return;
        var current = _overflow.FirstOrDefault(m => m.Id == _tab);
        var labels = SettingsStore.Current.Appearance.TabLabels;
        _moreButton.IsChecked = current != null;
        // Labels on "Selected" would only show when checked; the More button always names itself.
        SetTabContent(_moreButton, current?.Glyph ?? Glyphs.More, (current?.ShortTitle ?? "More") + " ▾", labels == "Never" ? "Never" : "Always", _moreButton);
        _moreButton.Width = labels == "Never" ? 34 : double.NaN;
        _moreButton.MaxWidth = MoreButtonWidth;
        _moreButton.ToolTip = current != null ? $"{current.Title} — more tabs" : $"{_overflow.Count} more tab{(_overflow.Count == 1 ? "" : "s")}";
    }

    private void OpenMoreMenu()
    {
        if (_moreButton == null || _overflow.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = _moreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var m in _overflow)
        {
            var id = m.Id;
            var item = new MenuItem
            {
                Header = m.Title,
                Icon = new TextBlock { Text = m.Glyph, Style = (Style)FindResource("Icon"), FontSize = 13 },
                IsChecked = id == _tab,
            };
            item.Click += (_, _) => ShowTab(id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Edit-layout tab strip: every tab as a chip you can drag to reorder, with an eye to show/hide it.</summary>
    private void BuildEditableTabs()
    {
        const string format = "notchify/tab";
        foreach (var m in Notch.Modules.TabCandidates)
        {
            var id = m.Id;
            var visible = Notch.Modules.IsTabVisible(id);
            var eye = new Button { Style = (Style)FindResource("IconButton"), Width = 22, Height = 22, FontSize = 11,
                Content = visible ? Glyphs.View : Glyphs.Hide, ToolTip = visible ? $"Hide the {m.Title} tab" : $"Show the {m.Title} tab" };
            eye.Click += (_, e) => { Notch.Modules.SetTabVisible(id, !visible); e.Handled = true; };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 1, 1, 1),
                Margin = new Thickness(0, 3, 4, 3),
                Background = (Brush)FindResource(id == _tab ? "CardHoverBrush" : "CardBrush"),
                BorderBrush = (Brush)FindResource("DividerBrush"),
                BorderThickness = new Thickness(1),
                Opacity = visible ? 1 : 0.45,
                Cursor = Cursors.SizeAll,
                AllowDrop = true,
                ToolTip = $"{m.Title} — drag to move",
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = m.Glyph, Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 5, 0) },
                        new TextBlock { Text = m.ShortTitle, Style = (Style)FindResource("Caption"), Foreground = (Brush)FindResource("TextBrush"),
                            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) },
                        eye,
                    },
                },
            };
            Point? pressed = null;
            chip.PreviewMouseLeftButtonDown += (_, e) => pressed = e.GetPosition(chip);
            chip.PreviewMouseLeftButtonUp += (_, _) =>
            {
                if (pressed != null && visible) ShowTab(id);
                pressed = null;
            };
            chip.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || pressed is not { } start) return;
                var d = e.GetPosition(chip) - start;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = null;
                DragDrop.DoDragDrop(chip, new DataObject(format, id), DragDropEffects.Move);
            };
            chip.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(format) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            chip.Drop += (_, e) =>
            {
                if (e.Data.GetData(format) is string dragged) Dispatcher.BeginInvoke(() => Notch.Modules.MoveTo(dragged, id));
                e.Handled = true;
            };
            TabStrip.Children.Add(chip);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => LayoutEditor.Toggle();

    private void AddCard_Click(object sender, RoutedEventArgs e) => LayoutEditor.RequestAddCard();

    private void UpdateEditButtons()
    {
        AddCardButton.Visibility = LayoutEditor.IsEditing && _tab == "home" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLayoutEditChanged()
    {
        var editing = LayoutEditor.IsEditing;
        EditToggle.IsChecked = editing;
        DoneButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        // Stay open while editing; restore the user's own "keep open" choice afterwards.
        if (editing) { _pinnedBeforeEdit ??= PinToggle.IsChecked == true; PinToggle.IsChecked = true; }
        else if (_pinnedBeforeEdit is { } was) { PinToggle.IsChecked = was; _pinnedBeforeEdit = null; }
        RebuildTabs();
        UpdateEditButtons();
    }

    // ---------------- Right-click menu ----------------

    private void Pill_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Pill.ContextMenu!;
        menu.Items.Clear();
        var b = SettingsStore.Current.Behavior;

        var openOn = new MenuItem { Header = "Open on" };
        void Option(string label, string value)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = b.DefaultTab == value };
            item.Click += (_, _) => { b.DefaultTab = value; SettingsStore.NotifyChanged(); };
            openOn.Items.Add(item);
        }
        foreach (var t in Notch.Modules.Tabs) Option(t.Title, t.Id);
        openOn.Items.Add(new Separator());
        Option("Last used tab", "last");
        Option("Smart (music while playing)", "smart");
        menu.Items.Add(openOn);

        var a = SettingsStore.Current.Appearance;
        var extra = new MenuItem { Header = "Tabs that don't fit" };
        void Overflow(string label, string value)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = a.TabOverflow == value };
            item.Click += (_, _) => { a.TabOverflow = value; SettingsStore.NotifyChanged(); };
            extra.Items.Add(item);
        }
        Overflow("Show on a second row", "Wrap");
        Overflow("Put in a More ▾ menu", "Menu");
        menu.Items.Add(extra);

        var edit =new MenuItem { Header = LayoutEditor.IsEditing ? "Finish editing layout" : "Edit layout…" };
        edit.Click += (_, _) =>
        {
            if (!LayoutEditor.IsEditing && _state != NotchState.Expanded) { Expand(); Activate(); }
            LayoutEditor.Toggle();
        };
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Settings" };
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);
    }

    private void ShowTab(string id)
    {
        var module = Notch.Modules.Get(id);
        if (module == null || !module.HasTab) return;
        _tab = id;
        SettingsStore.Current.Behavior.LastTab = id;
        if (!_views.TryGetValue(id, out var view))
        {
            try { view = module.CreateView() ?? new TextBlock { Text = module.Title }; }
            catch (Exception ex)
            {
                Log.Error($"view {id}", ex);
                view = new TextBlock { Text = $"{module.Title} failed to load: {ex.Message}", Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap };
            }
            _views[id] = view; // views are cached: switching tabs never tears down running work
        }
        TabContent.Content = view;
        if (LayoutEditor.IsEditing) RebuildTabs(); // highlight the current chip
        UpdateEditButtons();
        foreach (var (rb, _) in _tabButtons)
        {
            var isThis = (string)rb.Tag == id;
            if (rb.IsChecked != isThis) rb.IsChecked = isThis;
        }
        // With labels only on the open tab, widths change as you switch, so re-fit the row.
        if (SettingsStore.Current.Appearance.TabLabels == "Selected") LayoutTabs();
        else UpdateMoreButton();
        Fade(TabContent, 0.2, 1, 200, 0);
        Rise(TabContent, 6, 240);
    }

    private void SwitchTab(int delta)
    {
        var tabs = Notch.Modules.Tabs.ToList();
        if (tabs.Count == 0) return;
        var i = tabs.FindIndex(t => t.Id == _tab);
        ShowTab(tabs[(i + delta + tabs.Count) % tabs.Count].Id);
    }

    // ---------------- Header ----------------

    private void UpdateHeader()
    {
        var clock = SettingsStore.Current.Clock;
        ClockText.Visibility = clock.ShowInHeader ? Visibility.Visible : Visibility.Collapsed;
        ClockText.Text = ClockFormat.Header(DateTime.Now);
        if (_clockTimer.Interval != ClockFormat.TickInterval) _clockTimer.Interval = ClockFormat.TickInterval;
        var bat = Notch.Battery;
        BatteryPanel.Visibility = bat.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatteryText.Text = bat.Text;
        BatteryGlyph.Text = bat.Glyph;
        BatteryGlyph.Foreground = bat.Charging ? Ui.Green : bat.Percent is >= 0 and <= 20 ? Ui.Red : (Brush)FindResource("TextBrush");
        CaffeineToggle.IsChecked = Notch.KeepAwake.IsActive;
    }

    private void Caffeine_Click(object sender, RoutedEventArgs e) => Notch.KeepAwake.Toggle();

    private void PopOut_Click(object sender, RoutedEventArgs e) => PopOut(_tab);

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    public void PopOut(string moduleId)
    {
        var m = Notch.Modules.Get(moduleId);
        if (m == null) return;
        Collapse();
        new DetachedWindow(m).Show();
    }

    private SettingsWindow? _settings;

    public void ShowSettings(string? page = null)
    {
        Collapse();
        if (_settings is not { IsLoaded: true })
        {
            _settings = new SettingsWindow();
            _settings.Show();
        }
        if (page != null) _settings.Navigate(page);
        _settings.Activate();
    }
}
