using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Notchify.Core;

namespace Notchify.Services;

public sealed class AudioDevice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDefault { get; init; }
    public string Glyph => Name.Contains("Head", StringComparison.OrdinalIgnoreCase) ||
                           Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
                           Name.Contains("Buds", StringComparison.OrdinalIgnoreCase)
        ? Glyphs.Headphones
        : Name.Contains("Monitor", StringComparison.OrdinalIgnoreCase) || Name.Contains("HDMI", StringComparison.OrdinalIgnoreCase) ||
          Name.Contains("Display", StringComparison.OrdinalIgnoreCase)
            ? Glyphs.Monitor
            : Glyphs.Volume;
    public override string ToString() => Name;
}

/// <summary>One app's audio session in the mixer.</summary>
public sealed class AppAudioSession : ObservableObject
{
    private readonly AudioSessionControl _control;

    public AppAudioSession(AudioSessionControl control)
    {
        _control = control;
        Pid = (int)control.GetProcessID;
        try
        {
            var p = Process.GetProcessById(Pid);
            ProcessName = p.ProcessName;
            Name = !string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.MainWindowTitle.Length < 30
                ? p.MainWindowTitle
                : FileVersionInfo.GetVersionInfo(p.MainModule?.FileName ?? "").FileDescription ?? p.ProcessName;
            if (string.IsNullOrWhiteSpace(Name)) Name = p.ProcessName;
            Icon = Ui.FileIcon(p.MainModule?.FileName ?? "");
        }
        catch
        {
            Name = control.IsSystemSoundsSession ? "System sounds" : (control.DisplayName ?? $"PID {Pid}");
            ProcessName = Name;
        }
        if (control.IsSystemSoundsSession) Name = "System sounds";
    }

    public int Pid { get; }
    public string Name { get; } = "";
    public string ProcessName { get; } = "";
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon != null;

    public double Volume
    {
        get { try { return _control.SimpleAudioVolume.Volume * 100; } catch { return 0; } }
        set { try { _control.SimpleAudioVolume.Volume = (float)Math.Clamp(value / 100, 0, 1); Raise(); } catch { } }
    }

    public bool Muted
    {
        get { try { return _control.SimpleAudioVolume.Mute; } catch { return false; } }
        set { try { _control.SimpleAudioVolume.Mute = value; Raise(); Raise(nameof(MuteGlyph)); } catch { } }
    }

    public string MuteGlyph => Muted ? Glyphs.Mute : Glyphs.Volume;
}

/// <summary>
/// Core Audio: master volume, output switching, per-app volume (Sound Mixer) and a global mic mute.
/// </summary>
public sealed class AudioService : ObservableObject, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _output;
    private bool _suppressHud;

    /// <summary>Raised when the master volume changes (value 0..1, muted).</summary>
    public event Action<double, bool>? VolumeChanged;

    public ObservableCollection<AudioDevice> Outputs { get; } = new();
    public ObservableCollection<AppAudioSession> Sessions { get; } = new();

    public void Start()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        AttachDefault();
        RefreshDevices();
    }

    private void AttachDefault()
    {
        try
        {
            if (_output != null) _output.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
            _output = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _output.AudioEndpointVolume.OnVolumeNotification += OnVolume;
            Raise(nameof(Volume));
            Raise(nameof(Muted));
            Raise(nameof(OutputName));
        }
        catch (Exception ex)
        {
            _output = null;
            Log.Error("No default audio output", ex);
        }
    }

    private void OnVolume(AudioVolumeNotificationData data) => Ui.Post(() =>
    {
        Raise(nameof(Volume));
        Raise(nameof(Muted));
        Raise(nameof(VolumeGlyph));
        if (!_suppressHud) VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
    });

    public string OutputName => _output?.FriendlyName ?? "No output";

    public double Volume
    {
        get { try { return (_output?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0) * 100; } catch { return 0; } }
        set
        {
            if (_output == null) return;
            _suppressHud = true;
            try { _output.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(value / 100, 0, 1); }
            catch { }
            finally { Ui.Post(() => _suppressHud = false); }
        }
    }

    public bool Muted
    {
        get { try { return _output?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
        set { try { if (_output != null) _output.AudioEndpointVolume.Mute = value; } catch { } }
    }

    public string VolumeGlyph => Muted || Volume < 1 ? Glyphs.Mute : Glyphs.Volume;

    /// <summary>Step the volume (used by the volume keys when Notchify replaces the stock flyout).</summary>
    public void Step(double delta)
    {
        if (_output == null) return;
        try
        {
            var v = Math.Clamp(_output.AudioEndpointVolume.MasterVolumeLevelScalar + delta, 0, 1);
            _output.AudioEndpointVolume.MasterVolumeLevelScalar = (float)v;
            if (_output.AudioEndpointVolume.Mute && delta > 0) _output.AudioEndpointVolume.Mute = false;
        }
        catch { }
    }

    public void ToggleMute() => Muted = !Muted;

    public void RefreshDevices()
    {
        Outputs.Clear();
        try
        {
            var defId = _output?.ID;
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                Outputs.Add(new AudioDevice { Id = d.ID, Name = d.FriendlyName, IsDefault = d.ID == defId });
        }
        catch (Exception ex) { Log.Error("enumerate outputs", ex); }
    }

    public void SetDefaultOutput(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
            policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            policy.SetDefaultEndpoint(deviceId, ERole.eCommunications);
        }
        catch (Exception ex) { Log.Error("set default output", ex); }
    }

    public void RefreshSessions()
    {
        Sessions.Clear();
        if (_output == null) return;
        try
        {
            var mgr = _output.AudioSessionManager;
            mgr.RefreshSessions();
            var seen = new HashSet<int>();
            for (var i = 0; i < mgr.Sessions.Count; i++)
            {
                var s = mgr.Sessions[i];
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                var vm = new AppAudioSession(s);
                if (!seen.Add(vm.Pid) && vm.Pid != 0) continue;
                Sessions.Add(vm);
            }
        }
        catch (Exception ex) { Log.Error("sessions", ex); }
    }

    public AppAudioSession? FindSession(string processName)
    {
        if (Sessions.Count == 0) RefreshSessions();
        return Sessions.FirstOrDefault(s => s.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Microphone ----------

    /// <summary>System-wide mic mute: mutes every active capture device so every meeting app goes silent at once.</summary>
    public bool MicMuted
    {
        get
        {
            try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).AudioEndpointVolume.Mute; }
            catch { return false; }
        }
        set
        {
            try
            {
                foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    d.AudioEndpointVolume.Mute = value;
            }
            catch (Exception ex) { Log.Error("mic mute", ex); }
            Raise();
            Raise(nameof(MicGlyph));
        }
    }

    public string MicGlyph => MicMuted ? Glyphs.MicOff : Glyphs.Mic;
    public bool HasMicrophone
    {
        get
        {
            try { return _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count > 0; }
            catch { return false; }
        }
    }

    // ---------- IMMNotificationClient ----------
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Ui.Post(RefreshDevices);
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Ui.Post(RefreshDevices);
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Ui.Post(RefreshDevices);
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia) return;
        Ui.Post(() =>
        {
            AttachDefault();
            RefreshDevices();
            RefreshSessions();
            if (SettingsStore.Current.Huds.OutputSwitchIsland)
                Notch.Hub.Notify(Glyphs.Headphones, OutputName, "Sound output", Ui.Accent, IslandPriority.Low, 2, "audio-output");
        });
    }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    // ---------- Undocumented-but-stable IPolicyConfig used by every audio switcher ----------
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(string a, bool b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(string a);
        [PreserveSig] int SetDeviceFormat(string a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(string a, bool b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(string a, IntPtr b);
        [PreserveSig] int GetShareMode(string a, IntPtr b);
        [PreserveSig] int SetShareMode(string a, IntPtr b);
        [PreserveSig] int GetPropertyValue(string a, bool b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(string a, bool b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(string a, bool b);
    }
}
