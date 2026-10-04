using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Notchify.Controls;
using Notchify.Core;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>System Stats: live CPU, memory, network and disk — only sampled while visible.</summary>
public sealed class StatsModule : NotchModule
{
    public override string Id => "stats";
    public override string Title => "System";
    public override string Glyph => Glyphs.Chart;
    public override string Description => "iStat-style CPU, memory, network and disk readouts.";

    public override FrameworkElement CreateView()
    {
        var st = Notch.Stats;
        (Border card, TextBlock value, TextBlock sub, Sparkline line) Tile(string label, string glyph)
        {
            var value = Text("—", "Title", 20);
            var sub = Text("", "Caption");
            var line = new Sparkline { Height = 36, Margin = new Thickness(0, 6, 0, 0) };
            line.SetResourceReference(Sparkline.StrokeProperty, "AccentBrush");
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Icon(glyph, 12));
            var l = Text(label, "Caption");
            l.Margin = new Thickness(6, 0, 0, 0);
            head.Children.Add(l);
            var card = Card(new StackPanel { Children = { head, value, sub, line } }, new Thickness(0, 0, 8, 8));
            return (card, value, sub, line);
        }

        var cpu = Tile("CPU", Glyphs.Chart);
        var mem = Tile("Memory", Glyphs.Apps);
        var net = Tile("Network", Glyphs.Network);
        var disk = Tile("Disk", Glyphs.Save);
        net.line.Visibility = Visibility.Collapsed;
        disk.line.Visibility = Visibility.Collapsed;

        var grid = new UniformGrid { Columns = 2 };
        grid.Children.Add(cpu.card);
        grid.Children.Add(mem.card);
        grid.Children.Add(net.card);
        grid.Children.Add(disk.card);

        void Update()
        {
            cpu.value.Text = $"{st.Cpu:0}%";
            cpu.sub.Text = $"{Environment.ProcessorCount} logical cores · up {st.Uptime}";
            cpu.line.Values = st.CpuHistory; cpu.line.Refresh();
            mem.value.Text = $"{st.Memory:0}%";
            mem.sub.Text = st.MemoryText;
            mem.line.Values = st.MemHistory; mem.line.Refresh();
            net.value.Text = "↓ " + st.Download;
            net.sub.Text = "↑ " + st.Upload;
            disk.value.Text = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
            disk.sub.Text = st.Disk;
        }

        grid.IsVisibleChanged += (_, _) =>
        {
            if (grid.IsVisible) { st.Updated += Update; st.Acquire(); }
            else { st.Updated -= Update; st.Release(); }
        };
        return grid;
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Open Task Manager", Glyphs.Chart, () => Ui.OpenUrl("taskmgr.exe"), null, "processes cpu memory"),
    };
}
