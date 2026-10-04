using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Notchify.Core;

namespace Notchify.Shell;

/// <summary>
/// A tab popped out of the notch into its own floating glass panel. Pin it on top like a menu-bar
/// popover, or "Pin to desktop" so it sits behind your windows like a desktop widget.
/// </summary>
public sealed class DetachedWindow : Window
{
    private readonly NotchModule _module;

    public DetachedWindow(NotchModule module)
    {
        _module = module;
        Title = $"{module.Title} — NotchX";
        Icon = AppInfo.Icon;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 380;
        Height = 420;
        FontFamily = (FontFamily)FindResource("UiFont");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var saved = JsonStore.Load<Dictionary<string, double[]>>("popouts");
        if (saved.TryGetValue(module.Id, out var r) && r.Length == 4)
        {
            Left = r[0]; Top = r[1]; Width = r[2]; Height = r[3];
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var title = new TextBlock { Text = module.Title, Style = (Style)FindResource("Title"), VerticalAlignment = VerticalAlignment.Center };
        var glyph = new TextBlock { Text = module.Glyph, Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 8, 0) };

        var pinTop = new ToggleButton { Style = (Style)FindResource("IconToggle"), Content = Glyphs.Pin, ToolTip = "Always on top", IsChecked = true };
        pinTop.Click += (_, _) => Topmost = pinTop.IsChecked == true;
        var desktop = new ToggleButton { Style = (Style)FindResource("IconToggle"), Content = Glyphs.Monitor, ToolTip = "Pin to desktop (widget mode)" };
        desktop.Click += (_, _) =>
        {
            var on = desktop.IsChecked == true;
            Topmost = !on && pinTop.IsChecked == true;
            if (on) SendToBottom();
        };
        var back = new Button { Style = (Style)FindResource("IconButton"), Content = Glyphs.Up, ToolTip = "Return to notch" };
        back.Click += (_, _) => { Close(); Notch.Shell.OpenTab(module.Id); };
        var close = new Button { Style = (Style)FindResource("IconButton"), Content = Glyphs.Close, ToolTip = "Close" };
        close.Click += (_, _) => Close();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new UIElement[] { desktop, pinTop, back, close }) buttons.Children.Add(b);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, title } });
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var content = new ContentControl { Content = module.CreateView(), Focusable = false };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(header);
        Grid.SetRow(content, 1);
        grid.Children.Add(content);

        var panel = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 10, 14, 14),
            Margin = new Thickness(10),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("GlassRimBrush"),
            Child = grid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.5 },
        };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        Content = panel;

        Closing += (_, _) =>
        {
            var all = JsonStore.Load<Dictionary<string, double[]>>("popouts");
            all[_module.Id] = new[] { Left, Top, Width, Height };
            JsonStore.Save("popouts", all);
        };
    }

    private void SendToBottom()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
