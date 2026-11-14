using System;

namespace NxToWin.Dsp;

public readonly record struct VibrationFrame(
    double LfFreq = 160,
    int LfAmp = 0,
    double HfFreq = 320,
    int HfAmp = 0)
{
    public static readonly VibrationFrame Silence = new(160, 0, 320, 0);
    public static VibrationFrame Silent => Silence;

    public VibrationFrame(float freqHz, float amp01)
        : this(
            LfFreq: RoundHz(Math.Clamp(freqHz, 45f, 510f)),
            LfAmp: RoundHz(Math.Clamp(amp01, 0f, 1f) * 1023f),
            HfFreq: RoundHz(Math.Clamp(freqHz * 1.5f, 82f, 800f)),
            HfAmp: RoundHz(Math.Clamp(amp01 * 0.45f, 0f, 1f) * 1023f))
    {
    }

    public bool IsSilent => LfAmp <= 0 && HfAmp <= 0;
    public double LfHz => LfFreq;
    public double HfHz => HfFreq;
    public double FreqHz => HfAmp > LfAmp ? HfFreq : LfFreq;
    public float Amp => Math.Clamp(Math.Max(LfAmp, HfAmp) / 1023f, 0f, 1f);

    public VibrationFrame WithStrength(double multiplier)
    {
        if (IsSilent) return Silence;
        double low = Math.Max(0, LfAmp * multiplier);
        double high = Math.Max(0, HfAmp * multiplier);
        double scale = Math.Max(1, (low + high) / 1000);
        return new VibrationFrame(LfFreq, RoundHz(low / scale), HfFreq, RoundHz(high / scale));
    }

    internal static int RoundHz(double hz) => (int)Math.Round(hz);

    // Nintendo Switch 2 encodes frequency logarithmically at 96 steps per octave.
    private static double DecodeFrequency(int code) => 110 * Math.Pow(2, (code - 333) / 96.0);

    private static int EncodeFrequency(double hz)
    {
        double frequency = Math.Clamp(hz, DecodeFrequency(0), DecodeFrequency(1023));
        int lower = Math.Clamp((int)Math.Floor(333 + 96 * Math.Log2(frequency / 110)), 0, 1022);
        return frequency - DecodeFrequency(lower) <= DecodeFrequency(lower + 1) - frequency ? lower : lower + 1;
    }

    public void WriteSwitch2Bytes(Span<byte> dest5)
    {
        ulong value = (ulong)(uint)EncodeFrequency(HfFreq)
                    | (ulong)(uint)Math.Clamp(HfAmp, 0, 1023) << 10
                    | (ulong)(uint)EncodeFrequency(LfFreq) << 20
                    | (ulong)(uint)Math.Clamp(LfAmp, 0, 1023) << 30;
        for (int i = 0; i < 5; i++) dest5[i] = (byte)(value >> (8 * i));
    }

    public static VibrationFrame FromSwitch2Bytes(ReadOnlySpan<byte> src5)
    {
        if (src5.Length < 5) return Silence;
        ulong value = 0;
        for (int i = 0; i < 5; i++) value |= (ulong)src5[i] << (8 * i);
        return new VibrationFrame(
            LfFreq: DecodeFrequency((int)((value >> 20) & 0x3FF)),
            LfAmp: (int)((value >> 30) & 0x3FF),
            HfFreq: DecodeFrequency((int)(value & 0x3FF)),
            HfAmp: (int)((value >> 10) & 0x3FF));
    }

    internal static VibrationFrame Merge(VibrationFrame a, VibrationFrame b)
    {
        var low = a.LfAmp >= b.LfAmp ? a : b;
        var high = a.HfAmp >= b.HfAmp ? a : b;
        return new VibrationFrame(low.LfFreq, low.LfAmp, high.HfFreq, high.HfAmp);
    }
}

public readonly record struct MotorSamples(VibrationFrame First, VibrationFrame Second, VibrationFrame Third, int Count)
{
    public MotorSamples(VibrationFrame frame) : this(frame, frame, frame, 1) { }

    public VibrationFrame Peak => VibrationFrame.Merge(First, VibrationFrame.Merge(Second, Third));

    internal MotorSamples WithStrength(float gain) =>
        new(First.WithStrength(gain), Second.WithStrength(gain), Third.WithStrength(gain), Count);

    internal static MotorSamples ReadSwitch2(ReadOnlySpan<byte> block)
    {
        int count = (block[0] >> 4) - 4;
        if (count is < 1 or > 3) return new(VibrationFrame.Silence);
        var first = VibrationFrame.FromSwitch2Bytes(block[1..]);
        return new(first,
            count >= 2 ? VibrationFrame.FromSwitch2Bytes(block[6..]) : first,
            count == 3 ? VibrationFrame.FromSwitch2Bytes(block[11..]) : first, count);
    }
}

public readonly record struct StereoRumble(MotorSamples Left, MotorSamples Right)
{
    public StereoRumble(VibrationFrame left, VibrationFrame right) : this(new MotorSamples(left), new MotorSamples(right)) { }

    public static StereoRumble Silence => new(VibrationFrame.Silence, VibrationFrame.Silence);

    internal bool Sustained { get; init; }
    internal bool IsSilent => Left.Peak.IsSilent && Right.Peak.IsSilent;

    internal StereoRumble WithStrength(float gain) => new(Left.WithStrength(gain), Right.WithStrength(gain));

    internal StereoRumble FillPacket() =>
        new(Left with { Count = 3 }, Right with { Count = 3 });

    internal static bool TryReadSwitch2Report(ReadOnlySpan<byte> data, out StereoRumble packet)
    {
        packet = Silence;
        if (data.Length < 33 || data[0] != 2) return false;
        packet = new(MotorSamples.ReadSwitch2(data.Slice(1, 16)), MotorSamples.ReadSwitch2(data.Slice(17, 16)));
        return true;
    }
}
public static class HdRumbleEngine
{
    public static byte[] BuildGameCubeOutput(int sequence, bool active, bool wired)
    {
        byte[] report = new byte[wired ? 64 : 5];
        report[0] = wired ? (byte)3 : (byte)0;
        report[1] = (byte)(0x50 | (sequence & 0x0F));
        report[2] = active ? (byte)1 : (byte)2;
        return report;
    }

    private static void WriteSwitch2MotorBlock(Span<byte> destination, int sequence, MotorSamples samples)
    {
        destination[0] = (byte)(0x40 | (samples.Count << 4) | (sequence & 0x0F));
        samples.First.WriteSwitch2Bytes(destination.Slice(1, 5));
        if (samples.Count >= 2) samples.Second.WriteSwitch2Bytes(destination.Slice(6, 5));
        if (samples.Count == 3) samples.Third.WriteSwitch2Bytes(destination.Slice(11, 5));
    }

    internal static byte[] BuildSwitch2Report(int sequence, StereoRumble rumble, bool wired,
        bool leftJoyCon, bool rightJoyCon)
    {
        bool single = leftJoyCon || rightJoyCon;
        byte[] report = new byte[wired ? 64 : single ? 17 : 33];
        report[0] = wired ? (byte)(single ? 1 : 2) : (byte)0;
        WriteSwitch2MotorBlock(report.AsSpan(1, 16), sequence, rightJoyCon ? rumble.Right : rumble.Left);
        if (!single) WriteSwitch2MotorBlock(report.AsSpan(17, 16), sequence, rumble.Right);
        return report;
    }
    /// <summary>
    /// Decodes a 4-byte Switch 1 HD Rumble motor block (sent by SDL2 / SDL3 / Native PC Games / Emulators)
    /// into a VibrationFrame with LF/HF frequencies in Hz and 10-bit amplitudes (0..1023).
    /// </summary>
    public static VibrationFrame DecodeSwitch1Side(ReadOnlySpan<byte> b, double strengthMultiplier = 1.0, bool isPro = true)
    {
        if (b.Length < 4) return VibrationFrame.Silent;

        // Neutral Switch 1 packet check: 00 01 40 40
        if (b[0] == 0x00 && b[1] == 0x01 && b[2] == 0x40 && b[3] == 0x40)
            return VibrationFrame.Silent;

        // High Frequency (bytes 0..1)
        int hfRaw = b[0] | ((b[1] & 0x01) << 8);
        int hfAmpEnc = (b[1] >> 1) & 0x7F;

        // Low Frequency (bytes 2..3)
        int lfRaw = b[2] & 0x7F;
        int lfAmpIdx = Math.Max(0, ((b[3] - 0x40) << 1) | (b[2] >> 7));
        int hfHz = VibrationFrame.RoundHz(10 * Math.Pow(2, (hfRaw / 4.0 + 0x60) / 32));
        int lfHz = VibrationFrame.RoundHz(10 * Math.Pow(2, (lfRaw + 0x40) / 32.0));

        double hfLinear = DecodeSwitch1EncodedAmplitude(hfAmpEnc);
        double lfLinear = DecodeSwitch1EncodedAmplitude(lfAmpIdx);

        int hfAmp10 = Math.Clamp((int)Math.Round(hfLinear * 1023.0 * strengthMultiplier), 0, 1023);
        int lfAmp10 = Math.Clamp((int)Math.Round(lfLinear * 1023.0 * strengthMultiplier), 0, 1023);

        if (!isPro)
        {
            // Keep Joy-Con total amplitude within 10-bit physical budget
            int total = hfAmp10 + lfAmp10;
            if (total > 1023)
            {
                lfAmp10 = (int)Math.Round(lfAmp10 * 1023.0 / total);
                hfAmp10 = 1023 - lfAmp10;
            }
        }

        if (hfAmp10 == 0 && lfAmp10 == 0)
            return VibrationFrame.Silent;

        return new VibrationFrame(
            LfFreq: lfHz,
            LfAmp: lfAmp10,
            HfFreq: hfHz,
            HfAmp: hfAmp10);
    }

    private static double DecodeSwitch1EncodedAmplitude(int encoded)
    {
        return Math.Clamp(encoded, 0, 100) switch
        {
            0 => 0,
            1 => 0.007843,
            <= 15 => 0.112491 * Math.Pow(2, (encoded - 15) / 4.0),
            <= 32 => 0.117471 * Math.Pow(2, (encoded - 16) / 16.0),
            int level => Math.Min(1, 0.240087 * Math.Pow(2, (level - 34) / 32.0))
        };
    }

    /// <summary>
    /// Encodes a VibrationFrame into a 4-byte Switch 1 LRA HD Rumble block (for physical Switch 1 controllers).
    /// </summary>
    public static void WriteSwitch1Side(Span<byte> dest4, VibrationFrame frame)
    {
        if (frame.IsSilent)
        {
            dest4[0] = 0x00;
            dest4[1] = 0x01;
            dest4[2] = 0x40;
            dest4[3] = 0x40;
            return;
        }

        double hfHz = Math.Clamp(frame.HfHz, 82.0, 1252.0);
        double lfHz = Math.Clamp(frame.LfHz, 41.0, 626.0);

        byte hfCode = (byte)Math.Clamp((int)Math.Round((Math.Log2(hfHz / 10.0) - 3.0) * 32.0), 0, 127);
        byte lfCode = (byte)Math.Clamp((int)Math.Round((Math.Log2(lfHz / 10.0) - 2.0) * 32.0), 1, 127);

        ushort hfRaw = (ushort)(hfCode * 4);
        byte lfRaw = lfCode;

        byte hfAmpByte = EncodeSwitch1AmplitudeByte(frame.HfAmp / 1023.0);
        int lfAmpIndex = EncodeSwitch1AmplitudeByte(frame.LfAmp / 1023.0) >> 1;

        dest4[0] = (byte)(hfRaw & 0xFF);
        dest4[1] = (byte)(hfAmpByte | ((hfRaw >> 8) & 1));
        dest4[2] = (byte)(lfRaw | ((lfAmpIndex & 1) << 7));
        dest4[3] = (byte)(0x40 + (lfAmpIndex >> 1));
    }

    private static byte EncodeSwitch1AmplitudeByte(double amp01)
    {
        int low = 0, high = 100;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (DecodeSwitch1EncodedAmplitude(middle) < amp01) low = middle;
            else high = middle;
        }
        int nearest = amp01 - DecodeSwitch1EncodedAmplitude(low) <= DecodeSwitch1EncodedAmplitude(high) - amp01 ? low : high;
        return (byte)(nearest * 2);
    }

    /// <summary>
    /// Synthesizes Dual-Band LRA HD Rumble frames from DualSense / Xbox One / XInput motor + impulse trigger values.
    /// </summary>
    public static (VibrationFrame Left, VibrationFrame Right) SynthesizeFromLegacyMotors(
        byte largeLeft,
        byte smallRight,
        byte impulseLeft = 0,
        byte impulseRight = 0,
        double strengthMultiplier = 1.0)
    {
        int lfAmp = Math.Clamp((int)Math.Round((largeLeft / 255.0) * 820.0 * strengthMultiplier), 0, 1023);
        int hfAmp = Math.Clamp((int)Math.Round((smallRight / 255.0) * 680.0 * strengthMultiplier), 0, 1023);

        int impLeftAmp = Math.Clamp((int)Math.Round((impulseLeft / 100.0) * 650.0 * strengthMultiplier), 0, 1023);
        int impRightAmp = Math.Clamp((int)Math.Round((impulseRight / 100.0) * 650.0 * strengthMultiplier), 0, 1023);

        int leftHfFreq = impulseLeft > 0 ? Math.Clamp(300 + impulseLeft * 181 / 100, 300, 481) : 0x171;
        int rightHfFreq = impulseRight > 0 ? Math.Clamp(300 + impulseRight * 181 / 100, 300, 481) : 0x171;

        var left = new VibrationFrame(
            LfFreq: 0x0E1,
            LfAmp: lfAmp,
            HfFreq: leftHfFreq,
            HfAmp: Math.Min(1023, hfAmp / 2 + impLeftAmp));

        var right = new VibrationFrame(
            LfFreq: 0x0E1,
            LfAmp: lfAmp / 2,
            HfFreq: rightHfFreq,
            HfAmp: Math.Min(1023, hfAmp + impRightAmp));

        return (left, right);
    }

    public static (VibrationFrame Left, VibrationFrame Right) FromLegacyAndImpulseTriggers(
        byte largeLeft,
        byte smallRight,
        byte impulseLeft = 0,
        byte impulseRight = 0,
        double strengthMultiplier = 1.0) =>
        SynthesizeFromLegacyMotors(largeLeft, smallRight, impulseLeft, impulseRight, strengthMultiplier);

    public static byte[] EncodeSwitch1Neutral() =>
        [0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40];

    public static byte[] EncodeSwitch1Side(VibrationFrame frame)
    {
        byte[] buf = new byte[4];
        WriteSwitch1Side(buf, frame);
        return buf;
    }
}
