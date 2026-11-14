using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using NxToWin.Dsp;
using NxToWin.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace NxToWin.Hardware;

public sealed class Switch2BleController : IPhysicalController
{
    private BluetoothLEDevice? _bleDevice;
    private GattDeviceService? _sw2Service;
    private GattCharacteristic? _inputReportChar;
    private GattCharacteristic? _commandWriteChar;
    private GattCharacteristic? _commandResponseChar;
    private GattCharacteristic? _vibrationWriteChar;

    private TaskCompletionSource<byte[]>? _cmdResponseTcs;
    private int _expectedCommand;
    private int _disposed;
    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private readonly Action<string>? _log;
    private int _vibrationSeq;
    private bool _loggedFirstReport;

    private volatile StickCalibration _leftStickCal = new();
    private volatile StickCalibration _rightStickCal = new();

    private long _reportWindowStartTicks = Stopwatch.GetTimestamp();
    private int _reportWindowCount;
    private double _currentRateHz;
    private ushort _prevMouseX;
    private ushort _prevMouseY;
    private bool _hasPrevMouse;

    public string Id { get; }
    public ulong BluetoothAddress { get; }
    public string DeviceInfoId { get; private set; } = "";
    public string Name { get; private set; }
    public string TransportName => "Bluetooth LE";
    public ushort ProductId { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsSwitch2 => true;
    public bool IsProController => NintendoIds.IsProController(ProductId);
    public bool IsLeftJoyCon => ProductId == NintendoIds.JoyCon2LeftPid;
    public bool IsRightJoyCon => ProductId == NintendoIds.JoyCon2RightPid;

    public ControllerStateSnapshot LatestState { get; private set; } = new();
    public ImuFusionEngine ImuFusion { get; } = new();
    public BridgeConfiguration Config { get; }

    public event Action<IPhysicalController, ControllerStateSnapshot>? StateUpdated;
    public event Action<IPhysicalController>? Disconnected;

    public Switch2BleController(ulong bluetoothAddress, ushort advertisedPid, BridgeConfiguration config, Action<string>? log = null)
    {
        BluetoothAddress = bluetoothAddress;
        ProductId = advertisedPid != 0 ? advertisedPid : NintendoIds.ProController2Pid;
        Id = $"BLE:{bluetoothAddress:X12}";
        Name = NintendoIds.GetProductName(ProductId);
        Config = config;
        _log = log;
    }

    public static async Task<Switch2BleController?> ConnectAsync(
        ulong bluetoothAddress,
        ushort advertisedPid,
        BridgeConfiguration config,
        Action<string>? log = null)
    {
        var ctrl = new Switch2BleController(bluetoothAddress, advertisedPid, config, log);
        bool ok = await ctrl.ConnectAndInitializeAsync();
        if (!ok)
        {
            ctrl.Dispose();
            return null;
        }
        log?.Invoke($"Ready: {ctrl.Name} (BLE 0x{ctrl.ProductId:X4}, Addr={bluetoothAddress:X12}).");
        return ctrl;
    }

    public async Task<bool> ConnectAndInitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(BluetoothAddress);
            if (_bleDevice == null)
            {
                _log?.Invoke($"BLE FromBluetoothAddressAsync({BluetoothAddress:X12}) returned null.");
                return false;
            }

            DeviceInfoId = _bleDevice.DeviceId ?? "";
            _bleDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

            TryRequestThroughputOptimized(_bleDevice);

            // Try reading PnP ID (0x2A50) on the same _bleDevice (without disposing it!)
            await TryUpdatePidFromDisServiceAsync(_bleDevice);

            var servicesResult = await _bleDevice.GetGattServicesForUuidAsync(NintendoIds.Sw2ServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                servicesResult = await _bleDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            }

            _sw2Service = servicesResult.Services.FirstOrDefault(s => s.Uuid == NintendoIds.Sw2ServiceUuid);
            foreach (var service in servicesResult.Services)
                if (service != _sw2Service) service.Dispose();

            if (_sw2Service == null)
            {
                _log?.Invoke($"SW2 GATT service (ab7de9be) not found on {BluetoothAddress:X12} (status={servicesResult.Status}).");
                return false;
            }

            var charsResult = await _sw2Service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                _log?.Invoke($"GetCharacteristicsAsync failed on {BluetoothAddress:X12} (status={charsResult.Status}).");
                return false;
            }

            var allChars = charsResult.Characteristics.OrderBy(c => c.AttributeHandle).ToList();

            _inputReportChar = allChars.FirstOrDefault(c => c.Uuid == NintendoIds.InputReportUuid);
            _commandWriteChar = allChars.FirstOrDefault(c => c.Uuid == NintendoIds.CommandWriteUuid);
            _commandResponseChar = allChars.FirstOrDefault(c => c.Uuid == NintendoIds.CommandResponseUuid);

            if (_inputReportChar == null || _commandWriteChar == null || _commandResponseChar == null)
            {
                _log?.Invoke($"Missing required SW2 GATT characteristics on {BluetoothAddress:X12}.");
                return false;
            }

            _commandResponseChar.ValueChanged += OnCommandResponseChanged;
            var commandNotify = await _commandResponseChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (commandNotify != GattCommunicationStatus.Success)
                return false;

            var initCommands = new (byte Cmd, byte SubCmd, byte[] Data)[]
            {
                (0x03, 0x0D, [0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
                (0x07, 0x01, []),
                (0x16, 0x01, []),
                (0x15, 0x03, [0x00]),
                (0x0C, 0x02, [0x94, 0x00, 0x00, 0x00]),
                (0x11, 0x03, []),
                (0x0A, 0x08, [0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
                (0x0C, 0x04, [0x94, 0x00, 0x00, 0x00]),
                (0x10, 0x01, []),
                (0x01, 0x0C, []),
                (0x09, 0x07, [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
            };

            foreach (var (cmd, subCmd, payload) in initCommands)
            {
                try
                {
                    await WriteCommandAsync(cmd, subCmd, payload, 400);
                }
                catch
                {
                    // Continue if controller was already initialized
                }
            }

            try
            {
                byte[] info = await ReadSpiMemoryAsync(0x00013000, 0x40, 500);
                if (info.Length >= 22)
                {
                    ushort pid = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(20, 2));
                    if (NintendoIds.IsSwitch2Pid(pid))
                    {
                        ProductId = pid;
                        Name = NintendoIds.GetProductName(ProductId);
                    }
                }
            }
            catch { }

            Guid vibUuid = IsProController
                ? NintendoIds.VibrationPro2Uuid
                : (IsLeftJoyCon ? NintendoIds.VibrationJoyCon2LeftUuid : NintendoIds.VibrationJoyCon2RightUuid);
            if (ProductId == NintendoIds.NsoGameCubePid)
                vibUuid = new Guid("3f8fb670-ab25-45bf-b540-38c72834d064");
            _vibrationWriteChar = allChars.FirstOrDefault(c => c.Uuid == vibUuid);

            await LoadStickCalibrationAsync();

            // Standard 0x05 is shared by all Switch 2 controller models.
            _inputReportChar.ValueChanged += OnInputReportChanged;
            var notifyStatus = await _inputReportChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            _log?.Invoke($"Notifiche GATT {Name}: {notifyStatus}.");
            IsConnected = notifyStatus == GattCommunicationStatus.Success &&
                _bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;

            return IsConnected;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"BLE ConnectAndInitializeAsync error: {ex.Message}");
            Dispose();
            return false;
        }
    }

    private async Task TryUpdatePidFromDisServiceAsync(BluetoothLEDevice dev)
    {
        try
        {
            Guid disUuid = new("0000180a-0000-1000-8000-00805f9b34fb");
            Guid pnpUuid = new("00002a50-0000-1000-8000-00805f9b34fb");

            var svcRes = await dev.GetGattServicesForUuidAsync(disUuid, BluetoothCacheMode.Cached);
            if (svcRes.Status == GattCommunicationStatus.Success && svcRes.Services.Count > 0)
            {
                using var svc = svcRes.Services[0];
                var charRes = await svc.GetCharacteristicsForUuidAsync(pnpUuid, BluetoothCacheMode.Cached);
                if (charRes.Status == GattCommunicationStatus.Success && charRes.Characteristics.Count > 0)
                {
                    var valRes = await charRes.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Cached);
                    if (valRes.Status == GattCommunicationStatus.Success)
                    {
                        byte[] data = valRes.Value.ToArray();
                        if (data.Length >= 5)
                        {
                            ushort pid = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(3, 2));
                            if (NintendoIds.IsSwitch2Pid(pid))
                            {
                                ProductId = pid;
                                Name = NintendoIds.GetProductName(ProductId);
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static void TryRequestThroughputOptimized(BluetoothLEDevice device)
    {
        try
        {
            var prefs = BluetoothLEPreferredConnectionParameters.ThroughputOptimized;
            device.RequestPreferredConnectionParameters(prefs);
        }
        catch { }
    }

    private async Task LoadStickCalibrationAsync()
    {
        if (ProductId == NintendoIds.NsoGameCubePid) return;
        if (!IsRightJoyCon)
            _leftStickCal = await ReadCalibrationAsync(0x1FC040, 0x130A8);
        if (!IsLeftJoyCon)
            _rightStickCal = await ReadCalibrationAsync(IsRightJoyCon ? 0x1FC040u : 0x1FC080u,
                IsRightJoyCon ? 0x130A8u : 0x130E8u);
    }

    private async Task<StickCalibration> ReadCalibrationAsync(uint userAddress, uint factoryAddress)
    {
        try
        {
            byte[] user = await ReadSpiMemoryAsync(userAddress, 11, 450);
            if (user.Length >= 11 && user[0] == 0xB2 && user[1] == 0xA1)
            {
                var calibration = StickCalibration.FromSwitch2SpiBytes(user.AsSpan(2));
                if (calibration.IsValid) return calibration;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException) { }

        try
        {
            return StickCalibration.FromSwitch2SpiBytes(await ReadSpiMemoryAsync(factoryAddress, 9, 450));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Calibrazione BLE non disponibile per {Name}: {ex.Message}");
            return new();
        }
    }

    private async Task<byte[]> ReadSpiMemoryAsync(uint address, byte length, int timeoutMs = 600)
    {
        byte[] req = new byte[8];
        req[0] = length;
        req[1] = 0x7E;
        req[2] = 0x00;
        req[3] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4, 4), address);

        byte[] resp = await WriteCommandAsync(0x02, 0x04, req, timeoutMs);
        if (resp.Length < length + 8 || BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(4)) != address)
            return [];
        return resp.AsSpan(8, length).ToArray();
    }

    private async Task<byte[]> WriteCommandAsync(byte cmdId, byte subCmdId, byte[] payload, int timeoutMs = 500)
    {
        if (_commandWriteChar == null)
            throw new InvalidOperationException("Command Write characteristic not initialized.");

        await _cmdLock.WaitAsync();
        try
        {
            if (_disposed != 0) return [];
            _expectedCommand = (cmdId << 8) | subCmdId;
            var responseSource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _cmdResponseTcs = responseSource;

            byte[] packet = new byte[8 + payload.Length];
            packet[0] = cmdId;
            packet[1] = 0x91;
            packet[2] = 0x01;
            packet[3] = subCmdId;
            packet[4] = 0x00;
            packet[5] = (byte)payload.Length;
            packet[6] = 0x00;
            packet[7] = 0x00;
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, packet, 8, payload.Length);

            await WriteRawCharacteristicAsync(_commandWriteChar, packet);

            byte[] response = await responseSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            if (response.Length < 8)
                return [];
            return response.AsSpan(8).ToArray();
        }
        finally
        {
            _cmdResponseTcs = null;
            _cmdLock.Release();
        }
    }

    private void OnCommandResponseChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var tcs = _cmdResponseTcs;
        if (tcs == null || tcs.Task.IsCompleted) return;

        byte[] data = args.CharacteristicValue.ToArray();
        if (data.Length >= 8 && ((data[0] << 8) | data[3]) == _expectedCommand)
        {
            tcs.TrySetResult(data);
        }
    }

    private void OnInputReportChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] data = args.CharacteristicValue.ToArray();
        if (!IsConnected || data.Length < 63) return;

        if (!_loggedFirstReport)
        {
            _loggedFirstReport = true;
            _log?.Invoke($"Input BLE attivo · {Name} · {data.Length} byte.");
        }

        ParseInputReport(data);
    }

    public void ParseInputReport(ReadOnlySpan<byte> data)
    {
        var snap = Switch2InputReport.Parse(5, data, ProductId, out bool motion);
        if (snap == null) return;
        _reportWindowCount++;
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_reportWindowStartTicks, now).TotalSeconds;
        if (elapsed >= 0.5)
        {
            _currentRateHz = _reportWindowCount / elapsed;
            _reportWindowCount = 0;
            _reportWindowStartTicks = now;
        }
        snap.ReportRateHz = _currentRateHz;
        if (!IsRightJoyCon)
            (snap.LeftStickX, snap.LeftStickY) = _leftStickCal.Normalize(snap.RawLeftStickX, snap.RawLeftStickY, Config.LeftStickDeadzone);
        if (!IsLeftJoyCon)
            (snap.RightStickX, snap.RightStickY) = _rightStickCal.Normalize(snap.RawRightStickX, snap.RawRightStickY, Config.RightStickDeadzone);
        if (motion) ImuFusion.ProcessSample(snap, IsProController, Config.Enable9AxisMagFusion);
        if (Config.EnableJoyCon2OpticalMouse && (IsLeftJoyCon || IsRightJoyCon))
            ProcessOpticalMouse(snap);
        else _hasPrevMouse = false;
        LatestState = snap;
        StateUpdated?.Invoke(this, snap);
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    private void ProcessOpticalMouse(ControllerStateSnapshot snap)
    {
        if (!_hasPrevMouse)
        {
            _prevMouseX = snap.MouseRawX;
            _prevMouseY = snap.MouseRawY;
            _hasPrevMouse = true;
            return;
        }

        short dx = (short)(snap.MouseRawX - _prevMouseX);
        short dy = (short)(snap.MouseRawY - _prevMouseY);
        _prevMouseX = snap.MouseRawX;
        _prevMouseY = snap.MouseRawY;

        if (Math.Abs((int)dx) < 1200 && Math.Abs((int)dy) < 1200)
        {
            int mx = (int)Math.Round(dx * Config.OpticalMouseSensitivity * 0.25f);
            int my = (int)Math.Round(dy * Config.OpticalMouseSensitivity * 0.25f);
            if (mx != 0 || my != 0)
            {
                mouse_event(MOUSEEVENTF_MOVE, mx, my, 0, UIntPtr.Zero);
            }
        }
    }

    public bool SupportsRumble => _vibrationWriteChar != null;

    public async Task SendHdRumbleAsync(StereoRumble rumble)
    {
        if (!IsConnected || _vibrationWriteChar is not { } characteristic) return;
        var scaled = rumble.WithStrength(Config.RumbleStrength);
        int sequence = Interlocked.Increment(ref _vibrationSeq) & 0x0F;
        byte[] payload = ProductId == NintendoIds.NsoGameCubePid
            ? HdRumbleEngine.BuildGameCubeOutput(sequence, !scaled.IsSilent, wired: false)
            : HdRumbleEngine.BuildSwitch2Report(sequence, scaled, wired: false, IsLeftJoyCon, IsRightJoyCon);
        await WriteRawCharacteristicAsync(characteristic, payload).ConfigureAwait(false);
    }
    public async Task SetPlayerLedsAsync(int playerNumber)
    {
        byte pattern = playerNumber switch
        {
            1 => 0x01,
            2 => 0x03,
            3 => 0x07,
            4 => 0x0F,
            _ => 0x00
        };
        try
        {
            await WriteCommandAsync(0x09, 0x07, [pattern, 0, 0, 0, 0, 0, 0, 0], 400);
        }
        catch { }
    }

    public void TriggerGyroCalibration() => ImuFusion.StartGyroCalibration();

    public void CenterSticks()
    {
        var sample = LatestState;
        if (!IsRightJoyCon) _leftStickCal = _leftStickCal.WithCenter(sample.RawLeftStickX, sample.RawLeftStickY);
        if (!IsLeftJoyCon) _rightStickCal = _rightStickCal.WithCenter(sample.RawRightStickX, sample.RawRightStickY);
    }

    private static async Task WriteRawCharacteristicAsync(GattCharacteristic characteristic, byte[] payload)
    {
        var option = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        var status = await characteristic.WriteValueAsync(payload.AsBuffer(), option);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Scrittura GATT: {status}.");
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && IsConnected)
        {
            IsConnected = false;
            Disconnected?.Invoke(this);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IsConnected = false;
        _cmdResponseTcs?.TrySetCanceled();
        try
        {
            if (_inputReportChar != null)
                _inputReportChar.ValueChanged -= OnInputReportChanged;
            if (_commandResponseChar != null)
                _commandResponseChar.ValueChanged -= OnCommandResponseChanged;
            if (_bleDevice != null)
                _bleDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _sw2Service?.Dispose();
            _bleDevice?.Dispose();
        }
        catch { }
    }
}
