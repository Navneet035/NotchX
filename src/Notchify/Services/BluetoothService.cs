using System.Collections.ObjectModel;
using Notchify.Core;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace Notchify.Services;

public sealed class DeviceBattery
{
    public string Name { get; init; } = "";
    public int Percent { get; init; }
    public bool Connected { get; init; }
    public string Glyph => Name.Contains("Mouse", StringComparison.OrdinalIgnoreCase) ? Glyphs.Mouse :
        Name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ? Glyphs.Keyboard :
        Name.Contains("Pods", StringComparison.OrdinalIgnoreCase) || Name.Contains("Buds", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Head", StringComparison.OrdinalIgnoreCase) || Name.Contains("WH-", StringComparison.OrdinalIgnoreCase) ? Glyphs.Headphones :
        Glyphs.Bluetooth;
    public string BatteryGlyph => Glyphs.Battery(Percent, false);
    public string Text => $"{Percent}%";
}

/// <summary>A paired Bluetooth device for the Home widget: connection state plus battery when Windows knows it.</summary>
public sealed class PairedDevice
{
    public string Name { get; init; } = "";
    public bool Connected { get; init; }
    public int? Percent { get; init; }
    public string Glyph => new DeviceBattery { Name = Name }.Glyph;
    public string Status => Connected ? (Percent is { } p ? $"{p}%" : "Connected") : "Not connected";
    public string BatteryGlyph => Percent is { } p ? Glyphs.Battery(p, false) : "";
    public double Opacity => Connected ? 1 : 0.5;
}

public sealed class AirPodsStatus : ObservableObject
{
    public string Model { get; set; } = "AirPods";
    public int? Left { get; set; }
    public int? Right { get; set; }
    public int? Case { get; set; }
    public bool LeftCharging { get; set; }
    public bool RightCharging { get; set; }
    public bool CaseCharging { get; set; }
    public bool LidOpen { get; set; }
    public short Rssi { get; set; }
    public DateTime Seen { get; set; }
    public string LeftText => Left is { } l ? $"{l}%" : "—";
    public string RightText => Right is { } r ? $"{r}%" : "—";
    public string CaseText => Case is { } c ? $"{c}%" : "—";
    public void Changed() => Raise(string.Empty);
}

/// <summary>
/// Battery for every paired Bluetooth device Windows knows the level of (the same value Settings shows),
/// plus AirPods detection by parsing Apple's proximity-pairing BLE advertisements — per-bud and case battery.
/// </summary>
public sealed class BluetoothService : ObservableObject
{
    // DEVPKEY_Bluetooth_Battery
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ConnectedKey = "System.Devices.Connected";

    private BluetoothLEAdvertisementWatcher? _watcher;
    private int _scanUsers;
    private DateTime _lastPopup;

    public ObservableCollection<DeviceBattery> Devices { get; } = new();
    /// <summary>Every paired device (Classic and LE), connected ones first.</summary>
    public ObservableCollection<PairedDevice> Paired { get; } = new();
    public AirPodsStatus? AirPods { get; private set; }

    private const string AepConnectedKey = "System.Devices.Aep.IsConnected";

    public async Task RefreshPairedAsync()
    {
        try
        {
            await RefreshDevicesAsync(); // battery levels, matched by name below
            var props = new[] { AepConnectedKey };
            var classic = await DeviceInformation.FindAllAsync(
                Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true), props, DeviceInformationKind.AssociationEndpoint);
            var le = await DeviceInformation.FindAllAsync(
                Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), props, DeviceInformationKind.AssociationEndpoint);
            var byName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in classic.Concat(le))
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                var connected = d.Properties.TryGetValue(AepConnectedKey, out var c) && c is true;
                byName[d.Name] = byName.TryGetValue(d.Name, out var prev) ? prev || connected : connected;
            }
            Ui.Post(() =>
            {
                var list = byName.Select(kv => new PairedDevice
                {
                    Name = kv.Key,
                    Connected = kv.Value,
                    Percent = Devices.FirstOrDefault(b => b.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))?.Percent,
                }).OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
                Paired.Clear();
                foreach (var d in list) Paired.Add(d);
            });
        }
        catch (Exception ex) { Log.Info("bluetooth paired: " + ex.Message); }
    }

    /// <summary>Windows 11's quick-settings Bluetooth list (connect / disconnect in one click); older builds get the Settings page.</summary>
    public static void OpenQuickConnect()
    {
        if (Environment.OSVersion.Version.Build >= 22621) Ui.OpenUrl("ms-actioncenter:controlcenter/bluetooth");
        else OpenBluetoothSettings();
    }
    public bool HasAirPods => AirPods != null && (DateTime.Now - AirPods.Seen).TotalSeconds < 30;

    public async Task RefreshDevicesAsync()
    {
        try
        {
            // The battery level lives on the PnP device node of each paired Bluetooth (Classic or LE) device.
            var found = await DeviceInformation.FindAllAsync(
                "System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                new[] { BatteryKey, ConnectedKey }, DeviceInformationKind.Device);
            var list = new List<DeviceBattery>();
            foreach (var d in found)
            {
                if (!d.Properties.TryGetValue(BatteryKey, out var b) || b is not byte level) continue;
                if (string.IsNullOrWhiteSpace(d.Name) || list.Any(x => x.Name == d.Name)) continue;
                var connected = !d.Properties.TryGetValue(ConnectedKey, out var c) || c is not false;
                list.Add(new DeviceBattery { Name = d.Name, Percent = level, Connected = connected });
            }
            Ui.Post(() =>
            {
                Devices.Clear();
                foreach (var d in list.OrderBy(d => d.Name)) Devices.Add(d);
            });
        }
        catch (Exception ex)
        {
            Log.Info("bluetooth battery: " + ex.Message);
        }
    }

    /// <summary>BLE scanning costs battery, so it only runs while the Devices tab is visible or the popup is enabled.</summary>
    public void AcquireScan()
    {
        if (++_scanUsers > 1) return;
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            _watcher.Received += OnAdvertisement;
            _watcher.Start();
        }
        catch (Exception ex) { Log.Info("BLE unavailable: " + ex.Message); }
    }

    public void ReleaseScan()
    {
        if (--_scanUsers > 0) return;
        _scanUsers = 0;
        try { _watcher?.Stop(); } catch { }
        _watcher = null;
    }

    private void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var md in args.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != 0x004C) continue; // Apple
            var data = new byte[md.Data.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(md.Data)) reader.ReadBytes(data);
            if (data.Length < 9 || data[0] != 0x07) continue; // proximity pairing message
            if (args.RawSignalStrengthInDBm < -70) continue;  // ignore other people's AirPods across the room
            var status = Parse(data);
            status.Rssi = args.RawSignalStrengthInDBm;
            Ui.Post(() => Update(status));
        }
    }

    private static AirPodsStatus Parse(byte[] d)
    {
        // Layout (after Apple's company id): [0]=0x07 [1]=len [2]=? [3..4]=model [5]=status [6]=pod batteries [7]=charge flags|case [8]=lid
        var model = (d[3] << 8) | d[4];
        var flip = (d[5] & 0x20) == 0;
        var leftRaw = flip ? d[6] >> 4 : d[6] & 0x0F;
        var rightRaw = flip ? d[6] & 0x0F : d[6] >> 4;
        var caseRaw = d[7] & 0x0F;
        var charge = d[7] >> 4;
        static int? Level(int raw) => raw == 15 ? null : Math.Min(100, raw * 10 + (raw == 10 ? 0 : 5));
        return new AirPodsStatus
        {
            Model = model switch
            {
                0x0220 => "AirPods",
                0x0F20 => "AirPods (2nd gen)",
                0x1320 => "AirPods (3rd gen)",
                0x1920 or 0x1B20 => "AirPods 4",
                0x0E20 => "AirPods Pro",
                0x1420 or 0x2420 => "AirPods Pro 2",
                0x0A20 or 0x1F20 => "AirPods Max",
                _ => "AirPods",
            },
            Left = Level(leftRaw),
            Right = Level(rightRaw),
            Case = Level(caseRaw),
            LeftCharging = (charge & (flip ? 0b10 : 0b01)) != 0,
            RightCharging = (charge & (flip ? 0b01 : 0b10)) != 0,
            CaseCharging = (charge & 0b100) != 0,
            LidOpen = d.Length > 8 && ((d[8] >> 3) & 1) == 0,
            Seen = DateTime.Now,
        };
    }

    private void Update(AirPodsStatus s)
    {
        var isNew = !HasAirPods;
        AirPods = s;
        Raise(nameof(AirPods));
        Raise(nameof(HasAirPods));
        if (isNew && SettingsStore.Current.Huds.AirPodsPopup && (DateTime.Now - _lastPopup).TotalMinutes > 2)
        {
            _lastPopup = DateTime.Now;
            Notch.Hub.Show(new Island
            {
                Key = "airpods",
                Glyph = Glyphs.Headphones,
                Title = s.Model,
                Message = $"L {s.LeftText}   R {s.RightText}   Case {s.CaseText}",
                Accent = Ui.Teal,
                Duration = TimeSpan.FromSeconds(6),
                OpenTab = "devices",
                Actions = { new IslandAction("Connect", OpenBluetoothSettings, true, Glyphs.Bluetooth) },
            });
        }
    }

    /// <summary>Windows doesn't let apps start an A2DP connection directly; open the Bluetooth page / quick-connect.</summary>
    public static void OpenBluetoothSettings() => Ui.OpenUrl("ms-settings:bluetooth");
}
