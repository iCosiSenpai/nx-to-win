using System.Runtime.InteropServices;
using NxToWin.Models;
using Windows.Gaming.Input;
using Windows.System.Power;

namespace NxToWin.Hardware;

public sealed record BatteryEntry(string Name, BatteryReading Reading);

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
            if (controller.HardwareVendorId != 0x045E) continue;
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
                string detail = remaining.HasValue && full.HasValue
                    ? $"Capacità residua / carica piena secondo Windows: {remaining} / {full}."
                    : "Windows non ha esposto entrambe le capacità.";
                result.Add(new(controller.DisplayName, new(CapacityLevel(remaining, full), status, "Windows Gaming Input", detail)));
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
            result.Add(new($"Slot XInput {i + 1}",
                new(level, type, "Windows · XInput",
                    $"Tipo 0x{battery.Type:X2}, livello 0x{battery.Level:X2}. Lo slot può corrispondere a un controller già elencato o virtuale.")));
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
                result.Add(new(name, BatteryReading.Unknown with { Detail = "Lettura HID non disponibile." }));
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
                result.Add(new(name, reading ?? BatteryReading.Unknown with
                {
                    Detail = "Il controller non ha inviato un report HID esteso con il campo batteria."
                }));
            }
            finally
            {
                HidApiNative.hid_close(handle);
            }
        }
        return result;
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
        return new(level, power, $"Sony HID · 0x{report[0]:X2}", $"Stato grezzo 0x{status:X2}. Risoluzione a scatti del 10%.");
    }

    private static string TenPercentBand(int step) => step switch
    {
        < 10 => $"{step * 10}–{step * 10 + 9}%",
        10 => "100%",
        _ => "—"
    };
}
