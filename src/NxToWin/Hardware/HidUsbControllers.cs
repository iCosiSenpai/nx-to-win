using System.IO;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NxToWin.Dsp;
using NxToWin.Models;

namespace NxToWin.Hardware;

internal static class HidApiNative
{
    private const string DllName = "hidapi.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct HidDeviceInfo
    {
        public IntPtr Path;
        public ushort VendorId;
        public ushort ProductId;
        public IntPtr SerialNumber;
        public ushort ReleaseNumber;
        public IntPtr ManufacturerString;
        public IntPtr ProductString;
        public ushort UsagePage;
        public ushort Usage;
        public int InterfaceNumber;
        public IntPtr Next;
        public int BusType;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hid_init();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hid_enumerate(ushort vendorId, ushort productId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void hid_free_enumeration(IntPtr devs);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hid_open_path([MarshalAs(UnmanagedType.LPStr)] string path);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void hid_close(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hid_write(IntPtr device, byte[] data, UIntPtr length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr hid_error(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hid_read_timeout(IntPtr device, byte[] data, UIntPtr length, int milliseconds);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int hid_set_nonblocking(IntPtr device, int nonblock);
}

public sealed class DiscoveredHidInfo
{
    public string Path { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public string Serial { get; init; } = "";
    public string Name { get; init; } = "";
    public int BusType { get; init; }

    public string Transport => BusType switch { 1 => "USB", 2 => "Bluetooth", _ => "HID" };
}

/// <summary>
/// Wired USB Switch 2 Pro Controller (0x2069) / NSO GameCube Controller (0x2073)
/// & Switch 1 Pro / Joy-Con (0x2009 / 0x2006 / 0x2007) via hidapi.dll.
/// </summary>
public sealed class HidNintendoController : IPhysicalController
{
    private readonly IntPtr _handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoopTask;
    private volatile StickCalibration _leftCal = new();
    private volatile StickCalibration _rightCal = new();
    private readonly BridgeConfiguration _config;
    private readonly string _path;
    private readonly byte[] _switch1Rumble = HdRumbleEngine.EncodeSwitch1Neutral();
    private ushort? _batteryMillivolts;
    private long _nextVoltageRead;
    private byte _packetCounter;
    private long _reportCount;
    private long _lastRateTick = Stopwatch.GetTimestamp();
    private double _measuredRateHz;
    private int _disposed;
    private volatile bool _rumbleWriteFailed;
    private readonly object _writeGate = new();

    public string Id { get; }
    public string Name { get; }
    public string TransportName { get; }
    internal string DevicePath => _path;
    public ushort ProductId { get; }
    public bool IsConnected { get; private set; }
    public bool IsSwitch2 => NintendoIds.IsSwitch2Pid(ProductId);
    public bool IsProController => NintendoIds.IsProController(ProductId);
    public bool IsLeftJoyCon => NintendoIds.IsLeftJoyCon(ProductId);
    public bool IsRightJoyCon => NintendoIds.IsRightJoyCon(ProductId);

    public ControllerStateSnapshot LatestState { get; private set; } = new();
    public ImuFusionEngine ImuFusion { get; } = new();

    public event Action<IPhysicalController, ControllerStateSnapshot>? StateUpdated;
    public event Action<IPhysicalController>? Disconnected;

    private HidNintendoController(IntPtr handle, DiscoveredHidInfo device, BridgeConfiguration config, Action<string> log)
    {
        _handle = handle;
        _path = device.Path;
        ushort pid = device.ProductId;
        ProductId = pid;
        _config = config;
        Id = GetId(_path, pid);
        Name = NintendoIds.GetProductName(pid);
        TransportName = IsSwitch2 ? "USB" : device.Transport;
        IsConnected = true;

        if (IsSwitch2)
        {
            try
            {
                using var vendor = Switch2UsbConnection.Open(_path, pid);
                vendor.Initialize(IsLeftJoyCon || IsRightJoyCon);
                if (pid != NintendoIds.NsoGameCubePid)
                {
                    try
                    {
                        if (!IsRightJoyCon) _leftCal = vendor.ReadCalibration(0x13080, 0x1FC040);
                        if (!IsLeftJoyCon) _rightCal = vendor.ReadCalibration(IsRightJoyCon ? 0x13080u : 0x130C0u, IsRightJoyCon ? 0x1FC040u : 0x1FC080u);
                    }
                    catch (Exception ex) { log($"{Name}: calibrazione USB non disponibile. {ex.Message}"); }
                }
                log($"{Name}: inizializzazione WinUSB completata.");
            }
            catch (Exception ex)
            {
                log($"{Name}: inizializzazione WinUSB non riuscita. {ex.Message}");
            }
        }
        else InitializeSwitch1Device();

        _readLoopTask = Task.Run(ReadLoopAsync);
    }

    public static string GetId(string path, ushort pid) => $"HID:{pid:X4}:{path.ToUpperInvariant()}";

    public static IEnumerable<DiscoveredHidInfo> EnumerateNintendoHidDevices() =>
        EnumerateHidDevices(NintendoIds.VendorId).Where(d => NintendoIds.IsSwitch2Pid(d.ProductId) || NintendoIds.IsSwitch1Pid(d.ProductId));

    public static List<DiscoveredHidInfo> EnumerateHidDevices(ushort vendor)
    {
        var list = new List<DiscoveredHidInfo>();
        HidApiNative.hid_init();
        IntPtr head = HidApiNative.hid_enumerate(vendor, 0);
        try
        {
            for (IntPtr current = head; current != IntPtr.Zero;)
            {
                var info = Marshal.PtrToStructure<HidApiNative.HidDeviceInfo>(current);
                current = info.Next;
                string path = Marshal.PtrToStringAnsi(info.Path) ?? "";
                string serial = Marshal.PtrToStringUni(info.SerialNumber) ?? "";
                if (path.Length == 0 || IsVirtualDevicePath(path, serial) ||
                    info.UsagePage != 1 || info.Usage is not (4 or 5)) continue;

                ushort pid = info.ProductId == NintendoIds.ChargingGripPid
                    ? info.InterfaceNumber == 1 ? NintendoIds.JoyCon1LeftPid : NintendoIds.JoyCon1RightPid
                    : info.ProductId;
                list.Add(new DiscoveredHidInfo
                {
                    Path = path, VendorId = info.VendorId, ProductId = pid,
                    UsagePage = info.UsagePage, Usage = info.Usage, Serial = serial,
                    BusType = info.BusType, Name = Marshal.PtrToStringUni(info.ProductString) ?? ""
                });
            }
        }
        finally
        {
            if (head != IntPtr.Zero) HidApiNative.hid_free_enumeration(head);
        }
        return list;
    }

    /// <summary>
    /// True for any *virtual* gamepad (ours or another tool's) that must never be treated as a physical controller.
    /// VHF children (WinUHid) enumerate as HID\HID_DEVICE_SYSTEM_VHF\..., so the VID/PID never appears in the path.
    /// USB/IP virtual pads are recognised by their synthetic serial strings.
    /// </summary>
    public static bool IsVirtualDevicePath(string path, string serial)
    {
        string p = path.ToLowerInvariant();
        if (p.Contains("hid_device_system_vhf") || p.Contains("winuhid") ||
            p.Contains("root#hidclass") || p.Contains("&ig_"))
            return true;

        string s = serial.Trim().ToUpperInvariant();
        return s.StartsWith("NX2WIN") || s.StartsWith("NXTOWIN") ||
               s.StartsWith("SWITCH2EMU") || s.StartsWith("PRO_") || s.StartsWith("JOYCON_");
    }

    public static HidNintendoController? TryOpen(DiscoveredHidInfo dev, BridgeConfiguration config, Action<string> log)
    {
        try
        {
            IntPtr handle = HidApiNative.hid_open_path(dev.Path);
            if (handle == IntPtr.Zero)
                return null;
            try { return new HidNintendoController(handle, dev, config, log); }
            catch
            {
                HidApiNative.hid_close(handle);
                throw;
            }
        }
        catch (Exception ex)
        {
            log($"Apertura HID: {ex.Message}");
            return null;
        }
    }

    private void InitializeSwitch1Device()
    {
        try
        {
            // USB handshake if wired
            SendSwitch1UsbCmd(0x02); // Handshake
            Thread.Sleep(15);
            SendSwitch1UsbCmd(0x04); // Disable USB timeout
            Thread.Sleep(15);

            // Enable 6-Axis IMU (0x40 = 0x01)
            SendSwitch1Subcommand(0x40, [0x01]);
            Thread.Sleep(20);

            // Enable HD Rumble (0x48 = 0x01)
            SendSwitch1Subcommand(0x48, [0x01]);
            Thread.Sleep(20);

            if (!IsRightJoyCon) _leftCal = ReadSwitch1Calibration(true);
            if (!IsLeftJoyCon) _rightCal = ReadSwitch1Calibration(false);

            // Standard full mode 60Hz (0x03 = 0x30)
            SendSwitch1Subcommand(0x03, [0x30]);
            Thread.Sleep(20);

            // Player LED 1 (0x30 = 0x01)
            SendSwitch1Subcommand(0x30, [0x01]);
        }
        catch
        {
            // Best effort
        }
    }

    private StickCalibration ReadSwitch1Calibration(bool left)
    {
        byte[] user = ReadSwitch1Flash(left ? 0x8010u : 0x801Bu, 11);
        if (user.Length == 11 && user[0] == 0xB2 && user[1] == 0xA1)
        {
            var calibration = StickCalibration.FromSwitch1SpiBytes(user.AsSpan(2), left);
            if (calibration.IsValid) return calibration;
        }
        return StickCalibration.FromSwitch1SpiBytes(ReadSwitch1Flash(left ? 0x603Du : 0x6046u, 9), left);
    }

    private byte[] ReadSwitch1Flash(uint address, byte length)
    {
        Span<byte> request = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(request, address);
        request[4] = length;
        SendSwitch1Subcommand(0x10, request);
        byte[] response = new byte[64];
        long until = Environment.TickCount64 + 200;
        while (Environment.TickCount64 < until)
        {
            int count = HidApiNative.hid_read_timeout(_handle, response, (UIntPtr)response.Length, 35);
            if (count < 0) break;
            if (count >= 20 + length && response[0] == 0x21 && response[13] == 0x90 &&
                response[14] == 0x10 && BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(15)) == address &&
                response[19] >= length)
                return response.AsSpan(20, length).ToArray();
        }
        return [];
    }

    private void SendSwitch1UsbCmd(byte usbCmd)
    {
        byte[] buf = new byte[64];
        buf[0] = 0x80;
        buf[1] = usbCmd;
        HidApiNative.hid_write(_handle, buf, (UIntPtr)buf.Length);
    }

    private void SendSwitch1Subcommand(byte subcmd, ReadOnlySpan<byte> args)
    {
        byte[] buf = new byte[49];
        buf[0] = 0x01;
        buf[1] = (byte)(_packetCounter++ & 0x0F);

        _switch1Rumble.CopyTo(buf, 2);

        buf[10] = subcmd;
        for (int i = 0; i < args.Length && (11 + i) < buf.Length; i++)
            buf[11 + i] = args[i];

        HidApiNative.hid_write(_handle, buf, (UIntPtr)buf.Length);
    }

    private async Task ReadLoopAsync()
    {
        byte[] buf = new byte[64];
        while (!_cts.IsCancellationRequested && IsConnected)
        {
            int read = HidApiNative.hid_read_timeout(_handle, buf, (UIntPtr)buf.Length, 50);
            if (read < 0)
            {
                IsConnected = false;
                Disconnected?.Invoke(this);
                break;
            }
            if (read == 0)
            {
                await Task.Yield();
                continue;
            }

            if (IsSwitch2)
            {
                ParseSwitch2UsbReport(buf.AsSpan(0, read));
            }
            else
            {
                ParseSwitch1HidReport(buf.AsSpan(0, read));
                long now = Environment.TickCount64;
                if (now >= _nextVoltageRead)
                {
                    _nextVoltageRead = now + 30000;
                    lock (_writeGate)
                        if (_disposed == 0 && !_rumbleWriteFailed) SendSwitch1Subcommand(0x50, []);
                }
            }
        }
    }

    private void ParseSwitch2UsbReport(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var snap = Switch2InputReport.Parse(data[0], data[1..], ProductId, out bool motion);
        if (snap == null) return;
        _reportCount++;
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastRateTick, now).TotalSeconds;
        if (elapsed >= 0.5)
        {
            _measuredRateHz = _reportCount / elapsed;
            _reportCount = 0;
            _lastRateTick = now;
        }
        snap.ReportRateHz = _measuredRateHz;
        if (!IsRightJoyCon)
            (snap.LeftStickX, snap.LeftStickY) = _leftCal.Normalize(snap.RawLeftStickX, snap.RawLeftStickY, _config.LeftStickDeadzone);
        if (!IsLeftJoyCon)
            (snap.RightStickX, snap.RightStickY) = _rightCal.Normalize(snap.RawRightStickX, snap.RawRightStickY, _config.RightStickDeadzone);
        if (motion)
        {
            float scale = IsProController ? ImuFusionEngine.Switch2ProReportGyroCountsPerDps
                : ImuFusionEngine.JoyCon2ReportGyroCountsPerDps;
            ImuFusion.ProcessSample(snap, scale, _config.Enable9AxisMagFusion);
        }
        LatestState = snap;
        StateUpdated?.Invoke(this, snap);
    }

    private void ParseSwitch1HidReport(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return;
        byte reportId = data[0];
        if (reportId == 0x21 && data.Length >= 17 && data[13] == 0xD0 && data[14] == 0x50)
        {
            int mv = BinaryPrimitives.ReadUInt16LittleEndian(data[15..]) * 5 / 2;
            if (mv is >= 2500 and <= 5000) _batteryMillivolts = (ushort)mv;
            return;
        }
        if (reportId != 0x30) return;

        _reportCount++;
        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedSec = (double)(nowTicks - _lastRateTick) / Stopwatch.Frequency;
        if (elapsedSec >= 0.5)
        {
            _measuredRateHz = _reportCount / elapsedSec;
            _reportCount = 0;
            _lastRateTick = nowTicks;
        }

        // Switch 1 0x30 report layout:
        // [0]=0x30, [1]=timer, [2]=bat/conn, [3..5]=buttons (Right, Shared, Left), [6..8]=LStick, [9..11]=RStick, [13..48]=3x IMU
        uint bRight = data[3];
        uint bShared = data[4];
        uint bLeft = data[5];
        var buttons = (SwitchButtons)(bRight | (bShared << 8) | (bLeft << 16));

        var (rawLx, rawLy) = StickCalibration.Unpack12Bit(data.Slice(6, 3));
        var (rawRx, rawRy) = StickCalibration.Unpack12Bit(data.Slice(9, 3));
        var (lx, ly) = _leftCal.Normalize(rawLx, rawLy, _config.LeftStickDeadzone);
        var (rx, ry) = _rightCal.Normalize(rawRx, rawRy, _config.RightStickDeadzone);

        if (IsLeftJoyCon) (rx, ry) = (0, 0);
        if (IsRightJoyCon) (lx, ly) = (0, 0);

        short ax = 0, ay = 0, az = 4096, gx = 0, gy = 0, gz = 0;
        if (reportId == 0x30 && data.Length >= 25)
        {
            // Read latest IMU sample (sample 2 at offset 37 if length >= 49, else sample 0 at offset 13)
            int imuOffset = data.Length >= 49 ? 37 : 13;
            ax = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(imuOffset + 0, 2));
            ay = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(imuOffset + 2, 2));
            az = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(imuOffset + 4, 2));
            gx = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(imuOffset + 6, 2));
            gy = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(imuOffset + 8, 2));
            gz = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(imuOffset + 10, 2));
        }

        var snap = new ControllerStateSnapshot
        {
            Timestamp = data[1],
            Buttons = buttons,
            LeftStickX = lx,
            LeftStickY = ly,
            RightStickX = rx,
            RightStickY = ry,
            RawLeftStickX = rawLx,
            RawLeftStickY = rawLy,
            RawRightStickX = rawRx,
            RawRightStickY = rawRy,
            LeftTriggerAnalog = buttons.HasFlag(SwitchButtons.ZL) ? (byte)255 : (byte)0,
            RightTriggerAnalog = buttons.HasFlag(SwitchButtons.ZR) ? (byte)255 : (byte)0,
            AccelX = ax,
            AccelY = ay,
            AccelZ = az,
            GyroX = gx,
            GyroY = gy,
            GyroZ = gz,
            Battery = BatteryReading.Switch1(data[2], _batteryMillivolts),
            ReportRateHz = _measuredRateHz
        };

        if (reportId == 0x30)
            ImuFusion.ProcessSample(snap, isProController: true, use9AxisMagnetometer: false); // Switch 1 = 0.070 dps/LSB on Pro and Joy-Con

        LatestState = snap;
        StateUpdated?.Invoke(this, snap);
    }

    public bool SupportsRumble => !_rumbleWriteFailed && ProductId != NintendoIds.ChargingGripPid;

    public Task SendHdRumbleAsync(StereoRumble rumble)
    {
        lock (_writeGate)
        {
            if (!IsConnected || _disposed != 0 || !SupportsRumble) return Task.CompletedTask;
            var scaled = rumble.WithStrength(_config.RumbleStrength);
            int sequence = _packetCounter++ & 0x0F;
            byte[] payload;
            if (ProductId == NintendoIds.NsoGameCubePid)
                payload = HdRumbleEngine.BuildGameCubeOutput(sequence, !scaled.IsSilent, wired: true);
            else if (IsSwitch2)
                payload = HdRumbleEngine.BuildSwitch2Report(sequence, scaled, wired: true, IsLeftJoyCon, IsRightJoyCon);
            else
            {
                HdRumbleEngine.WriteSwitch1Side(_switch1Rumble.AsSpan(0, 4), scaled.Left.Peak);
                HdRumbleEngine.WriteSwitch1Side(_switch1Rumble.AsSpan(4, 4), scaled.Right.Peak);
                payload = [0x10, (byte)sequence, .. _switch1Rumble];
            }

            if (HidApiNative.hid_write(_handle, payload, (UIntPtr)payload.Length) < 0)
            {
                // A timed-out HIDAPI write can remain pending. Wait for a new
                // connection before writing any further reports on this handle.
                _rumbleWriteFailed = true;
                string? error = Marshal.PtrToStringUni(HidApiNative.hid_error(_handle));
                throw new IOException($"Scrittura HID non riuscita · {Name} · {error}");
            }
        }
        return Task.CompletedTask;
    }
    public Task SetPlayerLedsAsync(int playerNumber)
    {
        if (IsSwitch2) return Task.CompletedTask;
        lock (_writeGate)
        {
            if (!IsConnected || _disposed != 0 || _rumbleWriteFailed) return Task.CompletedTask;
            byte pattern = playerNumber switch { 1 => 1, 2 => 3, 3 => 7, 4 => 15, _ => 0 };
            SendSwitch1Subcommand(0x30, [pattern]);
        }
        return Task.CompletedTask;
    }

    public void TriggerGyroCalibration() => ImuFusion.StartGyroCalibration();

    public void CenterSticks()
    {
        var sample = LatestState;
        if (!IsRightJoyCon) _leftCal = _leftCal.WithCenter(sample.RawLeftStickX, sample.RawLeftStickY);
        if (!IsLeftJoyCon) _rightCal = _rightCal.WithCenter(sample.RawRightStickX, sample.RawRightStickY);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IsConnected = false;
        _cts.Cancel();
        _ = CloseAfterReaderAsync();
    }

    private async Task CloseAfterReaderAsync()
    {
        try { await _readLoopTask.ConfigureAwait(false); }
        finally
        {
            lock (_writeGate) HidApiNative.hid_close(_handle);
            _cts.Dispose();
        }
    }
}

/// <summary>
/// Combines a Left Joy-Con (Switch 1 or 2) and a Right Joy-Con (Switch 1 or 2) into a single Dual-Joy-Con Pro Controller
/// with independent Left + Right LRA HD Rumble and unified 6-Axis IMU.
/// </summary>
public sealed class MergedJoyConPairController : IPhysicalController
{
    public IPhysicalController LeftJoyCon { get; }
    public IPhysicalController RightJoyCon { get; }

    public string Id { get; }
    public string Name { get; }
    public string TransportName => $"{LeftJoyCon.TransportName} (Dual L+R)";
    public ushort ProductId => LeftJoyCon.IsSwitch2 || RightJoyCon.IsSwitch2
        ? NintendoIds.ProController2Pid
        : NintendoIds.ProController1Pid;
    public bool IsConnected => LeftJoyCon.IsConnected && RightJoyCon.IsConnected;
    public bool IsSwitch2 => LeftJoyCon.IsSwitch2 || RightJoyCon.IsSwitch2;
    public bool IsProController => true;
    public bool IsLeftJoyCon => false;
    public bool IsRightJoyCon => false;

    public ControllerStateSnapshot LatestState { get; private set; } = new();
    public ImuFusionEngine ImuFusion => RightJoyCon.ImuFusion;

    public event Action<IPhysicalController, ControllerStateSnapshot>? StateUpdated;
    public event Action<IPhysicalController>? Disconnected;

    public MergedJoyConPairController(IPhysicalController left, IPhysicalController right)
    {
        LeftJoyCon = left;
        RightJoyCon = right;
        Id = $"PAIR:{left.Id}+{right.Id}";
        Name = IsSwitch2
            ? "Nintendo Switch 2 Dual Joy-Con 2 (L+R Combined)"
            : "Nintendo Switch Dual Joy-Con (L+R Combined)";

        LeftJoyCon.StateUpdated += OnHalfUpdated;
        RightJoyCon.StateUpdated += OnHalfUpdated;
        LeftJoyCon.Disconnected += OnHalfDisconnected;
        RightJoyCon.Disconnected += OnHalfDisconnected;
    }

    private void OnHalfDisconnected(IPhysicalController half)
    {
        Disconnected?.Invoke(this);
    }

    private void OnHalfUpdated(IPhysicalController sender, ControllerStateSnapshot _)
    {
        var l = LeftJoyCon.LatestState;
        var r = RightJoyCon.LatestState;

        var merged = new ControllerStateSnapshot
        {
            Timestamp = Math.Max(l.Timestamp, r.Timestamp),
            Buttons = l.Buttons | r.Buttons,
            LeftStickX = l.LeftStickX,
            LeftStickY = l.LeftStickY,
            RightStickX = r.RightStickX,
            RightStickY = r.RightStickY,
            RawLeftStickX = l.RawLeftStickX,
            RawLeftStickY = l.RawLeftStickY,
            RawRightStickX = r.RawRightStickX,
            RawRightStickY = r.RawRightStickY,
            LeftTriggerAnalog = l.LeftTriggerAnalog,
            RightTriggerAnalog = r.RightTriggerAnalog,
            AccelX = r.AccelX,
            AccelY = r.AccelY,
            AccelZ = r.AccelZ,
            GyroX = r.GyroX,
            GyroY = r.GyroY,
            GyroZ = r.GyroZ,
            AccelG_X = r.AccelG_X,
            AccelG_Y = r.AccelG_Y,
            AccelG_Z = r.AccelG_Z,
            GyroDps_X = r.GyroDps_X,
            GyroDps_Y = r.GyroDps_Y,
            GyroDps_Z = r.GyroDps_Z,
            MagX = r.MagX != 0 ? r.MagX : l.MagX,
            MagY = r.MagY != 0 ? r.MagY : l.MagY,
            MagZ = r.MagZ != 0 ? r.MagZ : l.MagZ,
            MouseRawX = r.MouseRawX != 0 ? r.MouseRawX : l.MouseRawX,
            MouseRawY = r.MouseRawY != 0 ? r.MouseRawY : l.MouseRawY,
            PitchDeg = r.PitchDeg,
            RollDeg = r.RollDeg,
            YawDeg = r.YawDeg,
            Battery = new BatteryReading($"{l.Battery.Level} / {r.Battery.Level}", "Joy-Con sinistro / destro",
                "Nintendo", "Letture individuali nella scheda Batterie."),
            ReportRateHz = Math.Max(l.ReportRateHz, r.ReportRateHz)
        };

        LatestState = merged;
        StateUpdated?.Invoke(this, merged);
    }

    public bool SupportsRumble => LeftJoyCon.SupportsRumble && RightJoyCon.SupportsRumble;

    public async Task SendHdRumbleAsync(StereoRumble rumble)
    {
        await Task.WhenAll(
            LeftJoyCon.SendHdRumbleAsync(new StereoRumble(rumble.Left, rumble.Left)),
            RightJoyCon.SendHdRumbleAsync(new StereoRumble(rumble.Right, rumble.Right))
        );
    }

    public async Task SetPlayerLedsAsync(int playerNumber)
    {
        await Task.WhenAll(
            LeftJoyCon.SetPlayerLedsAsync(playerNumber),
            RightJoyCon.SetPlayerLedsAsync(playerNumber)
        );
    }

    public void TriggerGyroCalibration()
    {
        LeftJoyCon.TriggerGyroCalibration();
        RightJoyCon.TriggerGyroCalibration();
    }

    public void CenterSticks()
    {
        LeftJoyCon.CenterSticks();
        RightJoyCon.CenterSticks();
    }

    public void Dispose()
    {
        LeftJoyCon.StateUpdated -= OnHalfUpdated;
        RightJoyCon.StateUpdated -= OnHalfUpdated;
        LeftJoyCon.Disconnected -= OnHalfDisconnected;
        RightJoyCon.Disconnected -= OnHalfDisconnected;
    }
}
