namespace NxToWin.Dsp;

internal static class OcarinaHaptics
{
    private const double Rest = 0;
    private const double C4 = 261.63;
    private const double D4 = 293.66;
    private const double E4 = 329.63;
    private const double F4 = 349.23;
    private const double G4 = 392;
    private const double A4 = 440;
    private const double BFlat4 = 466.16;
    private const double B4 = 493.88;
    private const double C5 = 523.25;
    private const double D5 = 587.33;
    private const double EFlat5 = 622.25;
    private const double E5 = 659.26;
    private const double F5 = 698.46;
    private const double FSharp5 = 739.99;
    private const double G5 = 783.99;
    private const double A5 = 880;
    private const double BFlat5 = 932.33;
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
        if (track == "hero") return BuildHeroTheme();
        if (notes.Length == 0) return [];

        (double Hz, int Ticks, int Volume)[] continuation = track switch
        {
            // Princess Zelda's Theme. The second phrase opens up the register before the descending answer.
            "lullaby" =>
            [
                (G4, 25, 76), (A4, 25, 80),
                (B4, 51, 84), (D5, 25, 88), (A5, 51, 83), (G5, 25, 82),
                (D5, 34, 82), (C5, 25, 78), (B4, 25, 76), (A4, 111, 77),
                (G5, 100, 83), (G5, 34, 82), (FSharp5, 25, 78), (E5, 25, 76),
                (FSharp5, 34, 82), (E5, 25, 79), (B4, 76, 76),
                (FSharp5, 34, 83), (E5, 25, 78), (D5, 42, 77),
                (E5, 34, 79), (D5, 25, 76), (A4, 76, 74),
                (G5, 34, 83), (FSharp5, 25, 80), (E5, 25, 77),
                (D5, 51, 81), (B4, 25, 75), (G4, 128, 76)
            ],
            // Song of Time's answer, followed by the chant from the Temple of Time.
            "time" =>
            [
                (A4, 16, 85), (C5, 16, 86), (B4, 32, 83), (G4, 32, 82), (F4, 16, 78), (G4, 16, 80),
                (A4, 32, 85), (D4, 32, 82), (C4, 16, 78), (E4, 16, 81), (D4, 64, 84),
                (C4, 32, 76), (E4, 16, 81), (C4, 16, 78), (E4, 32, 82), (F4, 32, 84), (D4, 72, 83),
                (C4, 32, 76), (E4, 16, 81), (C4, 16, 77), (F4, 32, 84), (G4, 32, 86), (D4, 64, 82),
                (A4, 48, 87), (C5, 32, 90), (B4, 32, 85), (C5, 16, 88), (A4, 16, 82),
                (C5, 16, 87), (A4, 32, 82), (D4, 48, 85), (C4, 16, 77), (E4, 16, 81), (D4, 84, 83),
                (F4, 32, 81), (G4, 16, 83), (F4, 32, 79), (E4, 32, 76), (D4, 104, 82)
            ],
            // The Windmill melody answers the two rising figures, then leads into a descending ending.
            "storms" =>
            [
                (E5, 16, 86), (F5, 16, 91), (E5, 16, 84), (F5, 16, 88),
                (E5, 24, 85), (C5, 24, 83), (A4, 36, 85), (Rest, 12, 0),
                (A4, 16, 82), (D4, 16, 83), (F4, 16, 87), (G4, 24, 89),
                (A4, 48, 90), (Rest, 12, 0),
                (A4, 16, 81), (D4, 16, 85), (F4, 16, 87), (G4, 24, 89), (E4, 48, 84),
                (G4, 16, 85), (A4, 16, 89), (C5, 36, 87), (BFlat4, 16, 85), (A4, 16, 84), (G4, 24, 83),
                (F4, 16, 83), (A4, 16, 87), (D5, 48, 91), (C5, 16, 86), (A4, 16, 84), (G4, 24, 81),
                (E5, 16, 85), (F5, 16, 90), (E5, 24, 85), (C5, 24, 83), (A4, 48, 85),
                (A4, 16, 81), (D4, 16, 85), (F4, 16, 88), (G4, 24, 89), (A4, 40, 90),
                (F4, 20, 82), (E4, 20, 79), (D4, 96, 88)
            ],
            _ => []
        };
        var pattern = new List<(VibrationFrame, VibrationFrame, int)>((notes.Length + continuation.Length) * 5);
        AppendPhrase(pattern, notes);
        AppendPhrase(pattern, continuation);
        return pattern.ToArray();
    }

    private static (VibrationFrame Left, VibrationFrame Right, int Milliseconds)[] BuildHeroTheme()
    {
        // Lead voice of the NES Overworld theme, transposed from B-flat to G.
        // 36 ticks per quarter = 100 BPM. Eighth-note triplets use 12 ticks each.
        // https://github.com/camthesaxman/zeldasource/blob/master/songs.asm#L508-L633
        (double Hz, int Ticks, int Volume)[] melody =
        [
            (G4, 18, 94), (Rest, 18, 0), (D4, 54, 86), (Rest, 9, 0),
            (G4, 9, 85), (G4, 9, 87), (A4, 9, 90), (B4, 9, 93), (C5, 9, 94),
            (D5, 72, 98), (Rest, 18, 0), (D5, 18, 89), (D5, 12, 90), (EFlat5, 12, 94), (F5, 12, 97),
            (G5, 72, 98), (Rest, 12, 0), (G5, 12, 90), (G5, 12, 90), (G5, 12, 92),
            (F5, 12, 88), (EFlat5, 12, 86), (F5, 12, 90), (Rest, 12, 0), (EFlat5, 12, 86),
            (D5, 72, 94), (D5, 36, 90), (C5, 18, 84), (C5, 9, 83), (D5, 9, 88),
            (EFlat5, 72, 94), (D5, 18, 87), (C5, 18, 85), (BFlat4, 18, 85), (BFlat4, 9, 83), (C5, 9, 88),
            (D5, 72, 94), (C5, 18, 85), (BFlat4, 18, 83),
            // The contrasting bridge leads into an ending in G for the standalone effect.
            (G5, 108, 98), (BFlat5, 36, 94), (A5, 18, 90), (Rest, 18, 0), (FSharp5, 72, 91), (D5, 36, 89),
            (EFlat5, 108, 93), (G5, 36, 97), (FSharp5, 18, 89), (Rest, 18, 0), (D5, 72, 88),
            (C5, 36, 84), (B4, 18, 86), (A4, 18, 83), (G4, 108, 96)
        ];
        var pattern = new List<(VibrationFrame, VibrationFrame, int)>(melody.Length * 5);
        AppendPhrase(pattern, melody);
        return pattern.ToArray();
    }

    private static void AppendPhrase(List<(VibrationFrame Left, VibrationFrame Right, int Milliseconds)> pattern,
        ReadOnlySpan<(double Hz, int Ticks, int Volume)> notes)
    {
        int elapsedTicks = 0;
        int elapsedMs = 0;
        foreach (var (hz, ticks, volume) in notes)
        {
            elapsedTicks += ticks;
            int endMs = (elapsedTicks * 1000 + 30) / 60;
            int duration = endMs - elapsedMs;
            elapsedMs = endMs;

            if (hz == Rest)
            {
                pattern.Add((VibrationFrame.Silence, VibrationFrame.Silence, duration));
                continue;
            }

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
    }
}
