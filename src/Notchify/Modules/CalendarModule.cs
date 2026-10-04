using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

public sealed class Reminder : ObservableObject
{
    private bool _done;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime? Due { get; set; }
    public bool Notified { get; set; }
    public bool Done { get => _done; set => Set(ref _done, value); }
    public string DueText => Due is { } d ? (d.Date == DateTime.Today ? d.ToString("t") : d.ToString("ddd d MMM, t")) : "";
    public bool Overdue => Due < DateTime.Now && !Done;
}

/// <summary>Calendar (iCal subscriptions) and Reminders with natural-language due times.</summary>
public sealed class CalendarModule : NotchModule
{
    private readonly DispatcherTimer _check = new() { Interval = TimeSpan.FromSeconds(20) };
    public ObservableCollection<Reminder> Reminders { get; } = new();

    public CalendarModule() => _check.Tick += (_, _) => CheckDue();

    public override string Id => "calendar";
    public override string Title => "Calendar";
    public override string Glyph => Glyphs.Calendar;
    public override string Description => "Upcoming events from Google, Outlook/Exchange or iCloud (iCal links), plus reminders.";

    protected override void Start()
    {
        Reminders.Clear();
        foreach (var r in JsonStore.Load<List<Reminder>>("reminders")) Reminders.Add(r);
        Notch.Calendar.Start();
        _check.Start();
    }

    protected override void Stop()
    {
        Notch.Calendar.Stop();
        _check.Stop();
        Notch.Hub.Remove("calendar");
    }

    internal void Save() => JsonStore.Save("reminders", Reminders.ToList());

    public void AddReminder(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        var (text, due) = ParseReminder(input.Trim());
        Reminders.Add(new Reminder { Text = text, Due = due });
        Sort();
        Save();
        Notch.Hub.Notify(Glyphs.Bell, "Reminder added", due is { } d ? $"{text} · {d:ddd t}" : text, Ui.Accent, IslandPriority.Low, 2);
    }

    private void Sort()
    {
        var sorted = Reminders.OrderBy(r => r.Done).ThenBy(r => r.Due ?? DateTime.MaxValue).ToList();
        Reminders.Clear();
        foreach (var r in sorted) Reminders.Add(r);
    }

    private void CheckDue()
    {
        foreach (var r in Reminders.Where(r => !r.Done && !r.Notified && r.Due <= DateTime.Now).ToList())
        {
            r.Notified = true;
            Save();
            var rem = r;
            System.Media.SystemSounds.Asterisk.Play();
            Notch.Hub.Show(new Island
            {
                Glyph = Glyphs.Bell,
                Title = rem.Text,
                Message = "Reminder",
                Accent = Ui.Orange,
                Sticky = true,
                Priority = IslandPriority.High,
                Actions =
                {
                    new IslandAction("Snooze 10m", () => { rem.Due = DateTime.Now.AddMinutes(10); rem.Notified = false; Save(); }),
                    new IslandAction("Done", () => { rem.Done = true; Sort(); Save(); }, true),
                },
            });
        }
    }

    /// <summary>"Call mum in 20m", "Standup at 9:30", "Pay rent tomorrow 9am", "Dentist fri 3pm".</summary>
    public static (string Text, DateTime? Due) ParseReminder(string s)
    {
        var m = Regex.Match(s, @"\s+in\s+(\d+)\s*(m|min|mins|minutes|h|hr|hrs|hours|d|days?)\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var n = int.Parse(m.Groups[1].Value);
            var unit = m.Groups[2].Value.ToLowerInvariant()[0];
            var due = unit == 'h' ? DateTime.Now.AddHours(n) : unit == 'd' ? DateTime.Now.AddDays(n) : DateTime.Now.AddMinutes(n);
            return (s.Remove(m.Index, m.Length).Trim(), due);
        }

        var day = DateTime.Today;
        var text = s;
        var dm = Regex.Match(text, @"\s+(today|tomorrow|mon|tue|wed|thu|fri|sat|sun)[a-z]*\b", RegexOptions.IgnoreCase);
        var hasDay = dm.Success;
        if (dm.Success)
        {
            var w = dm.Groups[1].Value.ToLowerInvariant();
            if (w == "tomorrow") day = day.AddDays(1);
            else if (w != "today")
            {
                var target = Enum.GetValues<DayOfWeek>().First(d => d.ToString().StartsWith(w, StringComparison.OrdinalIgnoreCase));
                var delta = ((int)target - (int)day.DayOfWeek + 7) % 7;
                day = day.AddDays(delta == 0 ? 7 : delta);
            }
            text = text.Remove(dm.Index, dm.Length);
        }

        var tm = Regex.Match(text, @"\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b", RegexOptions.IgnoreCase);
        if (tm.Success && (tm.Groups[3].Success || tm.Groups[2].Success || text.Contains(" at ", StringComparison.OrdinalIgnoreCase)))
        {
            var h = int.Parse(tm.Groups[1].Value);
            var min = tm.Groups[2].Success ? int.Parse(tm.Groups[2].Value) : 0;
            var ampm = tm.Groups[3].Value.ToLowerInvariant();
            if (ampm == "pm" && h < 12) h += 12;
            if (ampm == "am" && h == 12) h = 0;
            if (h < 24 && min < 60)
            {
                var due = day.AddHours(h).AddMinutes(min);
                if (!hasDay && due < DateTime.Now) due = due.AddDays(1);
                return (text.Remove(tm.Index, tm.Length).Trim(), due);
            }
        }
        return (text.Trim(), hasDay ? day.AddHours(9) : null);
    }

    public override FrameworkElement CreateView()
    {
        // Left: events
        var events = new ListBox { ItemsSource = Notch.Calendar.Upcoming };
        events.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
              xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
              xmlns:c='clr-namespace:Notchify.Controls;assembly=NotchX'>
  <DockPanel>
    <TextBlock DockPanel.Dock='Left' Width='64' Text='{Binding DayText}' Style='{DynamicResource Caption}' VerticalAlignment='Center' />
    <Button DockPanel.Dock='Right' Content='Join' Tag='{Binding JoinUrl}' Style='{DynamicResource ChipButton}' Padding='8,2'
            Visibility='{Binding HasJoin, Converter={x:Static c:Converters.Visible}}' />
    <StackPanel>
      <TextBlock Text='{Binding Title}' Style='{DynamicResource Body}' />
      <TextBlock Text='{Binding TimeText}' Style='{DynamicResource Caption}' />
    </StackPanel>
  </DockPanel>
</DataTemplate>");
        events.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Button { Tag: string url }) Ui.OpenUrl(url);
        }));
        var status = Text("", "Caption");
        status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CalendarService.Status)) { Source = Notch.Calendar });
        var refresh = IconButton(Glyphs.Refresh, "Refresh", (_, _) => _ = Notch.Calendar.RefreshAsync());
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(new StackPanel { Children = { Text("UPCOMING", "SectionHeader"), status } });
        var left = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        left.Children.Add(header);
        left.Children.Add(events);

        // Right: reminders
        var input = new TextBox { Tag = "Remind me… e.g. “Call Sam tomorrow 9am”" };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            AddReminder(input.Text);
            input.Clear();
            e.Handled = true;
        };
        var list = new ListBox { ItemsSource = Reminders, Margin = new Thickness(0, 8, 0, 0) };
        list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <DockPanel>
    <CheckBox DockPanel.Dock='Right' Style='{DynamicResource Switch}' IsChecked='{Binding Done, Mode=TwoWay}' />
    <StackPanel>
      <TextBlock Text='{Binding Text}' Style='{DynamicResource Body}' TextWrapping='Wrap' />
      <TextBlock Text='{Binding DueText}' Style='{DynamicResource Caption}' />
    </StackPanel>
  </DockPanel>
</DataTemplate>");
        list.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => { Save(); }));
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && list.SelectedItem is Reminder r) { Reminders.Remove(r); Save(); }
        };
        var right = new DockPanel();
        var rh = Text("REMINDERS", "SectionHeader");
        DockPanel.SetDock(rh, Dock.Top);
        DockPanel.SetDock(input, Dock.Top);
        right.Children.Add(rh);
        right.Children.Add(input);
        right.Children.Add(list);

        return Columns((left, Star(1.2)), (new Border(), Px(14)), (right, Star(1)));
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("New reminder", Glyphs.Bell, () => Notch.Shell.OpenTab(Id), null, "todo task remind"),
        new PaletteCommand("Refresh calendar", Glyphs.Calendar, () => _ = Notch.Calendar.RefreshAsync(), null, "events"),
    };
}
