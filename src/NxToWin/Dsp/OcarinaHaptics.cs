namespace NxToWin.Dsp;

internal static class OcarinaHaptics
{
    private const double D4 = 293.66;
    private const double F4 = 349.23;
    private const double A4 = 440;
    private const double B4 = 493.88;
    private const double D5 = 587.33;
    private const int AttackMs = 15;
    private const int ReleaseMs = 30;

    public static (VibrationFrame Left, VibrationFrame Right, int Milliseconds)[] Build(string track)
    {
        // The original Ocarina of Time uses audio task ticks at 60 Hz (NTSC).
        // https://github.com/zeldaret/oot/blob/main/src/audio/game/general.c#L860-L899
        (double Hz, int Ticks, int Volume)[] notes = track switch
        {
            "lullaby" => [(B4, 51, 84), (D5, 25, 88), (A4, 78, 80), (B4, 51, 84), (D5, 25, 88), (A4, 100, 80)],
            "time" => [(A4, 32, 84), (D4, 65, 88), (F4, 33, 80), (A4, 32, 84), (D4, 65, 88), (F4, 99, 80)],
            "storms" => [(D4, 11, 84), (F4, 11, 88), (D5, 45, 80), (D4, 11, 84), (F4, 11, 88), (D5, 90, 80)],
            _ => []
        };
        var pattern = new List<(VibrationFrame, VibrationFrame, int)>(notes.Length * 4);
        int elapsedTicks = 0;
        int elapsedMs = 0;
        foreach (var (hz, ticks, volume) in notes)
        {
            elapsedTicks += ticks;
            int endMs = (elapsedTicks * 1000 + 30) / 60;
            int duration = endMs - elapsedMs;
            elapsedMs = endMs;

            // The octave-down fundamental gives the actuators more energy in their working range.
            int fundamental = VibrationFrame.RoundHz(hz / 2);
            int harmonic = VibrationFrame.RoundHz(hz);
            int amplitude = volume * 4;
            var note = new VibrationFrame(LfFreq: fundamental, LfAmp: amplitude,
                HfFreq: harmonic, HfAmp: amplitude / 5);
            var attack = note.WithStrength(.48f);
            var release = note.WithStrength(.4f);
            var tail = note.WithStrength(.15f);
            pattern.Add((attack, attack, AttackMs));
            pattern.Add((note, note, duration - AttackMs - ReleaseMs));
            pattern.Add((release, release, ReleaseMs / 2));
            pattern.Add((tail, tail, ReleaseMs / 2 - 5));
            pattern.Add((VibrationFrame.Silence, VibrationFrame.Silence, 5));
        }
        return pattern.ToArray();
    }
}
