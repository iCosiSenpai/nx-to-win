using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NxToWin.Dsp;
using NxToWin.Models;

namespace NxToWin.VirtualOutput;

public sealed class UsbipSwitch2VirtualDevice : IVirtualOutputDevice
{
    private const ushort Version = 0x0111;
    private const ushort DeviceList = 0x8005;
    private const ushort Import = 0x8003;
    private const uint Submit = 1;
    private const uint Unlink = 2;
    private const int PipeStall = -32;
    private const int ConnectionReset = -104;
    private const int Port = 3240;
    private const string BusId = "1-1";

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _imported;
    private volatile bool _initialized;
    private long _lastHostInitialization;
    private volatile string? _attachError;
    private volatile RoutedInput _latestInput = new(new(), SwitchButtons.None);
    private long _inputReportsSent;
    private long _rumblePacketsReceived;

    public string Name => "Switch 2 Pro · USB/IP";
    public EmulationMode Mode => EmulationMode.Switch2Pro_NativeUSBIP;
    public bool IsRunning => _listener != null;
    public bool IsInitialized => _initialized;
    public DateTimeOffset? LastHostInitialization
    {
        get
        {
            long timestamp = Interlocked.Read(ref _lastHostInitialization);
            return timestamp == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        }
    }
    public string? AttachError => _attachError;
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
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _attachError = ex.Message;
            LogMessage?.Invoke($"USB/IP: impossibile aprire 127.0.0.1:{Port}. {ex.Message}");
            return false;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = AcceptAsync(listener, token);
        _ = AttachAsync(token);
        return true;
    }

    private async Task AttachAsync(CancellationToken token)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");
        if (!File.Exists(exe))
        {
            _attachError = "Client USB/IP assente";
            LogMessage?.Invoke($"USB/IP: client assente in {exe}.");
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, $"attach -r 127.0.0.1 -b {BusId}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }) ?? throw new InvalidOperationException("Avvio di usbip.exe fallito");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var cancelProcess = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stderr, stdout, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _attachError = $"Attach USB/IP fallito ({process.ExitCode})";
                LogMessage?.Invoke($"{_attachError}. {(await stderr).Trim()} {(await stdout).Trim()}");
                return;
            }
            LogMessage?.Invoke("USB/IP: Windows ha importato Switch 2 Pro. In attesa del comando di inizializzazione.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _attachError = ex is OperationCanceledException ? "Attach USB/IP scaduto" : ex.Message;
            LogMessage?.Invoke($"USB/IP: {_attachError}");
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = ServeClientAsync(client, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (SocketException ex)
        {
            _attachError = ex.Message;
            LogMessage?.Invoke($"USB/IP: {ex.Message}");
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                byte[] header = new byte[8];
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt16BigEndian(header) != Version) return;
                ushort opcode = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));

                if (opcode == DeviceList)
                {
                    byte[] count = [0, 0, 0, 1];
                    byte[] interfaces = [3, 0, 0, 0, 0xFF, 0, 0, 0];
                    await stream.WriteAsync(BuildManagementReply(opcode, 0, [.. count, .. DeviceRecord(), .. interfaces]), token)
                        .ConfigureAwait(false);
                    return;
                }

                if (opcode != Import) return;
                byte[] requestedId = new byte[32];
                await stream.ReadExactlyAsync(requestedId, token).ConfigureAwait(false);
                if (Encoding.ASCII.GetString(requestedId).TrimEnd('\0') != BusId ||
                    Interlocked.CompareExchange(ref _imported, 1, 0) != 0)
                {
                    await stream.WriteAsync(BuildManagementReply(opcode, 1, []), token).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await stream.WriteAsync(BuildManagementReply(opcode, 0, DeviceRecord()), token).ConfigureAwait(false);
                    await RunSessionAsync(stream, token).ConfigureAwait(false);
                }
                finally
                {
                    _initialized = false;
                    Interlocked.Exchange(ref _lastHostInitialization, 0);
                    Interlocked.Exchange(ref _imported, 0);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (EndOfStreamException) { }
            catch (IOException ex) when (!token.IsCancellationRequested)
            {
                LogMessage?.Invoke($"USB/IP: sessione chiusa. {ex.Message}");
            }
            catch (IOException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"USB/IP: errore di sessione. {ex.Message}");
            }
        }
    }

    private static byte[] BuildManagementReply(ushort request, uint status, byte[] body)
    {
        byte[] reply = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(reply, Version);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), (ushort)(request & 0x7FFF));
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), status);
        body.CopyTo(reply, 8);
        return reply;
    }

    private static byte[] DeviceRecord()
    {
        byte[] data = new byte[312];
        Encoding.ASCII.GetBytes($"/sys/devices/virtual/usbip/{BusId}", data.AsSpan(0, 256));
        Encoding.ASCII.GetBytes(BusId, data.AsSpan(256, 32));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(288), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(292), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(296), 2); // Full speed
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(300), NintendoIds.VendorId);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(302), NintendoIds.ProController2Pid);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(304), Switch2UsbProtocol.Revision);
        data[306] = 0xEF;
        data[307] = 2;
        data[308] = 1;
        data[309] = 1;
        data[310] = 1;
        data[311] = 2;
        return data;
    }

    private async Task RunSessionAsync(NetworkStream stream, CancellationToken token)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = sessionCancellation.Token;
        byte configuration = 0;
        byte format = 9;
        bool streaming = true;
        uint frame = 1;
        var bulkReads = new Queue<(uint Sequence, int Length)>();
        var hidReads = new Queue<(uint Sequence, int Length)>();
        var bulkReplies = new Queue<ArraySegment<byte>>();
        byte[] header = new byte[48];
        Task read = stream.ReadExactlyAsync(header, token).AsTask();
        Task? nextInput = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (streaming && hidReads.Count != 0)
                    nextInput ??= Task.Delay(4, token);

                if (nextInput is not null &&
                    (nextInput.IsCompletedSuccessfully || await Task.WhenAny(read, nextInput).ConfigureAwait(false) == nextInput))
                {
                    await nextInput.ConfigureAwait(false);
                    nextInput = null;
                    if (streaming && hidReads.TryDequeue(out var hid))
                    {
                        var input = _latestInput;
                        var report = Switch2UsbProtocol.BuildInputReport(format, frame++, input.State, input.Buttons, input.SuppressGyro);
                        await SendTransferAsync(stream, hid.Sequence, 0, report.AsMemory(0, Math.Min(hid.Length, report.Length)), token)
                            .ConfigureAwait(false);
                        Interlocked.Increment(ref _inputReportsSent);
                    }
                    continue;
                }

                await read.ConfigureAwait(false);
                uint command = BinaryPrimitives.ReadUInt32BigEndian(header);
                uint sequence = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                if (command == Unlink)
                {
                    uint target = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
                    bool removed = RemovePending(hidReads, target) | RemovePending(bulkReads, target);
                    await stream.WriteAsync(BuildTransferHeader(4, sequence, removed ? ConnectionReset : 0), token)
                        .ConfigureAwait(false);
                }
                else if (command == Submit)
                {
                    uint direction = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
                    uint endpoint = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
                    int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));
                    int iso = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(32));
                    if (length is < 0 or > 65536 || iso > 0 || direction > 1)
                        throw new InvalidDataException("Trasferimento USB/IP non valido.");

                    byte[] outgoing = direction == 0 && length > 0 ? new byte[length] : [];
                    if (outgoing.Length > 0)
                        await stream.ReadExactlyAsync(outgoing, token).ConfigureAwait(false);

                    if (endpoint == 0)
                    {
                        if (header[40] == 0x21 && header[41] == 9)
                            HandleRumble(outgoing);
                        byte requestedFormat = header[42] == 5 ? (byte)5 : (byte)9;
                        byte[]? reply = Switch2UsbProtocol.GetControlReply(header.AsSpan(40), length, ref configuration,
                            BuildControlInputReport(requestedFormat, frame));
                        await SendTransferAsync(stream, sequence, reply is null ? PipeStall : 0, reply ?? [], token, outgoing.Length)
                            .ConfigureAwait(false);
                    }
                    else if (direction == 1 && endpoint == 1 && length > 0 && hidReads.Count < 256)
                        hidReads.Enqueue((sequence, length));
                    else if (direction == 1 && endpoint == 2 && length > 0 && bulkReads.Count < 32)
                        bulkReads.Enqueue((sequence, length));
                    else
                    {
                        if (direction == 0 && endpoint == 1) HandleRumble(outgoing);
                        if (direction == 0 && endpoint == 2 && outgoing.Length >= 8 &&
                            outgoing[1] == 0x91 && outgoing[5] <= outgoing.Length - 8)
                        {
                            if (bulkReplies.Count == 64) bulkReplies.Dequeue();
                            bulkReplies.Enqueue(Switch2UsbProtocol.CreateReply(outgoing, ref format, ref streaming));
                            if (outgoing[0] == 3 && outgoing[3] == 0x0D)
                            {
                                _initialized = true;
                                Interlocked.Exchange(ref _lastHostInitialization, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                                LogMessage?.Invoke($"USB/IP: inizializzazione host completata, report 0x{format:X2}.");
                            }
                            if (outgoing[0] == 9 && outgoing[3] == 7 && outgoing.Length >= 9)
                                PlayerLedChanged?.Invoke(outgoing[8] switch { 1 => 1, 3 => 2, 7 => 3, 15 => 4, _ => 0 });
                        }
                        await SendTransferAsync(stream, sequence, endpoint is 1 or 2 ? 0 : PipeStall, ReadOnlyMemory<byte>.Empty, token, outgoing.Length)
                            .ConfigureAwait(false);
                    }

                    while (bulkReplies.Count > 0 && bulkReads.TryDequeue(out var bulk))
                    {
                        var reply = bulkReplies.Peek();
                        int count = Math.Min(bulk.Length, reply.Count);
                        await SendTransferAsync(stream, bulk.Sequence, 0, reply.AsMemory(0, count), token).ConfigureAwait(false);
                        if (count == reply.Count)
                            bulkReplies.Dequeue();
                        else
                        {
                            // Preserve the current reply ahead of later commands when a host requests 64 + 16 bytes.
                            bulkReplies.Dequeue();
                            var remaining = bulkReplies.ToArray();
                            bulkReplies.Clear();
                            bulkReplies.Enqueue(reply.Slice(count));
                            foreach (var queued in remaining) bulkReplies.Enqueue(queued);
                        }
                    }
                }
                else
                    throw new InvalidDataException($"Comando USB/IP sconosciuto: {command}.");

                read = stream.ReadExactlyAsync(header, token).AsTask();
            }
        }
        finally
        {
            await sessionCancellation.CancelAsync().ConfigureAwait(false);
            try { await read.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private static bool RemovePending(Queue<(uint Sequence, int Length)> pending, uint sequence)
    {
        bool found = false;
        for (int i = pending.Count; i > 0; i--)
        {
            var transfer = pending.Dequeue();
            if (transfer.Sequence == sequence) found = true;
            else pending.Enqueue(transfer);
        }
        return found;
    }

    private static byte[] BuildTransferHeader(uint command, uint sequence, int status, int length = 0, int payloadLength = 0)
    {
        byte[] header = new byte[48 + payloadLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, command);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), sequence);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), status);
        if (command == 3)
        {
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), length);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32), -1);
        }
        return header;
    }

    private static ValueTask SendTransferAsync(NetworkStream stream, uint sequence, int status, ReadOnlyMemory<byte> payload,
        CancellationToken token, int outgoingLength = 0)
    {
        byte[] reply = BuildTransferHeader(3, sequence, status, status == 0 ? outgoingLength + payload.Length : 0, payload.Length);
        payload.Span.CopyTo(reply.AsSpan(48));
        return stream.WriteAsync(reply, token);
    }

    private void HandleRumble(ReadOnlySpan<byte> data)
    {
        if (!StereoRumble.TryReadSwitch2Report(data, out var rumble)) return;
        LastLeftRumble = rumble.Left.Peak;
        LastRightRumble = rumble.Right.Peak;
        Interlocked.Increment(ref _rumblePacketsReceived);
        HdRumbleReceived?.Invoke(rumble);
    }
    public void UpdateControllerState(RoutedInput input, BridgeConfiguration config) => _latestInput = input;

    private byte[] BuildControlInputReport(byte format, uint frame)
    {
        var input = _latestInput;
        return Switch2UsbProtocol.BuildInputReport(format, frame, input.State, input.Buttons, input.SuppressGyro);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    public void Dispose() => Stop();
}
