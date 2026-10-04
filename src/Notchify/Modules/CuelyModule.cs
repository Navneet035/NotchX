using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notchify.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>
/// Cuely: a teleprompter that scrolls your script right beneath the webcam (the notch sits at the top-centre,
/// exactly where the camera is), plus a Camera Mirror to check yourself before a call.
/// </summary>
public sealed class CuelyModule : NotchModule
{
    public override string Id => "cuely";
    public override string Title => "Cuely";
    public override string Glyph => Glyphs.Reading;
    public override string Description => "Teleprompter under your webcam for video calls, and a quick camera mirror.";

    public override FrameworkElement CreateView()
    {
        var script = JsonStore.Load<Dictionary<string, string>>("cuely").GetValueOrDefault("script",
            "Paste your script here.\n\nPress Play and it will scroll right under your webcam, so your eyes stay on the camera.");

        // Editor
        var editor = new TextBox { Text = script, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        editor.TextChanged += (_, _) => JsonStore.Save("cuely", new Dictionary<string, string> { ["script"] = editor.Text });

        // Prompter
        var prompt = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 26, TextAlignment = TextAlignment.Center, LineHeight = 36, Margin = new Thickness(0, 60, 0, 200) };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var scroller = new ScrollViewer { Content = prompt, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Visibility = Visibility.Collapsed };
        var mirrorTransform = new ScaleTransform(1, 1);
        prompt.LayoutTransform = mirrorTransform;

        // Speed, size and mirror are remembered (Settings › Cuely).
        var cs = SettingsStore.Current.Cuely;
        var speed = new Slider { Minimum = 5, Maximum = 120, Value = cs.Speed, Width = 110, ToolTip = "Speed" };
        speed.ValueChanged += (_, _) => { cs.Speed = speed.Value; SettingsStore.Save(); };
        var size = new Slider { Minimum = 14, Maximum = 54, Value = cs.TextSize, Width = 90, ToolTip = "Text size" };
        void ApplySize() { prompt.FontSize = size.Value; prompt.LineHeight = size.Value * 1.4; }
        size.ValueChanged += (_, _) => { ApplySize(); cs.TextSize = size.Value; SettingsStore.Save(); };
        ApplySize();
        var mirror = new ToggleButton { Style = S("IconToggle"), Content = "⇋", ToolTip = "Mirror text (for beam-splitter rigs)", IsChecked = cs.Mirror };
        mirrorTransform.ScaleX = cs.Mirror ? -1 : 1;
        mirror.Click += (_, _) =>
        {
            cs.Mirror = mirror.IsChecked == true;
            mirrorTransform.ScaleX = cs.Mirror ? -1 : 1;
            SettingsStore.Save();
        };

        Button? playBtn = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var last = DateTime.Now;
        timer.Tick += (_, _) =>
        {
            var dt = (DateTime.Now - last).TotalSeconds;
            last = DateTime.Now;
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + speed.Value * dt);
            if (scroller.VerticalOffset >= scroller.ScrollableHeight && scroller.ScrollableHeight > 0) Play(false);
        };

        void Play(bool on)
        {
            if (on)
            {
                prompt.Text = editor.Text;
                editor.Visibility = Visibility.Collapsed;
                scroller.Visibility = Visibility.Visible;
                scroller.ScrollToTop();
                last = DateTime.Now;
                timer.Start();
                Notch.Shell.KeepOpen = true; // don't auto-collapse mid-sentence
            }
            else
            {
                timer.Stop();
                Notch.Shell.KeepOpen = false;
            }
            playBtn!.Content = on ? Glyphs.Pause : Glyphs.Play;
        }
        playBtn = IconButton(Glyphs.Play, "Play / pause", (_, _) => Play(!timer.IsEnabled));
        var edit = IconButton(Glyphs.Edit, "Edit script", (_, _) =>
        {
            Play(false);
            scroller.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
        });
        // Mouse wheel nudges the script while playing.
        scroller.PreviewMouseWheel += (_, e) => { scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 3.0); e.Handled = true; };

        var camera = new CameraMirror();
        var camToggle = new ToggleButton { Style = S("IconToggle"), Content = Glyphs.Camera, ToolTip = "Camera mirror" };
        camToggle.Click += (_, _) => { if (camToggle.IsChecked == true) camera.Start(); else camera.Stop(); };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(playBtn);
        bar.Children.Add(edit);
        bar.Children.Add(mirror);
        bar.Children.Add(Icon(Glyphs.Speed, 12));
        bar.Children.Add(speed);
        bar.Children.Add(Icon(Glyphs.Text, 12));
        bar.Children.Add(size);
        bar.Children.Add(camToggle);
        foreach (FrameworkElement c in bar.Children) c.Margin = new Thickness(0, 0, 6, 0);

        var left = new Grid();
        left.Children.Add(editor);
        left.Children.Add(scroller);
        var leftDock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        leftDock.Children.Add(bar);
        leftDock.Children.Add(left);

        var root = Columns((leftDock, Star(1.6)), (new Border(), Px(10)), (camera, Auto));
        root.IsVisibleChanged += (_, _) =>
        {
            if (root.IsVisible) return;
            Play(false);
            camera.Stop();
            camToggle.IsChecked = false;
        };
        return root;
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Open teleprompter", Glyphs.Reading, () => Notch.Shell.OpenTab(Id), null, "cuely prompter script zoom"),
        new PaletteCommand("Camera mirror", Glyphs.Camera, () => Notch.Shell.OpenTab(Id), null, "webcam check appearance"),
    };
}

/// <summary>Local webcam preview via Windows.Media.Capture. Nothing is recorded or sent anywhere.</summary>
public sealed class CameraMirror : Border
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private WriteableBitmap? _bitmap;
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private int _busy;

    /// <summary>In the Cuely tab the mirror slides open and shut; as a Home card it keeps its size.</summary>
    public bool Collapsible { get; init; } = true;

    public bool IsRunning => _capture != null;

    public CameraMirror(bool collapsible = true)
    {
        Collapsible = collapsible;
        if (Collapsible) Width = 0;
        CornerRadius = new CornerRadius(14);
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _image.RenderTransformOrigin = new Point(0.5, 0.5);
        _image.RenderTransform = new ScaleTransform(-1, 1); // mirror, like looking in a glass
        _status.Style = S("Caption");
        Child = new Grid { Children = { _image, _status } };
    }

    public async void Start()
    {
        if (_capture != null) return;
        if (Collapsible) Width = 200;
        _status.Text = "Starting camera…";
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var group = groups.FirstOrDefault(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color));
            if (group == null) { _status.Text = "No camera found"; return; }
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
            var info = group.SourceInfos.First(i => i.SourceKind == MediaFrameSourceKind.Color);
            _reader = await _capture.CreateFrameReaderAsync(_capture.FrameSources[info.Id], Windows.Media.MediaProperties.MediaEncodingSubtypes.Bgra8);
            _reader.FrameArrived += OnFrame;
            await _reader.StartAsync();
            _status.Text = "";
        }
        catch (UnauthorizedAccessException)
        {
            _status.Text = "Allow camera access for desktop apps\nin Windows Settings › Privacy";
        }
        catch (Exception ex)
        {
            _status.Text = "Camera unavailable";
            Log.Info("camera: " + ex.Message);
        }
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // drop frames while the UI is still drawing the last one
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var src = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (src == null) { _busy = 0; return; }
            using var bmp = SoftwareBitmap.Convert(src, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            var bytes = new byte[w * h * 4];
            bmp.CopyToBuffer(bytes.AsBuffer());
            Ui.Post(() =>
            {
                if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                {
                    _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                    _image.Source = _bitmap;
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, w, h), bytes, w * 4, 0);
                _busy = 0;
            });
        }
        catch { _busy = 0; }
    }

    public async void Stop()
    {
        if (Collapsible) Width = 0;
        try
        {
            if (_reader != null) { _reader.FrameArrived -= OnFrame; await _reader.StopAsync(); _reader.Dispose(); }
            _capture?.Dispose();
        }
        catch { }
        _reader = null;
        _capture = null;
        _image.Source = null;
        _bitmap = null;
    }
}
