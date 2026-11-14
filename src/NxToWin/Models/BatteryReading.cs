namespace NxToWin.Models;

public sealed record BatteryReading(string Level, string Power, string Source, string Detail, byte DsuLevel = 0)
{
    public static readonly BatteryReading Unknown = new("—", "Stato sconosciuto", "Nessun dato", "In attesa di un report valido.");

    public static BatteryReading Switch1(byte status)
    {
        int level = (status >> 5) & 7;
        bool charging = (status & 0x10) != 0;
        bool powered = (status & 1) != 0;
        string[] levels = ["Vuota", "Critica", "Bassa", "Media", "Alta"];
        return new(level < levels.Length ? $"{levels[level]} ({level}/4)" : "—",
            charging ? "In carica" : powered ? "Alimentazione esterna" : "A batteria",
            "Nintendo HID · 0x30", $"Stato 0x{status:X2}. Livello a scatti comunicato dal controller.",
            charging ? (byte)0xEE : level <= 4 ? (byte)(level + 1) : (byte)0);
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
            return new(levelText, powerText, "Nintendo · report compatto", $"{voltage}. Stato 0x{status:X2}.", dsu);
        }
        string detail = charge is byte value ? $"Tensione misurata. Campo di carica grezzo 0x{value:X2}." : "Tensione misurata.";
        return new(voltage, "Percentuale non esposta", "Nintendo · report 0x05", detail);
    }
}
