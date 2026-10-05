using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchify.Core;
using Notchify.Shell;
using WinForms = System.Windows.Forms;

namespace Notchify;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private WinForms.NotifyIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--restart" (Settings › Quit › Restart) waits for the old instance to exit first.
        _singleInstance = new Mutex(false, @"Local\NotchX.SingleInstance");
        bool first;
        try { first = _singleInstance.WaitOne(e.Args.Contains("--restart") ? TimeSpan.FromSeconds(8) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { first = true; }
        // An older "Notchify" build still running would fight over the notch; leave it be and don't start twice.
        using var legacy = new Mutex(false, @"Local\Notchify.SingleInstance", out var legacyFree);
        if (first && !legacyFree)
            MessageBox.Show("The old Notchify app is still running. Quit it from its tray icon, then start NotchX again.", AppInfo.Name);
        if (!first || !legacyFree)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true; // a bug in one feature should never take the notch down
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task", ex.Exception); ex.SetObserved(); };

        SettingsStore.Load();
        Notch.Clipboard.Load();
        Notch.Shelf.Load();
        Notch.Modules.Initialize();

        var window = new NotchWindow();
        Notch.Shell = window;
        MainWindow = window;
        window.Show();

        // Core services shared by many features.
        Notch.Audio.Start();
        Notch.Battery.Start();
        Notch.Brightness.Start();
        Notch.ScreenTime.Start();
        Notch.Spaces.Start();
        await Notch.Media.StartAsync();

        Notch.Modules.StartEnabled();
        RegisterHotkeys();
        SetupTray();
        ApplyStartup();
        SettingsStore.Changed += () => { RegisterHotkeys(); ApplyStartup(); UpdateTray(); };

        Log.Info($"{AppInfo.Name} {AppInfo.Version} started");

        // Developer helper: NotchX.exe --readme-shots <folder> saves fresh screenshots for the README.
        var shots = Array.IndexOf(e.Args, "--readme-shots");
        if (shots >= 0 && shots + 1 < e.Args.Length) _ = window.SaveReadmeShotsAsync(e.Args[shots + 1]);
    }

    /// <summary>Start a fresh copy and quit this one.</summary>
    public static void Restart()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Error("restart", ex); return; }
        Current.Shutdown();
    }

    private void RegisterHotkeys()
    {
        Notch.Hotkeys.UnregisterAll();
        var b = SettingsStore.Current.Behavior;
        Notch.Hotkeys.Register(b.PaletteHotkey, CommandPalette.Toggle);
        Notch.Hotkeys.Register(b.ToggleHotkey, () => Notch.Shell.Toggle());
        Notch.Hotkeys.Register(SettingsStore.Current.Capture.Hotkey, Modules.ToolsModule.ScreenCapture);
    }

    private static void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            // The old name's entry points at the old exe; replace it.
            if (key.GetValue("Notchify") != null) key.DeleteValue("Notchify");
            if (SettingsStore.Current.Behavior.StartWithWindows)
                key.SetValue(AppInfo.Name, $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue(AppInfo.Name) != null)
                key.DeleteValue(AppInfo.Name);
        }
        catch (Exception ex) { Log.Error("startup registration", ex); }
    }

    // ---------------- Tray icon (Windows' equivalent of the menu bar extra) ----------------

    private WinForms.ToolStripMenuItem? _caffeineItem;

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = AppInfo.Name,
            Icon = CreateTrayIcon(),
            Visible = SettingsStore.Current.Behavior.ShowTrayIcon,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"Open {AppInfo.Name}", null, (_, _) => Dispatcher.Invoke(() => Notch.Shell.Toggle()));
        menu.Items.Add("Command palette", null, (_, _) => Dispatcher.Invoke(CommandPalette.Toggle));
        _caffeineItem = new WinForms.ToolStripMenuItem("Keep awake", null, (_, _) => Dispatcher.Invoke(Notch.KeepAwake.Toggle));
        menu.Items.Add(_caffeineItem);
        var popout = new WinForms.ToolStripMenuItem("Pop out");
        foreach (var m in Notch.Modules.Tabs)
        {
            var id = m.Id;
            popout.DropDownItems.Add(m.Title, null, (_, _) => Dispatcher.Invoke(() => Notch.Shell.PopOut(id)));
        }
        menu.Items.Add(popout);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => Dispatcher.Invoke(() => Notch.Shell.ShowSettings()));
        menu.Items.Add($"Quit {AppInfo.Name}", null, (_, _) => Dispatcher.Invoke(Shutdown));
        menu.Opening += (_, _) => _caffeineItem.Checked = Notch.KeepAwake.IsActive;
        _tray.ContextMenuStrip = menu;
        // One-click Caffeine right in the tray, like Notchy's clock-side toggle.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) Dispatcher.Invoke(() => Notch.Shell.Toggle());
            else if (e.Button == WinForms.MouseButtons.Middle) Dispatcher.Invoke(Notch.KeepAwake.Toggle);
        };
    }

    private void UpdateTray()
    {
        if (_tray != null) _tray.Visible = SettingsStore.Current.Behavior.ShowTrayIcon;
    }

    /// <summary>The NotchX icon at tray size; falls back to a drawn pill if the resource can't be read.</summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/NotchX.ico"));
            if (res != null)
            {
                using var stream = res.Stream;
                return new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex) { Log.Info("tray icon: " + ex.Message); }

        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            const int x = 2, y = 9, w = 28, h = 14, r = 14;
            path.AddArc(x, y, r, h, 90, 180);
            path.AddArc(x + w - r, y, r, h, 270, 180);
            path.CloseFigure();
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.FillPath(fill, path);
            using var dot = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 10, 132, 255));
            g.FillEllipse(dot, 21, 13, 6, 6);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Notch.Modules.StopAll();
            Notch.Hotkeys.UnregisterAll();
            Notch.Input.Dispose();
            Notch.DeveloperApi.Dispose();
            Notch.Clipboard.Save();
            Notch.ScreenTime.Save();
            SettingsStore.SaveNow();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        }
        catch { }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
