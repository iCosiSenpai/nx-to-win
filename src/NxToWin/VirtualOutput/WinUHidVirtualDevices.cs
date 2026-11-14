using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NxToWin.Dsp;
using NxToWin.HostInput;
using NxToWin.Models;

namespace NxToWin.VirtualOutput;

public interface IVirtualOutputDevice : IDisposable
{
    string Name { get; }
    EmulationMode Mode { get; }
    bool IsRunning { get; }
    long InputReportsSent { get; }
    long RumblePacketsReceived { get; }
    VibrationFrame LastLeftRumble { get; }
    VibrationFrame LastRightRumble { get; }

    event Action<StereoRumble>? HdRumbleReceived;
    event Action<int>? PlayerLedChanged;
    event Action<string>? LogMessage;

    bool Start();
    void UpdateControllerState(RoutedInput input, BridgeConfiguration config);
    void Stop();
}

public sealed record RoutedInput(ControllerStateSnapshot State, SwitchButtons Buttons, bool SuppressGyro = false);

internal static class WinUHidNative
{
    public const string WinUHidDll = "WinUHid.dll";
    public const string WinUHidDevsDll = "WinUHidDevs.dll";

    // Exact bitmask flags from WinUHid.h (_WINUHID_EVENT_TYPE)
    public const int WINUHID_EVENT_NONE = 0x0;
    public const int WINUHID_EVENT_GET_FEATURE = 0x1;
    public const int WINUHID_EVENT_SET_FEATURE = 0x2;
    public const int WINUHID_EVENT_WRITE_REPORT = 0x4;
    public const int WINUHID_EVENT_READ_REPORT = 0x8;

    // Exact #include <pshpack1.h> layout from WinUHid.h (_WINUHID_DEVICE_CONFIG)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WinUHidDeviceConfig
    {
        public int SupportedEvents;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
        public ushort ReportDescriptorLength;
        public IntPtr ReportDescriptor;
        public Guid ContainerId;
        public IntPtr InstanceID;
        public IntPtr HardwareIDs;
        public uint ReadReportPeriodUs;
    }

    // Exact callback signature from WinUHid.h:
    // typedef VOID WINUHID_EVENT_CALLBACK(PVOID CallbackContext, PWINUHID_DEVICE Device, PCWINUHID_EVENT Event);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WinUHidEventCallback(IntPtr callbackContext, IntPtr device, IntPtr evtPtr);

    [DllImport(WinUHidDll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint WinUHidGetDriverInterfaceVersion();

    [DllImport(WinUHidDll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr WinUHidCreateDevice(ref WinUHidDeviceConfig config);

    [DllImport(WinUHidDll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUHidStartDevice(IntPtr device, WinUHidEventCallback callback, IntPtr callbackContext);

    [DllImport(WinUHidDll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUHidSubmitInputReport(IntPtr device, byte[] report, uint reportSize);

    [DllImport(WinUHidDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidCompleteWriteEvent(IntPtr device, IntPtr evtPtr, [MarshalAs(UnmanagedType.Bool)] bool success);

    [DllImport(WinUHidDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidCompleteReadEvent(IntPtr device, IntPtr evtPtr, byte[]? data, uint dataLen);

    [DllImport(WinUHidDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidStopDevice(IntPtr device);

    [DllImport(WinUHidDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidDestroyDevice(IntPtr device);

    // =========================================================================
    // WinUHidDevs.dll — Sony DualSense PS5 (WinUHidPS5.h)
    // =========================================================================
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WinUHidPS5GamepadInfo
    {
        public IntPtr BasicInfo;
        public fixed byte MacAddress[6];
        public IntPtr FirmwareInfo;
        public byte FirmwareInfoLength;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void Ps5RumbleCallback(IntPtr ctx, byte leftMotor, byte rightMotor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void Ps5LightbarLedCallback(IntPtr ctx, byte r, byte g, byte b);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void Ps5PlayerLedCallback(IntPtr ctx, byte ledVal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void Ps5TriggerEffectCallback(IntPtr ctx, IntPtr leftEffect, IntPtr rightEffect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void Ps5MicLedCallback(IntPtr ctx, byte ledState);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr WinUHidPS5Create(
        ref WinUHidPS5GamepadInfo info,
        Ps5RumbleCallback? rumbleCb,
        Ps5LightbarLedCallback? lightbarCb,
        Ps5PlayerLedCallback? playerLedCb,
        Ps5TriggerEffectCallback? triggerCb,
        Ps5MicLedCallback? micLedCb,
        IntPtr callbackContext);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidPS5InitializeInputReport(byte[] report);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidPS5SetHatState(byte[] report, int hatX, int hatY);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidPS5SetBatteryState(byte[] report, [MarshalAs(UnmanagedType.Bool)] bool wired, byte percentage);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidPS5SetTouchState(byte[] report, byte touchIndex,
        [MarshalAs(UnmanagedType.Bool)] bool touchDown, ushort x, ushort y);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidPS5SetAccelState(byte[] report, float accelX, float accelY, float accelZ);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidPS5SetGyroState(byte[] report, float gyroX, float gyroY, float gyroZ);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUHidPS5ReportInput(IntPtr gamepad, byte[] report);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidPS5Destroy(IntPtr gamepad);

    // =========================================================================
    // WinUHidDevs.dll — Xbox One Controller (WinUHidXOne.h)
    // =========================================================================
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void XOneForceFeedbackCallback(IntPtr ctx, byte leftMotor, byte rightMotor, byte leftTriggerMotor, byte rightTriggerMotor);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr WinUHidXOneCreate(IntPtr info, XOneForceFeedbackCallback? callback, IntPtr callbackContext);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidXOneInitializeInputReport(byte[] report);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidXOneSetHatState(byte[] report, int hatX, int hatY);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUHidXOneReportInput(IntPtr gamepad, byte[] report);

    [DllImport(WinUHidDevsDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void WinUHidXOneDestroy(IntPtr gamepad);
}

public static class ButtonRemapper
{
    public static SwitchButtons ApplyRemapping(
        ControllerStateSnapshot state,
        BridgeConfiguration config,
        out bool gyroRecenterRequested,
        out bool gyroSuppressed)
    {
        gyroRecenterRequested = false;
        gyroSuppressed = false;

        SwitchButtons btns = config.HostButtons.Process(state.Buttons, out var newlyPressed);

        if (config.Layout == AbxyLayout.Xbox)
        {
            bool a = btns.HasFlag(SwitchButtons.A);
            bool b = btns.HasFlag(SwitchButtons.B);
            bool x = btns.HasFlag(SwitchButtons.X);
            bool y = btns.HasFlag(SwitchButtons.Y);

            btns &= ~(SwitchButtons.A | SwitchButtons.B | SwitchButtons.X | SwitchButtons.Y);
            if (a) btns |= SwitchButtons.B; // Nintendo A (East) -> Xbox B (East)
            if (b) btns |= SwitchButtons.A; // Nintendo B (South) -> Xbox A (South)
            if (x) btns |= SwitchButtons.Y; // Nintendo X (North) -> Xbox Y (North)
            if (y) btns |= SwitchButtons.X; // Nintendo Y (West) -> Xbox X (West)
        }
        else if (config.Layout == AbxyLayout.Inverted)
        {
            bool a = btns.HasFlag(SwitchButtons.A);
            bool b = btns.HasFlag(SwitchButtons.B);
            bool x = btns.HasFlag(SwitchButtons.X);
            bool y = btns.HasFlag(SwitchButtons.Y);

            btns &= ~(SwitchButtons.A | SwitchButtons.B | SwitchButtons.X | SwitchButtons.Y);
            if (a) btns |= SwitchButtons.Y;
            if (b) btns |= SwitchButtons.X;
            if (x) btns |= SwitchButtons.B;
            if (y) btns |= SwitchButtons.A;
        }

        ApplyMappedButton(SwitchButtons.GL, config.GlAction, config, newlyPressed, ref btns, ref gyroRecenterRequested, ref gyroSuppressed);
        ApplyMappedButton(SwitchButtons.GR, config.GrAction, config, newlyPressed, ref btns, ref gyroRecenterRequested, ref gyroSuppressed);

        ApplyMappedButton(SwitchButtons.SL_L | SwitchButtons.SL_R, config.SlAction, config, newlyPressed, ref btns, ref gyroRecenterRequested, ref gyroSuppressed);
        ApplyMappedButton(SwitchButtons.SR_L | SwitchButtons.SR_R, config.SrAction, config, newlyPressed, ref btns, ref gyroRecenterRequested, ref gyroSuppressed);

        bool extraRecenter = config.HostButtons.ExtraRecenterPressed(gyroRecenterRequested);
        gyroRecenterRequested = config.HostButtons.ConsumeRecenterRequest() || extraRecenter;
        return btns;
    }

    internal static HostAction? GetWindowsAction(ExtraButtonAction action) => action switch
    {
        ExtraButtonAction.XboxGameBar => HostAction.XboxGameBar,
        ExtraButtonAction.GameBarScreenshot => HostAction.GameBarScreenshot,
        ExtraButtonAction.GameBarToggleRecording => HostAction.GameBarToggleRecording,
        ExtraButtonAction.GameBarRecordThat => HostAction.GameBarRecordThat,
        ExtraButtonAction.GameBarMicToggle => HostAction.GameBarMicToggle,
        ExtraButtonAction.VolumeUp => HostAction.VolumeUp,
        ExtraButtonAction.VolumeDown => HostAction.VolumeDown,
        ExtraButtonAction.VolumeMute => HostAction.VolumeMute,
        ExtraButtonAction.MediaPlayPause => HostAction.MediaPlayPause,
        ExtraButtonAction.TaskView => HostAction.TaskView,
        ExtraButtonAction.ShowDesktop => HostAction.ShowDesktop,
        ExtraButtonAction.AltTab => HostAction.AltTab,
        ExtraButtonAction.AltF4 => HostAction.AltF4,
        ExtraButtonAction.Escape => HostAction.Escape,
        _ => null
    };

    private static void ApplyMappedButton(SwitchButtons source, ExtraButtonAction action, BridgeConfiguration config,
        SwitchButtons newlyPressed, ref SwitchButtons buttons, ref bool recenter, ref bool suppressGyro)
    {
        if (action == ExtraButtonAction.None || (buttons & source) == 0) return;
        buttons &= ~source;
        if (GetWindowsAction(action) is HostAction windowsAction)
        {
            if ((newlyPressed & source) != 0) config.HostButtons.Trigger(windowsAction);
        }
        else ApplyExtraAction(action, ref buttons, ref recenter, ref suppressGyro);
    }

    private static void ApplyExtraAction(
        ExtraButtonAction action,
        ref SwitchButtons btns,
        ref bool recenter,
        ref bool suppressGyro)
    {
        switch (action)
        {
            case ExtraButtonAction.MapToA: btns |= SwitchButtons.A; break;
            case ExtraButtonAction.MapToB: btns |= SwitchButtons.B; break;
            case ExtraButtonAction.MapToX: btns |= SwitchButtons.X; break;
            case ExtraButtonAction.MapToY: btns |= SwitchButtons.Y; break;
            case ExtraButtonAction.MapToL3: btns |= SwitchButtons.LStick; break;
            case ExtraButtonAction.MapToR3: btns |= SwitchButtons.RStick; break;
            case ExtraButtonAction.MapToL: btns |= SwitchButtons.L; break;
            case ExtraButtonAction.MapToR: btns |= SwitchButtons.R; break;
            case ExtraButtonAction.MapToZL: btns |= SwitchButtons.ZL; break;
            case ExtraButtonAction.MapToZR: btns |= SwitchButtons.ZR; break;
            case ExtraButtonAction.MapToDpadUp: btns |= SwitchButtons.Up; break;
            case ExtraButtonAction.MapToDpadDown: btns |= SwitchButtons.Down; break;
            case ExtraButtonAction.MapToDpadLeft: btns |= SwitchButtons.Left; break;
            case ExtraButtonAction.MapToDpadRight: btns |= SwitchButtons.Right; break;
            case ExtraButtonAction.MapToCapture: btns |= SwitchButtons.Capture; break;
            case ExtraButtonAction.MapToHome: btns |= SwitchButtons.Home; break;
            case ExtraButtonAction.MacroCombo_B_Plus_X: btns |= SwitchButtons.B | SwitchButtons.X; break;
            case ExtraButtonAction.MacroCombo_A_Plus_B: btns |= SwitchButtons.A | SwitchButtons.B; break;
            case ExtraButtonAction.RecenterGyro: recenter = true; break;
            case ExtraButtonAction.GyroAimHold: suppressGyro = true; break;
        }
    }
}

/// <summary>
/// Kernel VHF Virtual Nintendo Switch Pro Controller (0x057E:0x2009) via WinUHid.dll.
/// Uses the exact WINUHID_DEVICE_CONFIG (Pack = 1) ABI from WinUHid.h.
/// </summary>
public sealed class WinUHidSwitchProVirtualDevice : IVirtualOutputDevice
{
    private static readonly byte[] Switch1ProReportDescriptor =
    [
        0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x06, 0x01, 0xFF,
        0x85, 0x21, 0x09, 0x21, 0x75, 0x08, 0x95, 0x3F, 0x81, 0x03,
        0x85, 0x30, 0x09, 0x30, 0x75, 0x08, 0x95, 0x3F, 0x81, 0x03,
        0x85, 0x31, 0x09, 0x31, 0x75, 0x08, 0x96, 0x69, 0x01, 0x81, 0x03,
        0x85, 0x32, 0x09, 0x32, 0x75, 0x08, 0x96, 0x69, 0x01, 0x81, 0x03,
        0x85, 0x33, 0x09, 0x33, 0x75, 0x08, 0x96, 0x69, 0x01, 0x81, 0x03,
        0x85, 0x3F,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x10, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x10, 0x81, 0x02,
        0x05, 0x01, 0x09, 0x39, 0x15, 0x00, 0x25, 0x07, 0x75, 0x04, 0x95, 0x01, 0x81, 0x42,
        0x05, 0x09, 0x75, 0x04, 0x95, 0x01, 0x81, 0x01,
        0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x33, 0x09, 0x34, 0x16, 0x00, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00, 0x75, 0x10, 0x95, 0x04, 0x81, 0x02,
        0x06, 0x01, 0xFF,
        0x85, 0x81, 0x09, 0x81, 0x75, 0x08, 0x95, 0x3F, 0x81, 0x03,
        0x85, 0x01, 0x09, 0x01, 0x75, 0x08, 0x95, 0x3F, 0x91, 0x83,
        0x85, 0x10, 0x09, 0x10, 0x75, 0x08, 0x95, 0x3F, 0x91, 0x83,
        0x85, 0x80, 0x09, 0x80, 0x75, 0x08, 0x95, 0x3F, 0x91, 0x83,
        0x85, 0x82, 0x09, 0x82, 0x75, 0x08, 0x95, 0x3F, 0x91, 0x83,
        0xC0
    ];

    private readonly byte[] _spi = new byte[65536];
    private readonly byte[] _mac = [0x98, 0xB6, 0xE9, 0x42, 0x10, 0x77];
    private WinUHidNative.WinUHidEventCallback? _callbackDelegate;
    private IntPtr _deviceHandle;
    private CancellationTokenSource? _cts;
    private Task? _streamTask;

    private volatile RoutedInput _latestInput = new(new(), SwitchButtons.None);
    private BridgeConfiguration _latestConfig = new();
    private bool _usbStreamingEnabled;
    private bool _reportedGuardSleep;
    private bool _imuEnabled = true;
    private byte _inputReportMode = 0x30;
    private int _timer;
    private long _inputReportsSent;
    private long _rumblePacketsReceived;

    public string Name => "Nintendo Switch Pro Controller (Kernel VHF 0x057E:0x2009)";
    public EmulationMode Mode { get; }
    public bool IsRunning => _deviceHandle != IntPtr.Zero;
    public long InputReportsSent => Interlocked.Read(ref _inputReportsSent);
    public long RumblePacketsReceived => Interlocked.Read(ref _rumblePacketsReceived);
    public VibrationFrame LastLeftRumble { get; private set; } = VibrationFrame.Silence;
    public VibrationFrame LastRightRumble { get; private set; } = VibrationFrame.Silence;

    public event Action<StereoRumble>? HdRumbleReceived;
    public event Action<int>? PlayerLedChanged;
    public event Action<string>? LogMessage;

    public WinUHidSwitchProVirtualDevice(EmulationMode mode = EmulationMode.SwitchPro_NativeVHF)
    {
        Mode = mode;
        InitializeVirtualSpiFlash();
    }

    private void InitializeVirtualSpiFlash()
    {
        Array.Fill(_spi, (byte)0xFF);

        byte[] serial = "NX2WIN00000001\0\0"u8.ToArray();
        Buffer.BlockCopy(serial, 0, _spi, 0x6000, 16);

        _spi[0x6012] = 0x03; // Pro Controller
        _spi[0x601B] = 0x01; // Color info present

        byte[] colors = [0x2D, 0x2D, 0x32, 0xE6, 0xE6, 0xEB, 0x23, 0x23, 0x28, 0x23, 0x23, 0x28];
        Buffer.BlockCopy(colors, 0, _spi, 0x6050, colors.Length);

        Span<byte> imuCal = _spi.AsSpan(0x6020, 24);
        imuCal.Clear();
        BinaryPrimitives.WriteInt16LittleEndian(imuCal.Slice(6, 2), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(imuCal.Slice(8, 2), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(imuCal.Slice(10, 2), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(imuCal.Slice(18, 2), 13371);
        BinaryPrimitives.WriteInt16LittleEndian(imuCal.Slice(20, 2), 13371);
        BinaryPrimitives.WriteInt16LittleEndian(imuCal.Slice(22, 2), 13371);

        StickCalibration.Pack12Bit(_spi.AsSpan(0x603D, 3), 1638, 1638);
        StickCalibration.Pack12Bit(_spi.AsSpan(0x6040, 3), 2048, 2048);
        StickCalibration.Pack12Bit(_spi.AsSpan(0x6043, 3), 1638, 1638);

        StickCalibration.Pack12Bit(_spi.AsSpan(0x6046, 3), 2048, 2048);
        StickCalibration.Pack12Bit(_spi.AsSpan(0x6049, 3), 1638, 1638);
        StickCalibration.Pack12Bit(_spi.AsSpan(0x604C, 3), 1638, 1638);

        byte[] stickParams =
        [
            0x50, 0xFD, 0x00, 0x00, 0xC6, 0x0F, 0x0F, 0x30, 0x61, 0x96, 0x30, 0xF3,
            0xD4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xC7, 0x79, 0x9C, 0x33, 0x36, 0x63
        ];
        Buffer.BlockCopy(stickParams, 0, _spi, 0x6080, stickParams.Length);
    }

    public bool Start()
    {
        if (IsRunning) return true;

        _callbackDelegate = OnWinUHidEvent;
        IntPtr descPtr = Marshal.AllocHGlobal(Switch1ProReportDescriptor.Length);

        try
        {
            Marshal.Copy(Switch1ProReportDescriptor, 0, descPtr, Switch1ProReportDescriptor.Length);

            var cfg = new WinUHidNative.WinUHidDeviceConfig
            {
                SupportedEvents = WinUHidNative.WINUHID_EVENT_WRITE_REPORT | WinUHidNative.WINUHID_EVENT_SET_FEATURE,
                VendorID = NintendoIds.VendorId,
                ProductID = NintendoIds.ProController1Pid, // 0x2009
                VersionNumber = 0x0200,
                ReportDescriptorLength = (ushort)Switch1ProReportDescriptor.Length,
                ReportDescriptor = descPtr,
                ContainerId = Guid.NewGuid(),
                InstanceID = IntPtr.Zero,
                HardwareIDs = IntPtr.Zero,
                ReadReportPeriodUs = 0
            };

            _deviceHandle = WinUHidNative.WinUHidCreateDevice(ref cfg);
            if (_deviceHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LogMessage?.Invoke($"WinUHidCreateDevice failed (Win32 error {err}). Ensure WinUHid driver is installed.");
                return false;
            }

            if (!WinUHidNative.WinUHidStartDevice(_deviceHandle, _callbackDelegate, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                WinUHidNative.WinUHidDestroyDevice(_deviceHandle);
                _deviceHandle = IntPtr.Zero;
                LogMessage?.Invoke($"WinUHidStartDevice failed (Win32 error {err}).");
                return false;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _streamTask = Task.Run(() => StreamInputLoopAsync(token));
            LogMessage?.Invoke($"Kernel VHF Virtual Device started: {Name}");
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(descPtr);
        }
    }

    public void UpdateControllerState(RoutedInput input, BridgeConfiguration config)
    {
        _latestInput = input;
        _latestConfig = config;
    }

    private void OnWinUHidEvent(IntPtr callbackContext, IntPtr device, IntPtr evtPtr)
    {
        if (evtPtr == IntPtr.Zero || _deviceHandle == IntPtr.Zero) return;

        // Read packed WINUHID_EVENT fields from evtPtr (Pack = 1):
        // Offset 0: int Type
        // Offset 4: uint RequestId
        // Offset 8: byte ReportId
        // Offset 9: uint DataLength
        // Offset 13: byte Data[DataLength]
        int evtType = Marshal.ReadInt32(evtPtr, 0);
        int dataLen = Marshal.ReadInt32(evtPtr, 9);

        if (evtType == WinUHidNative.WINUHID_EVENT_WRITE_REPORT ||
            evtType == WinUHidNative.WINUHID_EVENT_SET_FEATURE)
        {
            byte[] buf = [];
            if (dataLen is > 0 and <= 1024)
            {
                buf = new byte[dataLen];
                Marshal.Copy(evtPtr + 13, buf, 0, dataLen);
            }

            WinUHidNative.WinUHidCompleteWriteEvent(_deviceHandle, evtPtr, true);

            if (buf.Length > 0)
            {
                HandleHostOutputReport(buf);
            }
        }
        else if (evtType == WinUHidNative.WINUHID_EVENT_READ_REPORT ||
                 evtType == WinUHidNative.WINUHID_EVENT_GET_FEATURE)
        {
            byte[] resp = Build0x30InputReport();
            WinUHidNative.WinUHidCompleteReadEvent(_deviceHandle, evtPtr, resp, (uint)resp.Length);
        }
    }

    private void HandleHostOutputReport(ReadOnlySpan<byte> buf)
    {
        byte reportId = buf[0];

        if (reportId == 0x80 && buf.Length >= 2)
        {
            byte sub = buf[1];
            byte[] resp = new byte[64];
            resp[0] = 0x81;
            resp[1] = sub;

            switch (sub)
            {
                case 0x01:
                    resp[2] = 0x00;
                    resp[3] = 0x03; // Pro Controller
                    for (int i = 0; i < 6; i++)
                        resp[4 + i] = _mac[5 - i];
                    break;
                case 0x02:
                case 0x03:
                    break;
                case 0x04:
                    if (!_usbStreamingEnabled)
                    {
                        _usbStreamingEnabled = true;
                        LogMessage?.Invoke("Switch Pro VHF: streaming HID attivato (0x80 0x04).");
                    }
                    break;
                case 0x05:
                    if (!_latestConfig.SteamMultiClientGuard)
                    {
                        _usbStreamingEnabled = false;
                        _reportedGuardSleep = false;
                    }
                    else if (!_reportedGuardSleep)
                    {
                        _reportedGuardSleep = true;
                        LogMessage?.Invoke("Protezione Steam: richiesta di sospensione HID ignorata.");
                    }
                    break;
            }

            WinUHidNative.WinUHidSubmitInputReport(_deviceHandle, resp, (uint)resp.Length);
        }
        else if (reportId == 0x01 && buf.Length >= 11)
        {
            DecodeAndDispatchSwitch1Rumble(buf.Slice(2, 8));

            byte subcmd = buf[10];
            ReadOnlySpan<byte> subData = buf.Slice(11);
            byte[] resp = BuildSubcommandReply(subcmd, subData);
            WinUHidNative.WinUHidSubmitInputReport(_deviceHandle, resp, (uint)resp.Length);
        }
        else if (reportId == 0x10 && buf.Length >= 10)
        {
            DecodeAndDispatchSwitch1Rumble(buf.Slice(2, 8));
        }
    }

    private void DecodeAndDispatchSwitch1Rumble(ReadOnlySpan<byte> rumble8)
    {
        if (rumble8.Length < 8) return;
        bool isNeutral = rumble8[0] == 0x00 && rumble8[1] == 0x01 && rumble8[2] == 0x40 && rumble8[3] == 0x40 &&
                         rumble8[4] == 0x00 && rumble8[5] == 0x01 && rumble8[6] == 0x40 && rumble8[7] == 0x40;

        VibrationFrame left = isNeutral ? VibrationFrame.Silence : HdRumbleEngine.DecodeSwitch1Side(rumble8.Slice(0, 4));
        VibrationFrame right = isNeutral ? VibrationFrame.Silence : HdRumbleEngine.DecodeSwitch1Side(rumble8.Slice(4, 4));

        LastLeftRumble = left;
        LastRightRumble = right;
        Interlocked.Increment(ref _rumblePacketsReceived);
        HdRumbleReceived?.Invoke(new StereoRumble(left, right));
    }

    private byte[] BuildSubcommandReply(byte subcmd, ReadOnlySpan<byte> subData)
    {
        byte[] resp = new byte[64];
        resp[0] = 0x21;
        resp[1] = (byte)Interlocked.Increment(ref _timer);
        FillStandardButtonsAndSticks(resp.AsSpan(2, 10), _latestInput);
        resp[12] = 0x00;

        byte ack = 0x80;
        Span<byte> replyPayload = resp.AsSpan(15);

        switch (subcmd)
        {
            case 0x01:
                ack = 0x81;
                replyPayload[0] = 0x03;
                break;

            case 0x02:
                ack = 0x82;
                replyPayload[0] = 0x04;
                replyPayload[1] = 0x33;
                replyPayload[2] = 0x03; // Pro Controller
                replyPayload[3] = 0x02;
                Buffer.BlockCopy(_mac, 0, resp, 15 + 4, 6);
                replyPayload[10] = 0x03;
                replyPayload[11] = 0x02;
                break;

            case 0x03:
                if (subData.Length > 0)
                {
                    byte requestedMode = subData[0];
                    if (_latestConfig.SteamMultiClientGuard && requestedMode == 0x3F && _inputReportMode == 0x30)
                    {
                        LogMessage?.Invoke("Steam Multi-Client Guard: blocked 0x3F downgrade, keeping 0x30 Full IMU mode active.");
                    }
                    else
                    {
                        _inputReportMode = requestedMode;
                    }
                }
                break;

            case 0x04:
                ack = 0x83;
                break;

            case 0x10:
                ack = 0x90;
                if (subData.Length >= 5)
                {
                    uint addr = BinaryPrimitives.ReadUInt32LittleEndian(subData.Slice(0, 4));
                    byte len = Math.Min(subData[4], (byte)0x1D);
                    BinaryPrimitives.WriteUInt32LittleEndian(replyPayload.Slice(0, 4), addr);
                    replyPayload[4] = len;
                    for (int i = 0; i < len; i++)
                    {
                        uint idx = addr + (uint)i;
                        replyPayload[5 + i] = idx < _spi.Length ? _spi[idx] : (byte)0xFF;
                    }
                }
                break;

            case 0x30:
                if (subData.Length > 0)
                {
                    int player = (subData[0] & 0x0F) switch
                    {
                        0x01 => 1,
                        0x03 => 2,
                        0x07 => 3,
                        0x0F => 4,
                        _ => 1
                    };
                    PlayerLedChanged?.Invoke(player);
                }
                break;

            case 0x40:
                if (subData.Length > 0)
                    _imuEnabled = subData[0] != 0;
                break;
        }

        resp[13] = ack;
        resp[14] = subcmd;
        return resp;
    }

    private static void FillStandardButtonsAndSticks(Span<byte> dest10, RoutedInput input)
    {
        var s = input.State;
        var btns = input.Buttons;
        dest10[0] = 0x8E;

        byte bRight = 0;
        if (btns.HasFlag(SwitchButtons.Y)) bRight |= 0x01;
        if (btns.HasFlag(SwitchButtons.X)) bRight |= 0x02;
        if (btns.HasFlag(SwitchButtons.B)) bRight |= 0x04;
        if (btns.HasFlag(SwitchButtons.A)) bRight |= 0x08;
        if (btns.HasFlag(SwitchButtons.R)) bRight |= 0x40;
        if (btns.HasFlag(SwitchButtons.ZR) || s.RightTriggerAnalog > 80) bRight |= 0x80;

        byte bShared = 0;
        if (btns.HasFlag(SwitchButtons.Minus)) bShared |= 0x01;
        if (btns.HasFlag(SwitchButtons.Plus)) bShared |= 0x02;
        if (btns.HasFlag(SwitchButtons.RStick)) bShared |= 0x04;
        if (btns.HasFlag(SwitchButtons.LStick)) bShared |= 0x08;
        if (btns.HasFlag(SwitchButtons.Home)) bShared |= 0x10;
        if (btns.HasFlag(SwitchButtons.Capture)) bShared |= 0x20;

        byte bLeft = 0;
        if (btns.HasFlag(SwitchButtons.Down)) bLeft |= 0x01;
        if (btns.HasFlag(SwitchButtons.Up)) bLeft |= 0x02;
        if (btns.HasFlag(SwitchButtons.Right)) bLeft |= 0x04;
        if (btns.HasFlag(SwitchButtons.Left)) bLeft |= 0x08;
        if (btns.HasFlag(SwitchButtons.L)) bLeft |= 0x40;
        if (btns.HasFlag(SwitchButtons.ZL) || s.LeftTriggerAnalog > 80) bLeft |= 0x80;

        dest10[1] = bRight;
        dest10[2] = bShared;
        dest10[3] = bLeft;

        int lx = (int)MathF.Round(2048f + s.LeftStickX * 1638f);
        int ly = (int)MathF.Round(2048f + s.LeftStickY * 1638f);
        int rx = (int)MathF.Round(2048f + s.RightStickX * 1638f);
        int ry = (int)MathF.Round(2048f + s.RightStickY * 1638f);

        StickCalibration.Pack12Bit(dest10.Slice(4, 3), lx, ly);
        StickCalibration.Pack12Bit(dest10.Slice(7, 3), rx, ry);
    }

    private byte[] Build0x30InputReport()
    {
        byte[] rep = new byte[64];
        rep[0] = 0x30;
        rep[1] = (byte)Interlocked.Increment(ref _timer);
        var input = _latestInput;
        FillStandardButtonsAndSticks(rep.AsSpan(2, 10), input);
        rep[12] = 0x00;

        if (_imuEnabled)
        {
            var s = input.State;
            bool suppressGyro = input.SuppressGyro;
            short ax = s.AccelX;
            short ay = s.AccelY;
            short az = s.AccelZ == 0 && s.AccelX == 0 && s.AccelY == 0 ? (short)4096 : s.AccelZ;
            short gx = suppressGyro ? (short)0 : s.GyroX;
            short gy = suppressGyro ? (short)0 : s.GyroY;
            short gz = suppressGyro ? (short)0 : s.GyroZ;

            for (int sample = 0; sample < 3; sample++)
            {
                int off = 13 + sample * 12;
                BinaryPrimitives.WriteInt16LittleEndian(rep.AsSpan(off + 0, 2), ax);
                BinaryPrimitives.WriteInt16LittleEndian(rep.AsSpan(off + 2, 2), ay);
                BinaryPrimitives.WriteInt16LittleEndian(rep.AsSpan(off + 4, 2), az);
                BinaryPrimitives.WriteInt16LittleEndian(rep.AsSpan(off + 6, 2), gx);
                BinaryPrimitives.WriteInt16LittleEndian(rep.AsSpan(off + 8, 2), gy);
                BinaryPrimitives.WriteInt16LittleEndian(rep.AsSpan(off + 10, 2), gz);
            }
        }

        return rep;
    }

    private async Task StreamInputLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
        while (!ct.IsCancellationRequested && IsRunning)
        {
            await timer.WaitForNextTickAsync(ct);

            if (_usbStreamingEnabled || _inputReportMode == 0x30)
            {
                byte[] rep = Build0x30InputReport();
                if (WinUHidNative.WinUHidSubmitInputReport(_deviceHandle, rep, (uint)rep.Length))
                {
                    Interlocked.Increment(ref _inputReportsSent);
                }
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _streamTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _streamTask = null;
        }
        if (_deviceHandle != IntPtr.Zero)
        {
            IntPtr h = _deviceHandle;
            _deviceHandle = IntPtr.Zero;
            try
            {
                WinUHidNative.WinUHidStopDevice(h);
                WinUHidNative.WinUHidDestroyDevice(h);
            }
            catch { }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Kernel VHF Virtual Sony DualSense Controller (0x054C:0x0CE6) via WinUHidDevs.dll.
/// Uses the exact WinUHidPS5Create / WinUHidPS5ReportInput ABI from WinUHidPS5.h.
/// </summary>
public sealed class WinUHidDualSenseVirtualDevice : IVirtualOutputDevice
{
    private const int TouchContactOffset = 33;
    private IntPtr _device;
    private readonly byte[] _report = new byte[128];
    private WinUHidNative.Ps5RumbleCallback? _rumbleCb;
    private WinUHidNative.Ps5PlayerLedCallback? _playerLedCb;
    private WinUHidNative.Ps5TriggerEffectCallback? _triggerCb;
    private long _inputReportsSent;
    private long _rumblePacketsReceived;
    private byte _leftTrigPulse;
    private byte _rightTrigPulse;
    private volatile BridgeConfiguration _feedbackConfig = new();
    private int _motorLevels;
    private bool _touchDown;

    public string Name => "Sony DualSense PS5 Virtual Controller (Kernel VHF 0x054C:0x0CE6)";
    public EmulationMode Mode => EmulationMode.DualSense_PS5_VHF;
    public bool IsRunning => _device != IntPtr.Zero;
    public long InputReportsSent => Interlocked.Read(ref _inputReportsSent);
    public long RumblePacketsReceived => Interlocked.Read(ref _rumblePacketsReceived);
    public VibrationFrame LastLeftRumble { get; private set; } = VibrationFrame.Silence;
    public VibrationFrame LastRightRumble { get; private set; } = VibrationFrame.Silence;

    public event Action<StereoRumble>? HdRumbleReceived;
    public event Action<int>? PlayerLedChanged;
    public event Action<string>? LogMessage;

    public unsafe bool Start()
    {
        if (IsRunning) return true;

        WinUHidNative.WinUHidPS5InitializeInputReport(_report);
        _rumbleCb = OnPs5Rumble;
        _playerLedCb = OnPs5PlayerLed;
        _triggerCb = OnPs5TriggerEffect;

        var info = new WinUHidNative.WinUHidPS5GamepadInfo
        {
            BasicInfo = IntPtr.Zero,
            FirmwareInfo = IntPtr.Zero,
            FirmwareInfoLength = 0
        };

        _device = WinUHidNative.WinUHidPS5Create(
            ref info,
            _rumbleCb,
            null,
            _playerLedCb,
            _triggerCb,
            null,
            IntPtr.Zero);

        if (_device == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            LogMessage?.Invoke($"WinUHidPS5Create failed (Win32 error {err}).");
            return false;
        }
        LogMessage?.Invoke($"Started {Name}");
        return true;
    }

    private void OnPs5Rumble(IntPtr ctx, byte leftMotor, byte rightMotor)
    {
        Volatile.Write(ref _motorLevels, leftMotor | (rightMotor << 8));
        var (left, right) = HdRumbleEngine.FromLegacyAndImpulseTriggers(
            leftMotor, rightMotor, _feedbackConfig.DualSenseTriggerRumble ? _leftTrigPulse : (byte)0,
            _feedbackConfig.DualSenseTriggerRumble ? _rightTrigPulse : (byte)0);
        LastLeftRumble = left;
        LastRightRumble = right;
        Interlocked.Increment(ref _rumblePacketsReceived);
        HdRumbleReceived?.Invoke(new StereoRumble(left, right) { Sustained = true });
    }

    private void OnPs5PlayerLed(IntPtr ctx, byte ledVal)
    {
        int player = (ledVal & 0x1F) switch
        {
            0x04 => 1,
            0x0A => 2,
            0x15 => 3,
            0x1B => 4,
            _ => 1
        };
        PlayerLedChanged?.Invoke(player);
    }

    private void OnPs5TriggerEffect(IntPtr ctx, IntPtr leftEff, IntPtr rightEff)
    {
        _leftTrigPulse = leftEff != IntPtr.Zero && Marshal.ReadByte(leftEff) != 0 ? (byte)75 : (byte)0;
        _rightTrigPulse = rightEff != IntPtr.Zero && Marshal.ReadByte(rightEff) != 0 ? (byte)75 : (byte)0;
        int motors = Volatile.Read(ref _motorLevels);
        OnPs5Rumble(ctx, (byte)motors, (byte)(motors >> 8));
    }

    public void UpdateControllerState(RoutedInput input, BridgeConfiguration config)
    {
        if (!IsRunning) return;
        _feedbackConfig = config;
        var s = input.State;
        var btns = input.Buttons;
        bool suppressGyro = input.SuppressGyro || !config.DualSenseMotion;
        bool touching = config.TouchpadFromStick && btns.HasFlag(SwitchButtons.Minus);
        UpdateTouch(touching, s.RightStickX, s.RightStickY);
        if (touching) btns &= ~SwitchButtons.Minus;

        // Exact WINUHID_PS5_INPUT_REPORT layout (WinUHidPS5.h):
        // [0] = ReportId (managed by WinUHid)
        // [1] = LeftStickX (0x80 centered)
        // [2] = LeftStickY (0x80 centered, inverted: 0=up, 255=down)
        // [3] = RightStickX (0x80 centered)
        // [4] = RightStickY (0x80 centered, inverted: 0=up, 255=down)
        // [5] = LeftTrigger
        // [6] = RightTrigger
        // [7] = SequenceNumber
        // [8] = Hat(4b) | Square(1) | Cross(1) | Circle(1) | Triangle(1)
        // [9] = L1(1) | R1(1) | L2(1) | R2(1) | Share(1) | Options(1) | L3(1) | R3(1)
        // [10] = Home(1) | Touchpad(1) | Mute(1) ...
        _report[1] = (byte)Math.Clamp((int)MathF.Round((s.LeftStickX + 1f) * 127.5f), 0, 255);
        _report[2] = (byte)Math.Clamp((int)MathF.Round((-s.LeftStickY + 1f) * 127.5f), 0, 255);
        _report[3] = touching ? (byte)128 : (byte)Math.Clamp((int)MathF.Round((s.RightStickX + 1f) * 127.5f), 0, 255);
        _report[4] = touching ? (byte)128 : (byte)Math.Clamp((int)MathF.Round((-s.RightStickY + 1f) * 127.5f), 0, 255);
        _report[5] = s.LeftTriggerAnalog != 0 ? s.LeftTriggerAnalog : (byte)(btns.HasFlag(SwitchButtons.ZL) ? 255 : 0);
        _report[6] = s.RightTriggerAnalog != 0 ? s.RightTriggerAnalog : (byte)(btns.HasFlag(SwitchButtons.ZR) ? 255 : 0);
        _report[7]++;

        int hatX = (btns.HasFlag(SwitchButtons.Right) ? 1 : 0) - (btns.HasFlag(SwitchButtons.Left) ? 1 : 0);
        int hatY = (btns.HasFlag(SwitchButtons.Down) ? 1 : 0) - (btns.HasFlag(SwitchButtons.Up) ? 1 : 0);
        WinUHidNative.WinUHidPS5SetHatState(_report, hatX, hatY);

        byte b8 = (byte)(_report[8] & 0x0F); // preserve Hat low nibble
        if (btns.HasFlag(SwitchButtons.Y)) b8 |= 0x10; // Square
        if (btns.HasFlag(SwitchButtons.B)) b8 |= 0x20; // Cross
        if (btns.HasFlag(SwitchButtons.A)) b8 |= 0x40; // Circle
        if (btns.HasFlag(SwitchButtons.X)) b8 |= 0x80; // Triangle
        _report[8] = b8;

        byte b9 = 0;
        if (btns.HasFlag(SwitchButtons.L)) b9 |= 0x01; // L1
        if (btns.HasFlag(SwitchButtons.R)) b9 |= 0x02; // R1
        if (btns.HasFlag(SwitchButtons.ZL) || s.LeftTriggerAnalog > 80) b9 |= 0x04; // L2
        if (btns.HasFlag(SwitchButtons.ZR) || s.RightTriggerAnalog > 80) b9 |= 0x08; // R2
        if (btns.HasFlag(SwitchButtons.Minus)) b9 |= 0x10; // Share
        if (btns.HasFlag(SwitchButtons.Plus)) b9 |= 0x20; // Options
        if (btns.HasFlag(SwitchButtons.LStick)) b9 |= 0x40; // L3
        if (btns.HasFlag(SwitchButtons.RStick)) b9 |= 0x80; // R3
        _report[9] = b9;

        byte b10 = 0;
        if (btns.HasFlag(SwitchButtons.Home)) b10 |= 0x01;
        if (config.CaptureAsTouchpad && btns.HasFlag(SwitchButtons.Capture)) b10 |= 0x02;
        _report[10] = b10;

        const float deg2Rad = MathF.PI / 180f;
        const float gToMps2 = 9.80665f;
        WinUHidNative.WinUHidPS5SetGyroState(
            _report,
            suppressGyro ? 0f : s.GyroDps_X * deg2Rad,
            suppressGyro ? 0f : s.GyroDps_Y * deg2Rad,
            suppressGyro ? 0f : s.GyroDps_Z * deg2Rad);
        WinUHidNative.WinUHidPS5SetAccelState(
            _report,
            config.DualSenseMotion ? s.AccelG_X * gToMps2 : 0f,
            config.DualSenseMotion ? s.AccelG_Y * gToMps2 : 0f,
            config.DualSenseMotion ? s.AccelG_Z * gToMps2 : gToMps2);
        // The virtual USB controller has its own power state.
        WinUHidNative.WinUHidPS5SetBatteryState(_report, true, 100);

        if (WinUHidNative.WinUHidPS5ReportInput(_device, _report))
            Interlocked.Increment(ref _inputReportsSent);
    }

    private void UpdateTouch(bool touching, float x, float y)
    {
        if (!touching && !_touchDown) return;
        var (touchX, touchY) = MapStickToTouch(x, y);
        byte contactId = _report[TouchContactOffset];
        WinUHidNative.WinUHidPS5SetTouchState(_report, 0, touching, touchX, touchY);
        // Preserve the same touch ID throughout a held contact.
        if (touching && _touchDown) _report[TouchContactOffset] = contactId;
        _touchDown = touching;
    }

    internal static (ushort X, ushort Y) MapStickToTouch(float x, float y) =>
        ((ushort)MathF.Round((Math.Clamp(x, -1f, 1f) + 1f) * (1919f / 2)),
         (ushort)MathF.Round((1f - Math.Clamp(y, -1f, 1f)) * (1079f / 2)));

    public void Stop()
    {
        if (_device != IntPtr.Zero)
        {
            IntPtr d = _device;
            _device = IntPtr.Zero;
            try { WinUHidNative.WinUHidPS5Destroy(d); } catch { }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Kernel VHF Virtual Xbox One Controller (0x045E:0x02FF) via WinUHidDevs.dll.
/// Uses the exact WinUHidXOneCreate / WinUHidXOneReportInput ABI from WinUHidXOne.h.
/// </summary>
public sealed class WinUHidXboxOneVirtualDevice : IVirtualOutputDevice
{
    private volatile BridgeConfiguration _feedbackConfig = new();
    private IntPtr _device;
    private readonly byte[] _report = new byte[32];
    private WinUHidNative.XOneForceFeedbackCallback? _callback;
    private long _inputReportsSent;
    private long _rumblePacketsReceived;

    public string Name => "Xbox One Impulse Trigger Virtual Controller (Kernel VHF 0x045E:0x02FF)";
    public EmulationMode Mode => EmulationMode.XboxOne_Impulse_VHF;
    public bool IsRunning => _device != IntPtr.Zero;
    public long InputReportsSent => Interlocked.Read(ref _inputReportsSent);
    public long RumblePacketsReceived => Interlocked.Read(ref _rumblePacketsReceived);
    public VibrationFrame LastLeftRumble { get; private set; } = VibrationFrame.Silence;
    public VibrationFrame LastRightRumble { get; private set; } = VibrationFrame.Silence;

    public event Action<StereoRumble>? HdRumbleReceived;
    public event Action<int>? PlayerLedChanged;
    public event Action<string>? LogMessage;

    public bool Start()
    {
        if (IsRunning) return true;

        WinUHidNative.WinUHidXOneInitializeInputReport(_report);
        _callback = OnXboxFeedback;
        _device = WinUHidNative.WinUHidXOneCreate(IntPtr.Zero, _callback, IntPtr.Zero);
        if (_device == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            LogMessage?.Invoke($"WinUHidXOneCreate failed (Win32 error {err}).");
            return false;
        }
        PlayerLedChanged?.Invoke(1);
        LogMessage?.Invoke($"Started {Name}");
        return true;
    }

    private void OnXboxFeedback(IntPtr ctx, byte leftMotorPct, byte rightMotorPct, byte leftTrigPct, byte rightTrigPct)
    {
        byte mainL = (byte)Math.Clamp((int)Math.Round(leftMotorPct * 2.55), 0, 255);
        byte mainR = (byte)Math.Clamp((int)Math.Round(rightMotorPct * 2.55), 0, 255);
        var (left, right) = HdRumbleEngine.FromLegacyAndImpulseTriggers(mainL, mainR,
            _feedbackConfig.XboxOneTriggerRumble ? leftTrigPct : (byte)0,
            _feedbackConfig.XboxOneTriggerRumble ? rightTrigPct : (byte)0);
        LastLeftRumble = left;
        LastRightRumble = right;
        Interlocked.Increment(ref _rumblePacketsReceived);
        HdRumbleReceived?.Invoke(new StereoRumble(left, right) { Sustained = true });
    }

    public void UpdateControllerState(RoutedInput input, BridgeConfiguration config)
    {
        if (!IsRunning) return;
        _feedbackConfig = config;
        var s = input.State;
        var btns = input.Buttons;

        // Exact WINUHID_XONE_INPUT_REPORT layout (WinUHidXOne.h, Pack = 1):
        // [0..1]   USHORT LeftStickX  (0x8000 centered)
        // [2..3]   USHORT LeftStickY  (0x8000 centered, inverted: 0=up, 65535=down)
        // [4..5]   USHORT RightStickX (0x8000 centered)
        // [6..7]   USHORT RightStickY (0x8000 centered, inverted: 0=up, 65535=down)
        // [8..11]  USHORT LeftTrigger:10, RightTrigger:10 (Wait: in MSVC C bitfields, two USHORT : 10 bitfields do NOT pack into one 16-bit USHORT; each occupies its own USHORT!)
        // Wait! Look at WinUHidXOne.h lines 37-38:
        //   USHORT LeftTrigger : 10;
        //   USHORT RightTrigger : 10;
        // Because 10 + 10 = 20 > 16 bits (sizeof(USHORT)*8), MSVC allocates 2 bytes (offset 8..9) for LeftTrigger and 2 bytes (offset 10..11) for RightTrigger!
        // Then at offset 12: UCHAR ButtonA:1, ButtonB:1, ButtonX:1, ButtonY:1, ButtonLB:1, ButtonRB:1, ButtonBack:1, ButtonMenu:1
        // Offset 13: UCHAR ButtonLS:1, ButtonRS:1, Reserved3:6
        // Offset 14: UCHAR Hat:4, Reserved4:4
        // Offset 15: UCHAR ButtonHome:1, Reserved5:7
        // Offset 16: UCHAR BatteryLevel
        ushort lx = (ushort)Math.Clamp((int)MathF.Round((s.LeftStickX + 1f) * 32767.5f), 0, 65535);
        ushort ly = (ushort)Math.Clamp((int)MathF.Round((-s.LeftStickY + 1f) * 32767.5f), 0, 65535);
        ushort rx = (ushort)Math.Clamp((int)MathF.Round((s.RightStickX + 1f) * 32767.5f), 0, 65535);
        ushort ry = (ushort)Math.Clamp((int)MathF.Round((-s.RightStickY + 1f) * 32767.5f), 0, 65535);

        BinaryPrimitives.WriteUInt16LittleEndian(_report.AsSpan(0, 2), lx);
        BinaryPrimitives.WriteUInt16LittleEndian(_report.AsSpan(2, 2), ly);
        BinaryPrimitives.WriteUInt16LittleEndian(_report.AsSpan(4, 2), rx);
        BinaryPrimitives.WriteUInt16LittleEndian(_report.AsSpan(6, 2), ry);

        ushort lt = (ushort)(s.LeftTriggerAnalog != 0 ? s.LeftTriggerAnalog * 1023 / 255 : btns.HasFlag(SwitchButtons.ZL) ? 1023 : 0);
        ushort rt = (ushort)(s.RightTriggerAnalog != 0 ? s.RightTriggerAnalog * 1023 / 255 : btns.HasFlag(SwitchButtons.ZR) ? 1023 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(_report.AsSpan(8, 2), lt);
        BinaryPrimitives.WriteUInt16LittleEndian(_report.AsSpan(10, 2), rt);

        byte b12 = 0;
        if (btns.HasFlag(SwitchButtons.B)) b12 |= 0x01; // South = Xbox A
        if (btns.HasFlag(SwitchButtons.A)) b12 |= 0x02; // East  = Xbox B
        if (btns.HasFlag(SwitchButtons.Y)) b12 |= 0x04; // West  = Xbox X
        if (btns.HasFlag(SwitchButtons.X)) b12 |= 0x08; // North = Xbox Y
        if (btns.HasFlag(SwitchButtons.L)) b12 |= 0x10; // LB
        if (btns.HasFlag(SwitchButtons.R)) b12 |= 0x20; // RB
        if (btns.HasFlag(SwitchButtons.Minus)) b12 |= 0x40; // Back/View
        if (btns.HasFlag(SwitchButtons.Plus)) b12 |= 0x80; // Menu
        _report[12] = b12;

        byte b13 = 0;
        if (btns.HasFlag(SwitchButtons.LStick)) b13 |= 0x01;
        if (btns.HasFlag(SwitchButtons.RStick)) b13 |= 0x02;
        _report[13] = b13;

        int hatX = (btns.HasFlag(SwitchButtons.Right) ? 1 : 0) - (btns.HasFlag(SwitchButtons.Left) ? 1 : 0);
        int hatY = (btns.HasFlag(SwitchButtons.Down) ? 1 : 0) - (btns.HasFlag(SwitchButtons.Up) ? 1 : 0);
        WinUHidNative.WinUHidXOneSetHatState(_report, hatX, hatY);

        _report[15] = btns.HasFlag(SwitchButtons.Home) ? (byte)0x01 : (byte)0x00;

        if (WinUHidNative.WinUHidXOneReportInput(_device, _report))
            Interlocked.Increment(ref _inputReportsSent);
    }

    public void Stop()
    {
        if (_device != IntPtr.Zero)
        {
            IntPtr d = _device;
            _device = IntPtr.Zero;
            try { WinUHidNative.WinUHidXOneDestroy(d); } catch { }
        }
    }

    public void Dispose() => Stop();
}
