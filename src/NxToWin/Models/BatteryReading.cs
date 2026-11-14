namespace NxToWin.Models;

public sealed record BatteryReading(string Level, string Power, string Source, string Detail, byte DsuLevel = 0)
{
    public ushort? Millivolts { get; init; }
    public int? Percent { get; init; }
    public bool Charging { get; init; }
    public bool ExternalPower { get; init; }
    public string Icon => Charging ? "\uE85F" : "\uE855";
    public string Voltage => Millivolts is ushort mv && Level != $"{mv} mV" ? $"{mv} mV" : "";

    public static readonly BatteryReading Unknown = new("—", "Stato sconosciuto", "Nessun dato", "In attesa di un report valido.");

    public static BatteryReading Switch1(byte status, ushort? voltage = null)
    {
        int level = (status >> 5) & 7;
        bool charging = (status & 0x10) != 0;
        bool powered = (status & 1) != 0;
        string[] levels = ["Critica", "Bassa", "Media", "Alta", "Piena"];
        return new(level < levels.Length ? $"{levels[level]} ({level}/4)" : "—",
            charging ? "In carica" : powered && level == 4 ? "Carica completa" : powered ? "Alimentazione esterna" : "A batteria",
            voltage.HasValue ? "Nintendo HID · 0x30 / 0x50" : "Nintendo HID · 0x30", "",
            charging ? (byte)0xEE : level <= 4 ? (byte)(level + 1) : (byte)0)
        { Millivolts = voltage, Charging = charging, ExternalPower = powered };
    }

    public static BatteryReading Switch2(ushort millivolts, byte? power = null, byte? charge = null)
    {
        string voltage = millivolts is >= 2500 and <= 5000 ? $"{millivolts} mV" : "Tensione non disponibile";
        if (power is byte status)
        {
            int level = (status >> 2) & 15;
            string levelText = level <= 9 ? $"{level}/9" : "—";
            string powerText = (status & 2) != 0 ? "In carica" : (status & 1) != 0 ? "Alimentazione esterna" : "A batteria";
            byte dsu = (status & 2) != 0 ? (byte)0xEE : level <= 9 ? (byte)(1 + level * 4 / 9) : (byte)0;
            return new(levelText, powerText, "Nintendo · report compatto", "", dsu)
            { Millivolts = millivolts is >= 2500 and <= 5000 ? millivolts : null, Charging = (status & 2) != 0, ExternalPower = (status & 1) != 0 };
        }
        // Captures document 0x34 while charging. Other values have no general decoding.
        bool charging = charge == 0x34;
        return new(voltage, charging ? "In carica" : "Stato di carica non esposto", "Nintendo · report 0x05", "",
            charging ? (byte)0xEE : (byte)0)
        { Millivolts = millivolts is >= 2500 and <= 5000 ? millivolts : null, Charging = charging, ExternalPower = charging };
    }

    internal BatteryReading WithWindowsPercentage(int percentage) => this with
    {
        Level = $"{percentage}%",
        Percent = percentage,
        Source = "Windows · livello batteria",
        Detail = ""
    };
}
