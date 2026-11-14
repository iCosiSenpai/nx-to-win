using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NxToWin.Models;

namespace NxToWin.VirtualOutput;

/// <summary>
/// Native Cemuhook DSU (DualShock UDP Motion Server, Protocol v1001) on 127.0.0.1:26760.
/// Provides high-rate 6-axis Gyroscope (deg/s) and Accelerometer (g) + full pad state to Cemu, Yuzu, Ryujinx, Dolphin, etc.
/// </summary>
public sealed class CemuhookDsuServer : IDisposable
{
    private const uint ProtocolVersion = 1001;
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private readonly uint _serverId = (uint)Random.Shared.Next(1, int.MaxValue);
    private readonly ConcurrentDictionary<IPEndPoint, DateTime> _subscribers = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private uint _packetNumber;
    private bool _disposed;
    private volatile ControllerStateSnapshot? _latestState;
    private static readonly byte[] SlotMac = [0x98, 0xB6, 0xE9, 0x42, 0x10, 0x00];

    public int Port { get; private set; } = 26760;
    public bool IsRunning => _udp != null;

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        }
        return ~crc;
    }

    public bool Start(int port = 26760)
    {
        if (IsRunning) return true;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = ReceiveLoopAsync(_udp, token);
            return true;
        }
        catch
        {
            _udp = null;
            return false;
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var data = result.Buffer;
                if (data.Length < 20) continue;

                // Check "DSUC" magic
                if (data[0] != (byte)'D' || data[1] != (byte)'S' || data[2] != (byte)'U' || data[3] != (byte)'C')
                    continue;

                uint msgType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4));
                if (msgType == 0x00100000)
                {
                    byte[] response = new byte[22];
                    WriteHeader(response, msgType, 2);
                    BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(20), (ushort)ProtocolVersion);
                    FinalizeCrc(response);
                    await udp.SendAsync(response, result.RemoteEndPoint, ct).ConfigureAwait(false);
                }
                else if (msgType == 0x00100001 && data.Length >= 24)
                {
                    int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20, 4));
                    if (count is < 0 or > 4 || data.Length < 24 + count) continue;
                    for (int i = 0; i < count; i++)
                    {
                        byte slot = data[24 + i];
                        if (slot > 3) continue;
                        byte[] resp = BuildSlotInfoPacket(slot, slot == 0 ? _latestState : null);
                        await udp.SendAsync(resp, result.RemoteEndPoint, ct).ConfigureAwait(false);
                    }
                }
                else if (msgType == 0x00100002 && data.Length >= 28)
                {
                    byte flags = data[20];
                    if ((flags & 1) != 0 && data[21] != 0) continue;
                    if ((flags & 2) != 0 && !data.AsSpan(22, 6).SequenceEqual(SlotMac)) continue;
                    _subscribers[result.RemoteEndPoint] = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore malformed UDP packets
            }
        }
    }

    public void BroadcastControllerState(RoutedInput? input)
    {
        var previous = _latestState;
        _latestState = input?.State;
        var udp = _udp;
        if (udp == null || _subscribers.IsEmpty) return;
        if (input == null && previous == null) return;

        var now = DateTime.UtcNow;
        byte[] packet = BuildControllerDataPacket(0, input);

        foreach (var kvp in _subscribers)
        {
            if ((now - kvp.Value).TotalSeconds > 10.0)
            {
                _subscribers.TryRemove(kvp.Key, out _);
                continue;
            }
            try
            {
                udp.Send(packet, packet.Length, kvp.Key);
            }
            catch { }
        }
    }

    private byte[] BuildSlotInfoPacket(byte slot, ControllerStateSnapshot? state)
    {
        byte[] pkt = new byte[32];
        WriteHeader(pkt, 0x00100001, 12);
        FillSlot(pkt, slot, state);
        FinalizeCrc(pkt);
        return pkt;
    }

    private static void FillSlot(byte[] packet, byte slot, ControllerStateSnapshot? state)
    {
        packet[20] = slot;
        if (state == null) return;
        packet[21] = 2;
        packet[22] = 2;
        SlotMac.CopyTo(packet, 24);
        packet[30] = state.Battery.DsuLevel;
    }

    private byte[] BuildControllerDataPacket(byte slot, RoutedInput? input)
    {
        var state = input?.State;
        byte[] pkt = new byte[100];
        WriteHeader(pkt, 0x00100002, 80);

        FillSlot(pkt, slot, state);
        pkt[31] = state != null ? (byte)1 : (byte)0;
        var s = state ?? new ControllerStateSnapshot();

        uint pktNum = Interlocked.Increment(ref _packetNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(32, 4), pktNum);

        var btns = input?.Buttons ?? SwitchButtons.None;
        byte b1 = 0;
        if (btns.HasFlag(SwitchButtons.Minus)) b1 |= 0x01;
        if (btns.HasFlag(SwitchButtons.LStick)) b1 |= 0x02;
        if (btns.HasFlag(SwitchButtons.RStick)) b1 |= 0x04;
        if (btns.HasFlag(SwitchButtons.Plus)) b1 |= 0x08;
        if (btns.HasFlag(SwitchButtons.Up)) b1 |= 0x10;
        if (btns.HasFlag(SwitchButtons.Right)) b1 |= 0x20;
        if (btns.HasFlag(SwitchButtons.Down)) b1 |= 0x40;
        if (btns.HasFlag(SwitchButtons.Left)) b1 |= 0x80;
        pkt[36] = b1;

        byte b2 = 0;
        if (btns.HasFlag(SwitchButtons.ZL)) b2 |= 0x01;
        if (btns.HasFlag(SwitchButtons.ZR)) b2 |= 0x02;
        if (btns.HasFlag(SwitchButtons.L)) b2 |= 0x04;
        if (btns.HasFlag(SwitchButtons.R)) b2 |= 0x08;
        if (btns.HasFlag(SwitchButtons.X)) b2 |= 0x10;
        if (btns.HasFlag(SwitchButtons.A)) b2 |= 0x20;
        if (btns.HasFlag(SwitchButtons.B)) b2 |= 0x40;
        if (btns.HasFlag(SwitchButtons.Y)) b2 |= 0x80;
        pkt[37] = b2;

        if (btns.HasFlag(SwitchButtons.Home)) pkt[38] = 0x01;

        pkt[40] = StickByte(s.LeftStickX);
        pkt[41] = StickByte(s.LeftStickY);
        pkt[42] = StickByte(s.RightStickX);
        pkt[43] = StickByte(s.RightStickY);
        pkt[44] = ButtonByte(btns, SwitchButtons.Left);
        pkt[45] = ButtonByte(btns, SwitchButtons.Down);
        pkt[46] = ButtonByte(btns, SwitchButtons.Right);
        pkt[47] = ButtonByte(btns, SwitchButtons.Up);
        pkt[48] = ButtonByte(btns, SwitchButtons.Y);
        pkt[49] = ButtonByte(btns, SwitchButtons.B);
        pkt[50] = ButtonByte(btns, SwitchButtons.A);
        pkt[51] = ButtonByte(btns, SwitchButtons.X);
        pkt[52] = ButtonByte(btns, SwitchButtons.R);
        pkt[53] = ButtonByte(btns, SwitchButtons.L);
        pkt[54] = s.RightTriggerAnalog;
        pkt[55] = s.LeftTriggerAnalog;

        ulong microSec = (ulong)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
        BinaryPrimitives.WriteUInt64LittleEndian(pkt.AsSpan(68, 8), microSec);

        // Accelerometer (g) & Gyroscope (deg/s)
        BinaryPrimitives.WriteSingleLittleEndian(pkt.AsSpan(76, 4), s.AccelG_X);
        BinaryPrimitives.WriteSingleLittleEndian(pkt.AsSpan(80, 4), s.AccelG_Z);
        BinaryPrimitives.WriteSingleLittleEndian(pkt.AsSpan(84, 4), s.AccelG_Y);
        if (input?.SuppressGyro != true)
        {
            BinaryPrimitives.WriteSingleLittleEndian(pkt.AsSpan(88, 4), s.GyroDps_X);
            BinaryPrimitives.WriteSingleLittleEndian(pkt.AsSpan(92, 4), -s.GyroDps_Z);
            BinaryPrimitives.WriteSingleLittleEndian(pkt.AsSpan(96, 4), s.GyroDps_Y);
        }

        FinalizeCrc(pkt);
        return pkt;
    }

    private static byte StickByte(float axis) => (byte)Math.Clamp((int)MathF.Round((axis + 1f) * 127.5f), 0, 255);

    private static byte ButtonByte(SwitchButtons buttons, SwitchButtons mask) => (buttons & mask) != 0 ? (byte)255 : (byte)0;

    private void WriteHeader(Span<byte> pkt, uint msgType, ushort payloadLen)
    {
        pkt[0] = (byte)'D';
        pkt[1] = (byte)'S';
        pkt[2] = (byte)'U';
        pkt[3] = (byte)'S';
        BinaryPrimitives.WriteUInt16LittleEndian(pkt.Slice(4, 2), (ushort)ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(pkt.Slice(6, 2), (ushort)(payloadLen + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.Slice(8, 4), 0); // CRC placeholder
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.Slice(12, 4), _serverId);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.Slice(16, 4), msgType);
    }

    private static void FinalizeCrc(Span<byte> pkt)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.Slice(8, 4), 0);
        uint crc = ComputeCrc32(pkt);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.Slice(8, 4), crc);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _udp?.Close(); } catch { }
        _udp = null;
        _cts?.Dispose();
        _cts = null;
        _subscribers.Clear();
        _latestState = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
