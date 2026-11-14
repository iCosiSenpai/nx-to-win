using System;

namespace NxToWin.Models;

public static class NintendoIds
{
    public const ushort VendorId = 0x057E;
    public const ushort BluetoothManufacturerId = 0x0553;

    // Switch 2 Product IDs
    public const ushort JoyCon2RightPid = 0x2066;
    public const ushort JoyCon2LeftPid = 0x2067;
    public const ushort ProController2Pid = 0x2069;
    public const ushort NsoGameCubePid = 0x2073;

    // Switch 1 Product IDs
    public const ushort JoyCon1LeftPid = 0x2006;
    public const ushort JoyCon1RightPid = 0x2007;
    public const ushort ProController1Pid = 0x2009;
    public const ushort ChargingGripPid = 0x200E;

    // Switch 2 BLE GATT UUIDs
    public static readonly Guid Sw2ServiceUuid = new("ab7de9be-89fe-49ad-828f-118f09df7fd0");
    public static readonly Guid InputReportUuid = new("ab7de9be-89fe-49ad-828f-118f09df7fd2");
    public static readonly Guid CommandWriteUuid = new("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");
    public static readonly Guid CommandResponseUuid = new("c765a961-d9d8-4d36-a20a-5315b111836a");
    public static readonly Guid VibrationPro2Uuid = new("cc483f51-9258-427d-a939-630c31f72b05");
    public static readonly Guid VibrationJoyCon2LeftUuid = new("289326cb-a471-485d-a8f4-240c14f18241");
    public static readonly Guid VibrationJoyCon2RightUuid = new("fa19b0fb-cd1f-46a7-84a1-bbb09e00c149");

    public static bool IsSwitch2Pid(ushort pid) =>
        pid is JoyCon2LeftPid or JoyCon2RightPid or ProController2Pid or NsoGameCubePid;

    public static bool IsSwitch1Pid(ushort pid) =>
        pid is JoyCon1LeftPid or JoyCon1RightPid or ProController1Pid or ChargingGripPid;

    public static bool IsLeftJoyCon(ushort pid) =>
        pid is JoyCon2LeftPid or JoyCon1LeftPid;

    public static bool IsRightJoyCon(ushort pid) =>
        pid is JoyCon2RightPid or JoyCon1RightPid;

    public static bool IsProController(ushort pid) =>
        pid is ProController2Pid or ProController1Pid or NsoGameCubePid;

    internal static SwitchButtons ConfigurableButtons(ushort pid) => pid switch
    {
        ProController2Pid => SwitchButtons.Capture | SwitchButtons.Home | SwitchButtons.ChatC | SwitchButtons.GL | SwitchButtons.GR,
        NsoGameCubePid => SwitchButtons.Capture | SwitchButtons.Home | SwitchButtons.ChatC,
        JoyCon2RightPid => SwitchButtons.Home | SwitchButtons.ChatC | SwitchButtons.SL_R | SwitchButtons.SR_R,
        JoyCon1RightPid => SwitchButtons.Home | SwitchButtons.SL_R | SwitchButtons.SR_R,
        JoyCon1LeftPid or JoyCon2LeftPid => SwitchButtons.Capture | SwitchButtons.SL_L | SwitchButtons.SR_L,
        _ => SwitchButtons.Capture | SwitchButtons.Home
    };

    public static string GetProductName(ushort pid) => pid switch
    {
        ProController2Pid => "Nintendo Switch 2 Pro Controller",
        JoyCon2LeftPid => "Nintendo Switch 2 Joy-Con 2 (L)",
        JoyCon2RightPid => "Nintendo Switch 2 Joy-Con 2 (R)",
        NsoGameCubePid => "NSO GameCube Controller (Switch 2)",
        ProController1Pid => "Nintendo Switch 1 Pro Controller",
        JoyCon1LeftPid => "Nintendo Switch 1 Joy-Con (L)",
        JoyCon1RightPid => "Nintendo Switch 1 Joy-Con (R)",
        ChargingGripPid => "Nintendo Switch Charging Grip",
        _ => $"Nintendo Controller (0x{pid:X4})"
    };
}

[Flags]
public enum SwitchButtons : uint
{
    None       = 0x00000000,
    Y          = 0x00000001,
    X          = 0x00000002,
    B          = 0x00000004,
    A          = 0x00000008,
    SR_R       = 0x00000010,
    SL_R       = 0x00000020,
    R          = 0x00000040,
    ZR         = 0x00000080,
    Minus      = 0x00000100,
    Plus       = 0x00000200,
    RStick     = 0x00000400,
    LStick     = 0x00000800,
    Home       = 0x00001000,
    Capture    = 0x00002000,
    ChatC      = 0x00004000, // Switch 2 C (Chat) button
    Down       = 0x00010000,
    Up         = 0x00020000,
    Right      = 0x00040000,
    Left       = 0x00080000,
    SR_L       = 0x00100000,
    SL_L       = 0x00200000,
    L          = 0x00400000,
    ZL         = 0x00800000,
    GR         = 0x01000000, // Switch 2 Pro Right Back Grip Button
    GL         = 0x02000000, // Switch 2 Pro Left Back Grip Button
    GcLClick   = 0x40000000, // GameCube Left Digital Click
    GcRClick   = 0x80000000, // GameCube Right Digital Click
}

public enum EmulationMode
{
    ViGEm_Xbox360,          // ViGEmBus Xbox 360 (XInput)
    ViGEm_DualShock4,       // ViGEmBus Sony DualShock 4 Controller (DirectInput / DS4 with Motion & Touchpad)
    SwitchPro_NativeVHF,    // WinUHid 0x057E:0x2009 Kernel VHF Switch Pro (Multi-Client Steam-Safe + 200Hz Gyro + LRA HD Rumble)
    Switch2Pro_NativeUSBIP, // USB/IP 0x057E:0x2069 Native Switch 2 Pro Controller (Bulk + HID + 40-Bit LRA HD Rumble + GL/GR/C)
    DualSense_PS5_VHF,      // WinUHid 0x054C:0x0CE6 Sony DualSense PS5 (6-Axis Gyro + Adaptive Trigger -> LRA HD Rumble)
    XboxOne_Impulse_VHF     // WinUHid 0x045E:0x02FF Xbox One Controller (4-Motor Impulse Triggers -> LRA HD Rumble)
}

public enum AbxyLayout
{
    Nintendo = 0, // Physical A=East, B=South, X=North, Y=West
    Xbox = 1,     // Swap A/B and X/Y positions
    Inverted = 3  // Swap A/Y and B/X positions
}

public enum ExtraButtonAction
{
    None,
    MapToA,
    MapToB,
    MapToX,
    MapToY,
    MapToL3,
    MapToR3,
    MapToL,
    MapToR,
    MapToZL,
    MapToZR,
    MapToDpadUp,
    MapToDpadDown,
    MapToDpadLeft,
    MapToDpadRight,
    MapToCapture,
    MapToHome,
    GyroAimHold,
    RecenterGyro,
    MacroCombo_B_Plus_X,
    MacroCombo_A_Plus_B,
    XboxGameBar,
    GameBarScreenshot,
    GameBarToggleRecording,
    GameBarRecordThat,
    GameBarMicToggle,
    VolumeUp,
    VolumeDown,
    VolumeMute,
    MediaPlayPause,
    TaskView,
    ShowDesktop,
    AltTab,
    AltF4,
    Escape
}

public sealed class StickCalibration
{
    public int CenterX { get; set; } = 2048;
    public int CenterY { get; set; } = 2048;
    public int MaxX { get; set; } = 1500;
    public int MaxY { get; set; } = 1500;
    public int MinX { get; set; } = 1500;
    public int MinY { get; set; } = 1500;
    public bool IsValid { get; set; }

    public static (int X, int Y) Unpack12Bit(ReadOnlySpan<byte> b)
    {
        if (b.Length < 3) return (2048, 2048);
        int x = b[0] | ((b[1] & 0x0F) << 8);
        int y = (b[1] >> 4) | (b[2] << 4);
        return (x, y);
    }

    public static void Pack12Bit(Span<byte> dest, int x, int y)
    {
        x = Math.Clamp(x, 0, 4095);
        y = Math.Clamp(y, 0, 4095);
        dest[0] = (byte)(x & 0xFF);
        dest[1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        dest[2] = (byte)((y >> 4) & 0xFF);
    }

    public static StickCalibration FromSwitch2SpiBytes(ReadOnlySpan<byte> data)
    {
        var cal = new StickCalibration();
        if (data.Length < 9) return cal;

        var (cx, cy) = Unpack12Bit(data.Slice(0, 3));
        var (maxX, maxY) = Unpack12Bit(data.Slice(3, 3));
        var (minX, minY) = Unpack12Bit(data.Slice(6, 3));

        bool validCenter = cx is >= 1024 and <= 3072 && cy is >= 1024 and <= 3072;
        bool validRange = maxX > 200 && maxY > 200 && minX > 200 && minY > 200
                          && maxX <= (4095 - cx) && maxY <= (4095 - cy)
                          && minX <= cx && minY <= cy;
        if (validCenter && validRange)
        {
            cal.CenterX = cx;
            cal.CenterY = cy;
            cal.MinX = minX;
            cal.MinY = minY;
            cal.MaxX = maxX;
            cal.MaxY = maxY;
            cal.IsValid = true;
        }
        return cal;
    }

    public static StickCalibration FromSwitch1SpiBytes(ReadOnlySpan<byte> data, bool isLeftStick)
    {
        var cal = new StickCalibration();
        if (data.Length < 9) return cal;

        int cx, cy, minX, minY, maxX, maxY;
        if (isLeftStick)
        {
            (maxX, maxY) = Unpack12Bit(data.Slice(0, 3));
            (cx, cy) = Unpack12Bit(data.Slice(3, 3));
            (minX, minY) = Unpack12Bit(data.Slice(6, 3));
        }
        else
        {
            (cx, cy) = Unpack12Bit(data.Slice(0, 3));
            (minX, minY) = Unpack12Bit(data.Slice(3, 3));
            (maxX, maxY) = Unpack12Bit(data.Slice(6, 3));
        }

        if (cx is >= 1024 and <= 3072 && cy is >= 1024 and <= 3072 &&
            maxX > 200 && maxY > 200 && minX > 200 && minY > 200)
        {
            cal.CenterX = cx;
            cal.CenterY = cy;
            cal.MinX = minX;
            cal.MinY = minY;
            cal.MaxX = maxX;
            cal.MaxY = maxY;
            cal.IsValid = true;
        }
        return cal;
    }

    public (float X, float Y) Normalize(int rawX, int rawY, float deadzone = 0.05f, float gain = 1.0f)
    {
        float dx = rawX - CenterX;
        float dy = rawY - CenterY;

        float nx = dx >= 0
            ? dx / Math.Max(1, MaxX)
            : dx / Math.Max(1, MinX);
        float ny = dy >= 0
            ? dy / Math.Max(1, MaxY)
            : dy / Math.Max(1, MinY);

        nx = Math.Clamp(nx * gain, -1f, 1f);
        ny = Math.Clamp(ny * gain, -1f, 1f);

        float mag = MathF.Sqrt(nx * nx + ny * ny);
        if (mag <= deadzone)
            return (0f, 0f);

        float scaled = Math.Min(1f, (mag - deadzone) / Math.Max(1e-4f, 1f - deadzone));
        float factor = scaled / mag;
        return (Math.Clamp(nx * factor, -1f, 1f), Math.Clamp(ny * factor, -1f, 1f));
    }

    public StickCalibration WithCenter(int x, int y)
    {
        if (x is < 1024 or > 3072 || y is < 1024 or > 3072) return this;
        return new StickCalibration
        {
            CenterX = x, CenterY = y, IsValid = IsValid,
            MaxX = Math.Max(200, CenterX + MaxX - x),
            MaxY = Math.Max(200, CenterY + MaxY - y),
            MinX = Math.Max(200, x - (CenterX - MinX)),
            MinY = Math.Max(200, y - (CenterY - MinY))
        };
    }
}

public sealed class ControllerStateSnapshot
{
    public uint Timestamp { get; set; }
    public SwitchButtons Buttons { get; set; }
    public float LeftStickX { get; set; }  // -1.0 .. +1.0
    public float LeftStickY { get; set; }  // -1.0 (down) .. +1.0 (up)
    public float RightStickX { get; set; } // -1.0 .. +1.0
    public float RightStickY { get; set; } // -1.0 (down) .. +1.0 (up)
    public int RawLeftStickX { get; set; } = 2048;
    public int RawLeftStickY { get; set; } = 2048;
    public int RawRightStickX { get; set; } = 2048;
    public int RawRightStickY { get; set; } = 2048;
    public byte LeftTriggerAnalog { get; set; }  // 0..255
    public byte RightTriggerAnalog { get; set; } // 0..255

    // Raw IMU values (Switch 2 / Switch 1 native scale: Accel 4096 LSB/g, Gyro ~14.2857 or 16.384 LSB/dps)
    public short AccelX { get; set; }
    public short AccelY { get; set; }
    public short AccelZ { get; set; }
    public short GyroX { get; set; }
    public short GyroY { get; set; }
    public short GyroZ { get; set; }

    // Physical units
    public float AccelG_X { get; set; }
    public float AccelG_Y { get; set; }
    public float AccelG_Z { get; set; } = 1.0f;
    public float GyroDps_X { get; set; }
    public float GyroDps_Y { get; set; }
    public float GyroDps_Z { get; set; }

    // Switch 2 3-Axis Magnetometer (raw LSB, 0.15 uT/LSB)
    public short MagX { get; set; }
    public short MagY { get; set; }
    public short MagZ { get; set; }

    // Switch 2 Joy-Con 2 Optical Mouse Sensor
    public ushort MouseRawX { get; set; }
    public ushort MouseRawY { get; set; }
    public ushort MouseRoughness { get; set; }
    public ushort MouseDistance { get; set; }

    // Orientation (from 6/9-Axis AHRS Fusion)
    public float PitchDeg { get; set; }
    public float RollDeg { get; set; }
    public float YawDeg { get; set; }

    // Telemetry
    public float BatteryVoltage { get; set; }
    public BatteryReading Battery { get; set; } = BatteryReading.Unknown;
    public float TemperatureC { get; set; } = 25.0f;
    public double ReportRateHz { get; set; }
}

public sealed class BridgeConfiguration
{
    public EmulationMode Mode { get; set; } = EmulationMode.ViGEm_Xbox360;
    public AbxyLayout Layout { get; set; } = AbxyLayout.Nintendo;
    public ExtraButtonAction GlAction { get; set; } = ExtraButtonAction.MapToL3;
    public ExtraButtonAction GrAction { get; set; } = ExtraButtonAction.MapToR3;
    public ExtraButtonAction SlAction { get; set; } = ExtraButtonAction.MapToL;
    public ExtraButtonAction SrAction { get; set; } = ExtraButtonAction.MapToR;

    public HostInput.HostButtonProcessor HostButtons { get; } = new();

    public float LeftStickDeadzone { get; set; } = 0.05f;
    public float RightStickDeadzone { get; set; } = 0.05f;
    public float RumbleStrength { get; set; } = 1.15f; // 0.0 .. 2.5
    public bool Enable9AxisMagFusion { get; set; } = true;
    public bool EnableJoyCon2OpticalMouse { get; set; } = false;
    public float OpticalMouseSensitivity { get; set; } = 1.2f;
    public bool EnableCemuhookDsu { get; set; } = true;
    public int DsuPort { get; set; } = 26760;
    public bool AutoMergeJoyCons { get; set; } = true;
    public bool SteamMultiClientGuard { get; set; } = true;
}
