using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>AirPods manager (per-bud + case battery via BLE) and battery for every paired Bluetooth device.</summary>
public sealed class DevicesModule : NotchModule
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(5) };

    public DevicesModule() => _refresh.Tick += (_, _) => _ = Notch.Bluetooth.RefreshDevicesAsync();

    public override string Id => "devices";
    public override string Title => "Devices";
    public override string Glyph => Glyphs.Bluetooth;
    public override string Description => "AirPods detection with per-bud battery, plus battery for mice, keyboards and headphones.";

    protected override void Start()
    {
        _refresh.Start();
        _ = Notch.Bluetooth.RefreshDevicesAsync();
        if (SettingsStore.Current.Huds.AirPodsPopup) Notch.Bluetooth.AcquireScan();
    }

    protected override void Stop()
    {
        _refresh.Stop();
        if (SettingsStore.Current.Huds.AirPodsPopup) Notch.Bluetooth.ReleaseScan();
    }

    public override FrameworkElement CreateView()
    {
        var bt = Notch.Bluetooth;
        var podsCard = new Border { Style = S("Card"), Margin = new Thickness(0, 0, 0, 8) };
        var devices = new ListBox { ItemsSource = bt.Devices };
        devices.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <DockPanel>
    <TextBlock DockPanel.Dock='Left' Text='{Binding Glyph}' Style='{DynamicResource Icon}' Width='26' />
    <TextBlock DockPanel.Dock='Right' Text='{Binding BatteryGlyph}' Style='{DynamicResource Icon}' FontSize='16' Margin='6,0,0,0' />
    <TextBlock DockPanel.Dock='Right' Text='{Binding Text}' Style='{DynamicResource Body}' VerticalAlignment='Center' />
    <TextBlock Text='{Binding Name}' Style='{DynamicResource Body}' VerticalAlignment='Center' />
  </DockPanel>
</DataTemplate>");
        var empty = Text("No Bluetooth devices are reporting battery", "Caption");
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Margin = new Thickness(0, 10, 0, 0);

        void RenderPods()
        {
            var p = bt.AirPods;
            if (p == null || !bt.HasAirPods)
            {
                podsCard.Child = Columns(
                    (Icon(Glyphs.Headphones, 22), Px(40)),
                    (new StackPanel { Children = { Text("AirPods", "Title"), Text("Open the case near your PC to see battery", "Caption") } }, Star()),
                    (Chip("Bluetooth settings", (_, _) => BluetoothService.OpenBluetoothSettings(), Glyphs.Bluetooth), Auto));
                return;
            }
            UIElement Bud(string label, string value, bool charging)
            {
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(Text(label, "Caption"));
                var t = Text(value + (charging ? " ⚡" : ""), "Title", 18);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(t);
                return sp;
            }
            podsCard.Child = Columns(
                (new StackPanel { Children = { Text(p.Model, "Title"), Text(p.LidOpen ? "Case open" : "Nearby", "Caption") } }, Star(1.2)),
                (Bud("Left", p.LeftText, p.LeftCharging), Star()),
                (Bud("Right", p.RightText, p.RightCharging), Star()),
                (Bud("Case", p.CaseText, p.CaseCharging), Star()),
                (Chip("Connect", (_, _) => BluetoothService.OpenBluetoothSettings(), Glyphs.Bluetooth, accent: true), Auto));
        }

        bt.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(BluetoothService.AirPods)) RenderPods(); };
        bt.Devices.CollectionChanged += (_, _) => empty.Visibility = bt.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderPods();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var refresh = IconButton(Glyphs.Refresh, "Refresh", (_, _) => _ = bt.RefreshDevicesAsync());
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(Text("BLUETOOTH DEVICES", "SectionHeader"));

        var dock = new DockPanel();
        DockPanel.SetDock(podsCard, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(empty, Dock.Top);
        dock.Children.Add(podsCard);
        dock.Children.Add(header);
        dock.Children.Add(empty);
        dock.Children.Add(devices);
        // Scan for AirPods only while this tab is on screen (unless the popup is enabled).
        dock.IsVisibleChanged += (_, _) =>
        {
            if (dock.IsVisible) { bt.AcquireScan(); _ = bt.RefreshDevicesAsync(); }
            else bt.ReleaseScan();
        };
        return dock;
    }
}
