using System.Buffers.Binary;
using System.IO;
using System.Text;
using NxToWin.Dsp;
using NxToWin.Models;

namespace NxToWin.VirtualOutput;

internal static class Switch2UsbProtocol
{
    public const string Serial = "NX2WIN0002";
    public const ushort Revision = 0x0101;
    private const int StickRange = 1496;
    private const int StickCenter = 2048;

    // Captured Nintendo gamepad collection, including reports 05, 09 and output 02.
    private static readonly byte[] ReportDescriptor = Convert.FromHexString(
        "05010905A101850505FF0901150026FF00953F750881028509090195028102" +
        "050919012915250195157501810295017503810305010901A1000930093109330935" +
        "26FF0F9504750C8102C005FF090226FF0095347508810285020901953F9102C0");

    private static readonly byte[] DeviceDescriptor =
    [
        18, 1, 0, 2, 0xEF, 2, 1, 64, 0x7E, 5, 0x69, 0x20,
        (byte)(Revision & 0xFF), (byte)(Revision >> 8), 1, 2, 3, 1
    ];

    private static readonly byte[] HidDescriptor = [9, 0x21, 0x11, 1, 0, 1, 0x22, (byte)ReportDescriptor.Length, 0];

    private static readonly byte[] ConfigurationDescriptor =
    [
        9, 2, 80, 0, 2, 1, 0, 0xC0, 0xFA,
        8, 0x0B, 0, 1, 3, 0, 0, 0,
        9, 4, 0, 0, 2, 3, 0, 0, 0,
        .. HidDescriptor,
        7, 5, 0x81, 3, 64, 0, 4,
        7, 5, 0x01, 3, 64, 0, 4,
        8, 0x0B, 1, 1, 0xFF, 0, 0, 0,
        9, 4, 1, 0, 2, 0xFF, 0, 0, 0,
        7, 5, 0x02, 2, 64, 0, 0,
        7, 5, 0x82, 2, 64, 0, 0
    ];

    private static readonly byte[] OsString = Convert.FromHexString("12034D005300460054003100300030000100");
    private static readonly byte[] WinUsbId = Convert.FromHexString(
        "28000000000104000100000000000000010157494E55534200000000000000000000000000000000");

    private static readonly byte[] WinUsbProperties = BuildWinUsbProperties();

    private static byte[] BuildWinUsbProperties()
    {
        byte[] name = Encoding.Unicode.GetBytes("DeviceInterfaceGUID\0");
        byte[] value = Encoding.Unicode.GetBytes("{6F13725E-EF0E-4FD3-AE5F-B2DE989EC825}\0");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(24 + name.Length + value.Length);
        writer.Write((ushort)0x0100);
        writer.Write((ushort)5);
        writer.Write((ushort)1);
        writer.Write(14 + name.Length + value.Length);
        writer.Write(1);
        writer.Write((ushort)name.Length);
        writer.Write(name);
        writer.Write(value.Length);
        writer.Write(value);
        return stream.ToArray();
    }

    public static byte[]? GetControlReply(ReadOnlySpan<byte> setup, int maxLength, ref byte configuration, byte[] report)
    {
        byte type = setup[0];
        byte request = setup[1];
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(setup[2..]);
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(setup[4..]);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(setup[6..]);

        if (type == 0 && request == 9 && value <= 1)
        {
            configuration = (byte)value;
            return [];
        }

        byte[]? data = (type, request) switch
        {
            (0x80, 6) => value switch
            {
                0x0100 => DeviceDescriptor,
                0x0200 => ConfigurationDescriptor,
                0x0300 => [4, 3, 9, 4],
                0x0301 => StringDescriptor("Nintendo"),
                0x0302 => StringDescriptor("Switch 2 Pro Controller"),
                0x0303 => StringDescriptor(Serial),
                0x03EE => OsString,
                _ => null
            },
            (0x81, 6) when index == 0 && value == 0x2100 => HidDescriptor,
            (0x81, 6) when index == 0 && value == 0x2200 => ReportDescriptor,
            (0x80, 8) => [configuration],
            (0x81, 10) when index < 2 => [0],
            (0x01, 11) when index < 2 && value == 0 => [],
            (0x80, 0) => [1, 0],
            (0x81 or 0x82, 0) => [0, 0],
            (0x02, 1) when value == 0 => [],
            (0xA1, 1) when index == 0 && value is 0x0105 or 0x0109 => report,
            (0xA1, 2) when index == 0 => [0],
            (0x21, 9 or 10 or 11) when index == 0 => [],
            _ when (type & 0xE0) == 0xC0 && index == 4 => WinUsbId,
            _ when (type & 0xE0) == 0xC0 && index == 5 => WinUsbProperties,
            _ => null
        };

        return data?.AsSpan(0, Math.Min(data.Length, Math.Min(maxLength, length))).ToArray();
    }

    private static byte[] StringDescriptor(string text)
    {
        byte[] bytes = new byte[2 + text.Length * 2];
        bytes[0] = (byte)bytes.Length;
        bytes[1] = 3;
        Encoding.Unicode.GetBytes(text, bytes.AsSpan(2));
        return bytes;
    }

    public static byte[] BuildInputReport(byte format, uint frame, ControllerStateSnapshot state, SwitchButtons buttons, bool suppressGyro)
    {
        byte[] report = new byte[64];
        report[0] = format;
        if (format == 9)
        {
            report[1] = (byte)frame;
            report[2] = 0x25;
            report[3] = PackButtons(buttons, SwitchButtons.B, SwitchButtons.A, SwitchButtons.Y, SwitchButtons.X,
                SwitchButtons.R, SwitchButtons.ZR, SwitchButtons.Plus, SwitchButtons.RStick);
            report[4] = PackButtons(buttons, SwitchButtons.Down, SwitchButtons.Right, SwitchButtons.Left, SwitchButtons.Up,
                SwitchButtons.L, SwitchButtons.ZL, SwitchButtons.Minus, SwitchButtons.LStick);
            report[5] = PackButtons(buttons, SwitchButtons.Home, SwitchButtons.Capture, SwitchButtons.GR,
                SwitchButtons.GL, SwitchButtons.ChatC);
            PackSticks(report.AsSpan(6), state);
            report[12] = 0x30;
            return report;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(1), frame * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(5), (uint)buttons & 0x03FFFFFF);
        PackSticks(report.AsSpan(11), state);
        ushort millivolts = state.BatteryVoltage > 0 ? (ushort)Math.Clamp((int)(state.BatteryVoltage * 1000), 2500, 5000) : (ushort)3800;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(0x20), millivolts);
        report[0x22] = 0x20;
        report[0x2A] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(0x2B), frame * 4000);
        WriteAxis(report.AsSpan(0x31), state.AccelG_X, 4096);
        WriteAxis(report.AsSpan(0x33), state.AccelG_Y, 4096);
        WriteAxis(report.AsSpan(0x35), state.AccelG_Z, 4096);
        if (!suppressGyro)
        {
            const float countsPerDegree = ImuFusionEngine.Switch2ProReportGyroCountsPerDps;
            WriteAxis(report.AsSpan(0x37), state.GyroDps_X, countsPerDegree);
            WriteAxis(report.AsSpan(0x39), state.GyroDps_Y, countsPerDegree);
            WriteAxis(report.AsSpan(0x3B), state.GyroDps_Z, countsPerDegree);
        }
        return report;
    }

    private static void WriteAxis(Span<byte> destination, float value, float scale) =>
        BinaryPrimitives.WriteInt16LittleEndian(destination, (short)Math.Clamp(MathF.Round(value * scale), short.MinValue, short.MaxValue));

    private static void PackSticks(Span<byte> data, ControllerStateSnapshot state)
    {
        StickCalibration.Pack12Bit(data, ToStick(state.LeftStickX), ToStick(state.LeftStickY));
        StickCalibration.Pack12Bit(data[3..], ToStick(state.RightStickX), ToStick(state.RightStickY));
    }

    private static int ToStick(float value) => StickCenter + (int)MathF.Round(value * StickRange);

    private static byte PackButtons(SwitchButtons buttons, params SwitchButtons[] order)
    {
        byte packed = 0;
        for (int i = 0; i < order.Length; i++)
            if ((buttons & order[i]) != 0)
                packed |= (byte)(1 << i);
        return packed;
    }

    public static byte[] CreateReply(ReadOnlySpan<byte> command, ref byte format, ref bool streaming)
    {
        byte[] payload = (command[0], command[3]) switch
        {
            (2, 1 or 4) when command.Length >= 16 => ReadFlash(command),
            (3, 3 or 0x0D) or (0x11, 1) => [1, 0, 0, 0],
            (7, 1) => [0],
            (0x0C, 1 or 2 or 3 or 4 or 5) => [0, 0, 0, 0],
            (0x10, 1) => [1, 1, 5, 2, 12, 0, 0, 0, 255, 255, 255, 255],
            _ => []
        };

        if (command[0] == 3)
        {
            if (command[3] == 0x0A && command.Length >= 9 && command[8] is 5 or 9)
                format = command[8];
            if (command[3] == 3 && command.Length >= 9)
                streaming = command[8] != 0;
            if (command[3] == 0x0D)
                streaming = true;
        }

        return [command[0], 1, command[2], command[3], 0, 0xF8, 0, 0, .. payload];
    }

    private static byte[] ReadFlash(ReadOnlySpan<byte> command)
    {
        int length = command[3] == 1 ? 64 : Math.Min((int)command[8], 80);
        uint address = BinaryPrimitives.ReadUInt32LittleEndian(command[12..]);
        byte[] reply = new byte[8 + length];
        reply[0] = (byte)length;
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), address);

        // Each supported flash window is constructed in absolute address order.
        Span<byte> stick = stackalloc byte[9];
        StickCalibration.Pack12Bit(stick, StickCenter, StickCenter);
        StickCalibration.Pack12Bit(stick[3..], StickRange, StickRange);
        StickCalibration.Pack12Bit(stick[6..], StickRange, StickRange);
        ReadOnlySpan<byte> serial = "NX2WIN0002\0"u8;

        for (int i = 0; i < length; i++)
        {
            uint offset = address + (uint)i;
            reply[8 + i] = offset switch
            {
                >= 0x13002 and < 0x13002 + 11 => serial[(int)(offset - 0x13002)],
                0x13014 => 0x69,
                0x13015 => 0x20,
                >= 0x130A8 and < 0x130B1 => stick[(int)(offset - 0x130A8)],
                >= 0x130E8 and < 0x130F1 => stick[(int)(offset - 0x130E8)],
                >= 0x13000 and < 0x13140 => 0,
                _ => 0xFF
            };
        }
        return reply;
    }
}
