using System.Buffers.Binary;
using NxToWin.Models;

namespace NxToWin.Hardware;

internal static class Switch2InputReport
{
    public static ControllerStateSnapshot? Parse(byte format, ReadOnlySpan<byte> data, ushort product, out bool hasMotion)
    {
        hasMotion = false;
        if (format == 5)
        {
            if (data.Length < 63) return null;
            var snapshot = new ControllerStateSnapshot
            {
                Timestamp = BinaryPrimitives.ReadUInt32LittleEndian(data),
                Buttons = (SwitchButtons)(BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) & 0x03FFFFFFu)
            };
            (snapshot.RawLeftStickX, snapshot.RawLeftStickY) = StickCalibration.Unpack12Bit(data[10..]);
            (snapshot.RawRightStickX, snapshot.RawRightStickY) = StickCalibration.Unpack12Bit(data[13..]);
            snapshot.MouseRawX = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
            snapshot.MouseRawY = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
            snapshot.MouseRoughness = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
            snapshot.MouseDistance = BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
            snapshot.MagX = ReadAxis(data, 25);
            snapshot.MagY = ReadAxis(data, 27);
            snapshot.MagZ = ReadAxis(data, 29);

            ushort mv = BinaryPrimitives.ReadUInt16LittleEndian(data[31..]);
            if (mv is >= 2500 and <= 5000)
                snapshot.BatteryVoltage = mv / 1000f;
            snapshot.Battery = BatteryReading.Switch2(mv, charge: data[33]);

            hasMotion = BinaryPrimitives.ReadUInt32LittleEndian(data[42..]) != 0;
            if (hasMotion)
            {
                snapshot.TemperatureC = 25 + ReadAxis(data, 46) / 126.9f;
                snapshot.AccelX = ReadAxis(data, 48);
                snapshot.AccelY = ReadAxis(data, 50);
                snapshot.AccelZ = ReadAxis(data, 52);
                snapshot.GyroX = ReadAxis(data, 54);
                snapshot.GyroY = ReadAxis(data, 56);
                snapshot.GyroZ = ReadAxis(data, 58);
            }

            if (product == NintendoIds.NsoGameCubePid)
            {
                var buttons = snapshot.Buttons;
                snapshot.Buttons = buttons & ~(SwitchButtons.R | SwitchButtons.L | SwitchButtons.ZL | SwitchButtons.ZR);
                if (buttons.HasFlag(SwitchButtons.R)) snapshot.Buttons |= SwitchButtons.GcRClick;
                if (buttons.HasFlag(SwitchButtons.L)) snapshot.Buttons |= SwitchButtons.GcLClick;
                if (buttons.HasFlag(SwitchButtons.ZR)) snapshot.Buttons |= SwitchButtons.R;
                if (buttons.HasFlag(SwitchButtons.ZL)) snapshot.Buttons |= SwitchButtons.L;
                snapshot.LeftTriggerAnalog = data[60];
                snapshot.RightTriggerAnalog = data[61];
            }
            else SetDigitalTriggers(snapshot);
            return snapshot;
        }

        if (format is not (7 or 8 or 9 or 10) || data.Length < 14) return null;
        var compact = new ControllerStateSnapshot { Timestamp = data[0], Battery = BatteryReading.Switch2(0, data[1]) };
        if (format is 9 or 10)
        {
            bool gameCube = format == 10;
            compact.Buttons = UnpackButtons(data[2], SwitchButtons.B, SwitchButtons.A, SwitchButtons.Y, SwitchButtons.X,
                SwitchButtons.R, gameCube ? SwitchButtons.GcRClick : SwitchButtons.ZR, SwitchButtons.Plus, SwitchButtons.RStick)
                | UnpackButtons(data[3], SwitchButtons.Down, SwitchButtons.Right, SwitchButtons.Left, SwitchButtons.Up,
                    gameCube ? SwitchButtons.GcLClick : SwitchButtons.L, gameCube ? SwitchButtons.L : SwitchButtons.ZL, SwitchButtons.Minus, SwitchButtons.LStick)
                | UnpackButtons(data[4], SwitchButtons.Home, SwitchButtons.Capture, SwitchButtons.GR, SwitchButtons.GL, SwitchButtons.ChatC);
            (compact.RawLeftStickX, compact.RawLeftStickY) = StickCalibration.Unpack12Bit(data[5..]);
            (compact.RawRightStickX, compact.RawRightStickY) = StickCalibration.Unpack12Bit(data[8..]);
            if (gameCube)
            {
                compact.LeftTriggerAnalog = data[12];
                compact.RightTriggerAnalog = data[13];
            }
            else SetDigitalTriggers(compact);
        }
        else if (format == 8)
        {
            compact.Buttons = UnpackButtons(data[2], SwitchButtons.B, SwitchButtons.A, SwitchButtons.Y, SwitchButtons.X,
                SwitchButtons.R, SwitchButtons.ZR, SwitchButtons.Plus, SwitchButtons.RStick)
                | UnpackButtons(data[3], SwitchButtons.Home, 0, 0, 0, SwitchButtons.ChatC, 0, SwitchButtons.SR_R, SwitchButtons.SL_R);
            (compact.RawRightStickX, compact.RawRightStickY) = StickCalibration.Unpack12Bit(data[5..]);
            SetDigitalTriggers(compact);
        }
        else
        {
            compact.Buttons = UnpackButtons(data[2], SwitchButtons.Down, SwitchButtons.Right, SwitchButtons.Left, SwitchButtons.Up,
                SwitchButtons.L, SwitchButtons.ZL, SwitchButtons.Minus, SwitchButtons.LStick)
                | UnpackButtons(data[3], SwitchButtons.Capture, 0, 0, 0, 0, 0, SwitchButtons.SR_L, SwitchButtons.SL_L);
            (compact.RawLeftStickX, compact.RawLeftStickY) = StickCalibration.Unpack12Bit(data[5..]);
            SetDigitalTriggers(compact);
        }
        return compact;
    }

    private static short ReadAxis(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);

    private static SwitchButtons UnpackButtons(byte bits, params SwitchButtons[] order)
    {
        SwitchButtons buttons = SwitchButtons.None;
        for (int i = 0; i < order.Length; i++)
            if ((bits & (1 << i)) != 0) buttons |= order[i];
        return buttons;
    }

    private static void SetDigitalTriggers(ControllerStateSnapshot state)
    {
        state.LeftTriggerAnalog = state.Buttons.HasFlag(SwitchButtons.ZL) ? (byte)255 : (byte)0;
        state.RightTriggerAnalog = state.Buttons.HasFlag(SwitchButtons.ZR) ? (byte)255 : (byte)0;
    }
}
