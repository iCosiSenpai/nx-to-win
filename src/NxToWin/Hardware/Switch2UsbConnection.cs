using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NxToWin.Models;
using Windows.Devices.Enumeration;

namespace NxToWin.Hardware;

// Nintendo's vendor interface configures the pad. Input and rumble use its separate HID interface.
internal sealed class Switch2UsbConnection : IDisposable
{
    private const string ContainerProperty = "System.Devices.ContainerId";
    private const string InterfaceClass = "{6F13725E-EF0E-4FD3-AE5F-B2DE989EC825}";
    private readonly SafeFileHandle _file;
    private readonly IntPtr _interface;

    private Switch2UsbConnection(string path)
    {
        _file = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        try
        {
            if (_file.IsInvalid || !WinUsb_Initialize(_file, out _interface))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            uint timeout = 350;
            foreach (byte endpoint in new byte[] { 0x82, 0x02 })
                if (!WinUsb_SetPipePolicy(_interface, endpoint, 3, sizeof(uint), ref timeout))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Timeout WinUSB");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static Switch2UsbConnection Open(string hidPath, ushort pid)
    {
        var hid = DeviceInformation.CreateFromIdAsync(hidPath, [ContainerProperty], DeviceInformationKind.DeviceInterface)
            .AsTask().GetAwaiter().GetResult();
        if (hid == null || !hid.Properties.TryGetValue(ContainerProperty, out var identity) ||
            identity is not Guid container || container == Guid.Empty)
            throw new InvalidOperationException("Contenitore HID non disponibile.");

        var candidates = DeviceInformation.FindAllAsync($"System.Devices.InterfaceClassGuid:=\"{InterfaceClass}\"",
            [ContainerProperty], DeviceInformationKind.DeviceInterface).AsTask().GetAwaiter().GetResult();
        var match = candidates.FirstOrDefault(d =>
            d.Id.Contains($"vid_057e&pid_{pid:x4}", StringComparison.OrdinalIgnoreCase) &&
            d.Properties.TryGetValue(ContainerProperty, out var value) && Equals(value, container));
        return match == null
            ? throw new InvalidOperationException("Interfaccia WinUSB Nintendo assente per questo controller.")
            : new Switch2UsbConnection(match.Id);
    }

    public void Initialize(bool hasOpticalSensor)
    {
        byte[] start = [1, 0, 255, 255, 255, 255, 255, 255];
        Exchange(3, 0x0D, start);
        Exchange(7, 1, []);
        byte featureMask = hasOpticalSensor ? (byte)0x37 : (byte)0x27;
        Exchange(0x0C, 2, [featureMask, 0, 0, 0]);
        Exchange(0x0C, 4, [featureMask, 0, 0, 0]);
        Exchange(3, 0x0A, [5, 0, 0, 0]);
        Exchange(3, 0x0D, start);
    }

    public StickCalibration ReadCalibration(uint factoryAddress, uint userAddress)
    {
        byte[] user = ReadFlashBlock(userAddress);
        if (user[0] == 0xB2 && user[1] == 0xA1)
        {
            var calibration = StickCalibration.FromSwitch2SpiBytes(user.AsSpan(2));
            if (calibration.IsValid) return calibration;
        }
        byte[] factory = ReadFlashBlock(factoryAddress);
        return StickCalibration.FromSwitch2SpiBytes(factory.AsSpan(0x28));
    }

    public void SetPlayerLeds(byte pattern) => Exchange(9, 7, [pattern, 0, 0, 0, 0, 0, 0, 0]);

    private byte[] ReadFlashBlock(uint address)
    {
        byte[] request = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), address);
        byte[] reply = Exchange(2, 1, request, 80);
        if (reply.Length != 80 || BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(12)) != address)
            throw new InvalidOperationException("Risposta calibrazione WinUSB non valida.");
        return reply[16..];
    }

    private byte[] Exchange(byte command, byte subcommand, byte[] data, int expected = 64)
    {
        byte[] request = [command, 0x91, 0, subcommand, 0, (byte)data.Length, 0, 0, .. data];
        if (!WinUsb_WritePipe(_interface, 0x02, request, request.Length, out int written, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Comando USB {command:X2}/{subcommand:X2}");
        if (written != request.Length)
            throw new InvalidOperationException("Scrittura WinUSB incompleta.");

        byte[] reply = new byte[expected];
        int received = 0;
        while (received < expected)
        {
            int length = Math.Min(expected - received, 64);
            byte[] block = new byte[length];
            if (!WinUsb_ReadPipe(_interface, 0x82, block, length, out int count, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Risposta USB {command:X2}/{subcommand:X2}");
            block.AsSpan(0, count).CopyTo(reply.AsSpan(received));
            received += count;
            if (count < length) break;
        }
        if (received < 8 || reply[0] != command || reply[1] != 1 || reply[3] != subcommand)
            throw new InvalidOperationException($"Risposta WinUSB inattesa per {command:X2}/{subcommand:X2}.");
        return reply[..received];
    }

    public void Dispose()
    {
        if (_interface != IntPtr.Zero) WinUsb_Free(_interface);
        _file.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);

    [DllImport("winusb.dll")]
    private static extern bool WinUsb_Free(IntPtr usb);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte endpoint, uint policy, uint length, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(IntPtr usb, byte endpoint, byte[] buffer, int length, out int written, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr usb, byte endpoint, [Out] byte[] buffer, int length, out int received, IntPtr overlapped);
}
