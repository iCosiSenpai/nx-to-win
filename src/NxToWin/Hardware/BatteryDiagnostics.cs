using System.Runtime.InteropServices;
using NxToWin.Models;
using Windows.Gaming.Input;
using Windows.System.Power;
using Windows.Devices.Enumeration;

namespace NxToWin.Hardware;

public sealed record BatteryEntry(string Name, BatteryReading Reading, string Transport = "")
{
    public string TransportIcon => Transport switch
    {
        "USB" => "\uE88E",
        "Wireless" => "\uE701",
        _ when Transport.StartsWith("Bluetooth", StringComparison.Ordinal) => "\uE702",
        _ => "\uE7FC"
    };
}

internal static class BatteryDiagnostics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBattery { public byte Type; public byte Level; }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint index, byte deviceType, out XInputBattery battery);

    public static IReadOnlyList<BatteryEntry> Read()
    {
        var result = new List<BatteryEntry>();
        foreach (var controller in RawGameController.RawGameControllers)
        {
            if (controller.HardwareVendorId is NintendoIds.VendorId or 0x054C) continue;
            try
            {
                var report = controller.TryGetBatteryReport();
                if (report == null || report.Status == BatteryStatus.NotPresent) continue;
                int? remaining = report.RemainingCapacityInMilliwattHours;
                int? full = report.FullChargeCapacityInMilliwattHours;
                string status = report.Status switch
                {
                    BatteryStatus.Charging => "In carica",
                    BatteryStatus.Discharging => "A batteria",
                    BatteryStatus.Idle => "Inattiva",
                    _ => "Stato sconosciuto"
                };
                string detail = remaining.HasValue && full.HasValue ? $"{remaining} / {full} mWh" : "";
                if (remaining is >= 0 && full is > 0 && remaining <= full || report.Status == BatteryStatus.Charging)
                {
                    var reading = new BatteryReading(CapacityLevel(remaining, full), status, "Windows Gaming Input", detail)
                    {
                        Charging = report.Status == BatteryStatus.Charging,
                        Percent = remaining is >= 0 && full is > 0 && remaining <= full
                            ? (int)Math.Round((double)remaining.Value / full.Value * 100) : null
                    };
                    result.Add(new(controller.DisplayName, reading));
                }
            }
            catch (COMException) { }
        }

        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetBatteryInformation(i, 0, out var battery) != 0 || battery.Type == 0) continue;
            string type = battery.Type switch
            {
                1 => "Alimentato via cavo secondo XInput",
                2 => "Pile alcaline",
                3 => "Batteria NiMH",
                _ => "Tipo batteria sconosciuto"
            };
            string[] levels = ["Vuota", "Bassa", "Media", "Alta"];
            string level = (battery.Type is 2 or 3 or 255) && battery.Level < levels.Length
                ? levels[battery.Level] : "—";
            if (battery.Type == 1) continue;
            result.Add(new($"Slot XInput {i + 1}",
                new(level, type, "Windows · XInput",
                    ""), battery.Type is 2 or 3 ? "Wireless" : ""));
        }

        foreach (var device in HidNintendoController.EnumerateHidDevices(0x054C))
        {
            string? name = device.ProductId switch
            {
                0x05C4 or 0x09CC => "DualShock 4",
                0x0BA0 => "DualShock 4 · adattatore USB",
                0x0CE6 => "DualSense",
                0x0DF2 => "DualSense Edge",
                _ => null
            };
            if (name == null) continue;

            IntPtr handle = HidApiNative.hid_open_path(device.Path);
            if (handle == IntPtr.Zero)
            {
                var fallback = BatteryReading.Unknown with { Detail = "Lettura HID non disponibile." };
                result.Add(new(name, ReadWindowsBatteryAsync(device.Path, DeviceInformationKind.DeviceInterface, fallback)
                    .GetAwaiter().GetResult(), device.Transport));
                continue;
            }

            try
            {
                BatteryReading? reading = null;
                byte[] buffer = new byte[128];
                for (int attempt = 0; attempt < 3 && reading == null; attempt++)
                {
                    int count = HidApiNative.hid_read_timeout(handle, buffer, (UIntPtr)buffer.Length, 70);
                    if (count < 0) break;
                    if (count > 0)
                        reading = ParsePlayStation(device.ProductId, buffer.AsSpan(0, count));
                }
                var fallback = reading ?? BatteryReading.Unknown with
                {
                    Detail = "Il controller non ha inviato un report HID esteso con il campo batteria."
                };
                result.Add(new(name, ReadWindowsBatteryAsync(device.Path, DeviceInformationKind.DeviceInterface, fallback)
                    .GetAwaiter().GetResult(), device.Transport));
            }
            finally
            {
                HidApiNative.hid_close(handle);
            }
        }
        foreach (var device in HidNintendoController.EnumerateHidDevices(0).Where(d => d.VendorId is not (NintendoIds.VendorId or 0x054C)))
        {
            var reading = ReadWindowsBatteryAsync(device.Path, DeviceInformationKind.DeviceInterface, BatteryReading.Unknown)
                .GetAwaiter().GetResult();
            if (reading.Percent.HasValue)
                result.Add(new(string.IsNullOrWhiteSpace(device.Name) ? "Controller HID" : device.Name, reading, device.Transport));
        }
        return result;
    }

    internal static async Task<BatteryReading> ReadWindowsBatteryAsync(string id, DeviceInformationKind kind, BatteryReading fallback)
    {
        if (string.IsNullOrEmpty(id)) return fallback;
        try
        {
            const string batteryProperty = "System.Devices.BatteryLife";
            const string containerProperty = "System.Devices.ContainerId";
            string[] properties = [batteryProperty, containerProperty];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            var info = await DeviceInformation.CreateFromIdAsync(id, properties, kind).AsTask(timeout.Token).ConfigureAwait(false);
            int? percentage = GetBatteryPercent(info, batteryProperty);
            if (percentage == null && kind == DeviceInformationKind.DeviceInterface &&
                info?.Properties.TryGetValue(containerProperty, out var value) == true && value is Guid container && container != Guid.Empty)
            {
                var physicalDevice = await DeviceInformation.CreateFromIdAsync(container.ToString("B"),
                    [batteryProperty], DeviceInformationKind.DeviceContainer).AsTask(timeout.Token).ConfigureAwait(false);
                percentage = GetBatteryPercent(physicalDevice, batteryProperty);
            }
            if (percentage.HasValue) return fallback.WithWindowsPercentage(percentage.Value);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or OperationCanceledException or UnauthorizedAccessException) { }
        return fallback;
    }

    private static int? GetBatteryPercent(DeviceInformation? info, string property)
    {
        if (info?.Properties.TryGetValue(property, out var value) != true) return null;
        int percentage = value switch { byte b => b, ushort u => u, uint u when u <= 100 => (int)u, int i => i, _ => -1 };
        return percentage is >= 0 and <= 100 ? percentage : null;
    }

    internal static string CapacityLevel(int? remaining, int? full) =>
        remaining is >= 0 && full is > 0 && remaining <= full
            ? $"{(double)remaining.Value / full.Value:P0}" : "—";

    internal static BatteryReading? ParsePlayStation(ushort product, ReadOnlySpan<byte> report)
    {
        if (report.IsEmpty) return null;
        bool sense = product is 0x0CE6 or 0x0DF2;
        int offset = (sense, report[0], report.Length) switch
        {
            (true, 1, 64) => 53,
            (true, 0x31, 78) => 54,
            (false, 1, 64) => 30,
            (false, 0x11, 78) => 32,
            _ => -1
        };
        if (offset < 0) return null;
        if (product == 0x0BA0 && (report[offset + 1] & 4) != 0) return null;

        byte status = report[offset];
        int step = status & 0x0F;
        string power;
        string level;
        if (sense)
        {
            int state = status >> 4;
            power = state switch
            {
                0 => "A batteria",
                1 => "In carica",
                2 => "Carica completa",
                0xA or 0xB => "Carica sospesa",
                0xF => "Errore di carica",
                _ => "Stato sconosciuto"
            };
            level = state == 2 ? "100%" : state is 0 or 1 ? TenPercentBand(step) : "—";
        }
        else
        {
            bool wired = (status & 0x10) != 0;
            power = !wired ? "A batteria" : step == 11 ? "Carica completa" : step <= 10 ? "In carica" : "Alimentazione USB";
            level = wired && step == 11 ? "100%" : !wired && step > 9 ? "—" : TenPercentBand(step);
        }
        return new(level, power, $"Sony HID · 0x{report[0]:X2}", "Risoluzione a scatti del 10%.")
        { Charging = power == "In carica", ExternalPower = (sense ? status >> 4 is 1 or 2 : (status & 0x10) != 0) };
    }

    private static string TenPercentBand(int step) => step switch
    {
        < 10 => $"{step * 10}–{step * 10 + 9}%",
        10 => "100%",
        _ => "—"
    };
}
