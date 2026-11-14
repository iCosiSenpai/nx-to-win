using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using NxToWin.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace NxToWin.Hardware;

public sealed class ControllerDiscoveryManager : IDisposable
{
    private const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    private readonly BridgeConfiguration _config;
    private readonly object _gate = new();
    private readonly Dictionary<string, IPhysicalController> _controllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manualDisconnects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (ulong Address, ushort Product)> _knownBle = new();
    private readonly ConcurrentDictionary<ulong, long> _bleRetryTimes = new();
    private readonly ConcurrentDictionary<ulong, byte> _connectingBle = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private BluetoothLEAdvertisementWatcher? _bleWatcher;
    private Timer? _periodicScanTimer;
    private volatile bool _disposed;

    public IReadOnlyList<IPhysicalController> Controllers
    {
        get
        {
            lock (_gate)
            {
                var paired = _controllers.Values.OfType<MergedJoyConPairController>()
                    .SelectMany(pair => new[] { pair.LeftJoyCon, pair.RightJoyCon }).ToHashSet();
                return _controllers.Values.Where(c => c.IsConnected && !paired.Contains(c)).ToArray();
            }
        }
    }

    public event Action<IPhysicalController>? ControllerConnected;
    public event Action<IPhysicalController>? ControllerDisconnected;
    public event Action<string>? LogMessage;

    public ControllerDiscoveryManager(BridgeConfiguration config) => _config = config;

    public void Start()
    {
        if (_periodicScanTimer != null || _disposed) return;
        try
        {
            _bleWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _bleWatcher.Received += OnBleAdvertisementReceived;
            _bleWatcher.Start();
        }
        catch (Exception ex)
        {
            Log($"Scansione Bluetooth LE: {ex.Message}");
        }

        _periodicScanTimer = new Timer(_ => _ = ScanAsync(false), null, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        Log("Ricerca controller Nintendo attiva.");
    }

    private void OnBleAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (_disposed || args.BluetoothAddress == 0) return;
        ushort pid = 0;
        foreach (var manufacturer in args.Advertisement.ManufacturerData)
        {
            if (manufacturer.CompanyId != NintendoIds.BluetoothManufacturerId) continue;
            byte[] data = manufacturer.Data.ToArray();
            if (data.Length < 7 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(3)) != NintendoIds.VendorId)
                continue;
            ushort advertised = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(5));
            if (!NintendoIds.IsSwitch2Pid(advertised)) continue;
            pid = advertised;
            break;
        }

        if (pid == 0 && args.Advertisement.ServiceUuids.Contains(NintendoIds.Sw2ServiceUuid))
            pid = NintendoIds.ProController2Pid;
        if (pid != 0)
            _ = TryConnectBleAsync(args.BluetoothAddress, pid);
    }

    public Task ScanPairedBleAndHidAsync() => ScanAsync(true);

    private async Task ScanAsync(bool manual)
    {
        if (_disposed) return;
        string[] reconnect = [];
        if (manual)
        {
            lock (_gate)
            {
                reconnect = _manualDisconnects.ToArray();
                _manualDisconnects.Clear();
            }
            _bleRetryTimes.Clear();
            await _scanGate.WaitAsync().ConfigureAwait(false);
        }
        else if (!await _scanGate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            if (_disposed) return;
            var attempts = reconnect.Where(_knownBle.ContainsKey)
                .Select(id => _knownBle[id])
                .Select(device => TryConnectBleAsync(device.Address, device.Product));
            await Task.WhenAll(attempts.Append(Task.Run(ScanHid)).Append(ScanPairedBleAsync())).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private void ScanHid()
    {
        try
        {
            foreach (var info in HidNintendoController.EnumerateNintendoHidDevices())
            {
                string id = HidNintendoController.GetId(info.Path, info.ProductId);
                if (!CanConnect(id)) continue;
                var controller = HidNintendoController.TryOpen(info, _config, Log);
                if (controller != null) Register(controller);
            }
        }
        catch (Exception ex)
        {
            Log($"Scansione HID: {ex.Message}");
        }
    }

    private async Task ScanPairedBleAsync()
    {
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var devices = await DeviceInformation.FindAllAsync(selector, [AddressProperty]);
            foreach (var info in devices)
            {
                ushort pid = InferPid(info.Name);
                if (pid == 0 || !info.Properties.TryGetValue(AddressProperty, out var address) || address is not string text)
                    continue;
                if (ulong.TryParse(text.Replace(":", "").Replace("-", ""), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out ulong value) && value != 0)
                    _ = TryConnectBleAsync(value, pid);
            }
        }
        catch (Exception ex)
        {
            Log($"Scansione Bluetooth LE: {ex.Message}");
        }
    }

    private static ushort InferPid(string name)
    {
        if (name.Contains("Joy-Con 2 (L)", StringComparison.OrdinalIgnoreCase)) return NintendoIds.JoyCon2LeftPid;
        if (name.Contains("Joy-Con 2 (R)", StringComparison.OrdinalIgnoreCase)) return NintendoIds.JoyCon2RightPid;
        if (name.Contains("GameCube", StringComparison.OrdinalIgnoreCase)) return NintendoIds.NsoGameCubePid;
        return name.Contains("Pro Controller", StringComparison.OrdinalIgnoreCase) ? NintendoIds.ProController2Pid : (ushort)0;
    }

    private bool CanConnect(string id)
    {
        lock (_gate) return !_disposed && !_controllers.ContainsKey(id) && !_manualDisconnects.Contains(id);
    }

    private async Task TryConnectBleAsync(ulong address, ushort pid)
    {
        string id = $"BLE:{address:X12}";
        if (!CanConnect(id) || !_connectingBle.TryAdd(address, 0)) return;
        try
        {
            long now = Environment.TickCount64;
            if (_bleRetryTimes.TryGetValue(address, out long retryAfter) && now < retryAfter) return;
            _bleRetryTimes[address] = now + 6000;
            if (!CanConnect(id)) return;

            var controller = await Switch2BleController.ConnectAsync(address, pid, _config, Log).ConfigureAwait(false);
            if (controller != null)
            {
                _knownBle[controller.Id] = (address, controller.ProductId);
                Register(controller);
            }
        }
        catch (Exception ex)
        {
            Log($"Connessione Bluetooth LE: {ex.Message}");
        }
        finally
        {
            _connectingBle.TryRemove(address, out _);
        }
    }

    private void Register(IPhysicalController controller)
    {
        lock (_gate)
        {
            if (!controller.IsConnected || !CanConnect(controller.Id))
            {
                controller.Dispose();
                return;
            }
            Add(controller);
            TryPair(controller);
        }
    }

    public void SetAutoMerge(bool enabled)
    {
        lock (_gate)
        {
            if (_config.AutoMergeJoyCons == enabled) return;
            _config.AutoMergeJoyCons = enabled;
            if (enabled)
            {
                foreach (var left in _controllers.Values.Where(c => c.IsLeftJoyCon).ToArray())
                    TryPair(left);
            }
            else
            {
                foreach (var pair in _controllers.Values.OfType<MergedJoyConPairController>().ToArray())
                    Remove(pair);
            }
        }
    }

    private void TryPair(IPhysicalController controller)
    {
        if (!_config.AutoMergeJoyCons || !controller.IsConnected || !(controller.IsLeftJoyCon || controller.IsRightJoyCon))
            return;
        var assigned = _controllers.Values.OfType<MergedJoyConPairController>()
            .SelectMany(pair => new[] { pair.LeftJoyCon, pair.RightJoyCon }).ToHashSet();
        if (assigned.Contains(controller)) return;
        var partner = _controllers.Values.FirstOrDefault(c => c.IsConnected && c.IsSwitch2 == controller.IsSwitch2 &&
            (controller.IsLeftJoyCon ? c.IsRightJoyCon : c.IsLeftJoyCon) && !assigned.Contains(c));
        if (partner != null)
            Add(controller.IsLeftJoyCon ? new MergedJoyConPairController(controller, partner) : new MergedJoyConPairController(partner, controller));
    }

    private void Add(IPhysicalController controller)
    {
        controller.Disconnected += OnDisconnected;
        _controllers.Add(controller.Id, controller);
        Log($"Connesso: {controller.Name} · {controller.TransportName}");
        ControllerConnected?.Invoke(controller);
        if (!controller.IsConnected) OnDisconnected(controller);
    }

    public void Disconnect(IPhysicalController controller)
    {
        lock (_gate)
        {
            if (!_controllers.TryGetValue(controller.Id, out var current) || current != controller) return;
            if (controller is MergedJoyConPairController pair)
            {
                _manualDisconnects.Add(pair.LeftJoyCon.Id);
                _manualDisconnects.Add(pair.RightJoyCon.Id);
                Remove(pair);
                Remove(pair.LeftJoyCon);
                Remove(pair.RightJoyCon);
                return;
            }

            _manualDisconnects.Add(controller.Id);
            OnDisconnected(controller);
        }
    }

    private void OnDisconnected(IPhysicalController controller)
    {
        lock (_gate)
        {
            if (!_controllers.TryGetValue(controller.Id, out var current) || current != controller) return;
            var pair = _controllers.Values.OfType<MergedJoyConPairController>()
                .FirstOrDefault(p => p.LeftJoyCon == controller || p.RightJoyCon == controller);
            if (pair != null) Remove(pair);
            Remove(controller);
        }
    }

    private void Remove(IPhysicalController controller)
    {
        if (!_controllers.Remove(controller.Id)) return;
        controller.Disconnected -= OnDisconnected;
        controller.Dispose();
        Log($"Disconnesso: {controller.Name}");
        ControllerDisconnected?.Invoke(controller);
    }

    private void Log(string message) => LogMessage?.Invoke(message);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _periodicScanTimer?.Dispose();
            if (_bleWatcher != null)
            {
                _bleWatcher.Received -= OnBleAdvertisementReceived;
                _bleWatcher.Stop();
            }
            foreach (var controller in _controllers.Values.OrderBy(c => c is MergedJoyConPairController ? 0 : 1))
            {
                controller.Disconnected -= OnDisconnected;
                controller.Dispose();
            }
            _controllers.Clear();
        }
    }
}
