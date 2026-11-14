namespace NxToWin.Dsp;

internal static class HapticEffects
{
    public static (VibrationFrame Left, VibrationFrame Right, int Milliseconds)[] Build(string effect) => effect switch
    {
        "pulse" => Sweep(170, 170, 170, (t, frame) => Stereo(frame.WithStrength(Math.Sin(Math.PI * t) * .65))),
        "sweep" => Sweep(320, 115, 610, (t, frame) => Stereo(frame.WithStrength((1 - t) * .67))),
        "chirp" => Sweep(80, 420, 475, (t, frame) => Stereo(frame.WithStrength(Math.Sin(Math.PI * t) * .59))),
        "heartbeat" => [
            (new(112, .65f), new(112, .65f), 65),
            (new(107, .22f), new(107, .22f), 40),
            (VibrationFrame.Silence, VibrationFrame.Silence, 80),
            (new(174, .42f), new(175, .42f), 65),
            (new(156, .15f), new(156, .15f), 50)
        ],
        "left" => Sweep(170, 170, 400, (t, frame) => (frame.WithStrength(Math.Sin(Math.PI * t) * .6f), VibrationFrame.Silence)),
        "right" => Sweep(170, 170, 400, (t, frame) => (VibrationFrame.Silence, frame.WithStrength(Math.Sin(Math.PI * t) * .6f))),
        "stereo" => Sweep(140, 278, 850, (t, frame) =>
        {
            double envelope = Math.Sin(Math.PI * t);
            double pan = (1 - Math.Cos(2 * Math.PI * t)) / 2;
            return (frame.WithStrength(envelope * Math.Sqrt(1 - pan) * .62),
                frame.WithStrength(envelope * Math.Sqrt(pan) * .62));
        }),
        "rain" => Sweep(210, 165, 1400, (t, _) =>
        {
            double left = Math.Pow(Math.Max(0, Math.Cos(t * 15 * Math.PI)), 6);
            double right = Math.Pow(Math.Max(0, Math.Cos((t + .043) * 19 * Math.PI)), 6);
            double envelope = Math.Sin(Math.PI * t);
            return (new(210f, (float)(.52 * left * envelope)), new(250f, (float)(.46 * right * envelope)));
        }),
        "gamecube" => [(new(160, .6f), new(160, .6f), 170)],
        _ => []
    };

    private static (VibrationFrame Left, VibrationFrame Right) Stereo(VibrationFrame frame) => (frame, frame);

    private static (VibrationFrame Left, VibrationFrame Right, int Milliseconds)[] Sweep(
        float start, float end, int milliseconds,
        Func<double, VibrationFrame, (VibrationFrame Left, VibrationFrame Right)> shape)
    {
        const int sampleMilliseconds = 5;
        var samples = new (VibrationFrame, VibrationFrame, int)[milliseconds / sampleMilliseconds];
        for (int i = 0; i < samples.Length; i++)
        {
            double position = (double)i / (samples.Length - 1);
            float frequency = (float)(start + (end - start) * position);
            var (left, right) = shape(position, new VibrationFrame(frequency, 1));
            samples[i] = (left, right, sampleMilliseconds);
        }
        return samples;
    }
}
