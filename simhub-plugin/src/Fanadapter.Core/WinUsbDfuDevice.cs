using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Fanadapter.Core
{
    /// <summary>
    /// The real <see cref="IDfuConnection"/>: the STM32 ROM bootloader (0483:DF11) opened through
    /// WinUSB. DfuSe is control-transfers only, so the P/Invoke surface is deliberately tiny —
    /// enumerate by interface GUID (cfgmgr32), CreateFile, WinUsb_Initialize, control transfers.
    /// Requires the WinUSB driver bound to "STM32 BOOTLOADER" (STM32CubeProgrammer's installer or
    /// one Zadig run does it; any machine where dfu-util works already has it) — the exact same
    /// constraint webconfig's WebUSB flasher has.
    /// </summary>
    public sealed class WinUsbDfuDevice : IDfuConnection
    {
        private const string DfuHardwareIdFragment = "vid_0483&pid_df11";

        private IntPtr _file = INVALID_HANDLE_VALUE;
        private IntPtr _winusb = IntPtr.Zero;

        public string InterfaceName { get; private set; }

        private WinUsbDfuDevice() { }

        /// <summary>
        /// Opens the first attached ROM bootloader, or returns null when none is present (not an
        /// error — the caller polls while the adapter reboots). Throws only when a device exists
        /// but cannot be opened, which almost always means the WinUSB driver isn't bound.
        /// </summary>
        public static WinUsbDfuDevice TryOpen()
        {
            string path = FindDevicePath();
            if (path == null) return null;

            var dev = new WinUsbDfuDevice();
            dev._file = CreateFile(
                path,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);
            if (dev._file == INVALID_HANDLE_VALUE)
            {
                throw new InvalidOperationException(
                    "The bootloader is attached but can't be opened (error " +
                    Marshal.GetLastWin32Error() + "). Is the WinUSB driver bound to it?");
            }

            if (!WinUsb_Initialize(dev._file, out dev._winusb))
            {
                int err = Marshal.GetLastWin32Error();
                dev.Dispose();
                throw new InvalidOperationException(
                    "WinUsb_Initialize failed (error " + err + "). The device driver bound to " +
                    "\"STM32 BOOTLOADER\" is not WinUSB — install STM32CubeProgrammer or run " +
                    "Zadig once to fix it.");
            }

            dev.InterfaceName = dev.TryReadInterfaceName();
            return dev;
        }

        /// <summary>
        /// Polls for the bootloader while the adapter reboots into it. Null on timeout.
        /// </summary>
        public static WinUsbDfuDevice WaitAndOpen(int timeoutMs, CancellationToken ct)
        {
            var deadline = Environment.TickCount + timeoutMs;
            for (;;)
            {
                ct.ThrowIfCancellationRequested();
                // The device can be enumerated but not yet claimable for a beat (Windows still
                // binding the driver right after attach) — treat open failures as "not yet"
                // until the deadline, so a slow driver bind doesn't abort the update.
                try
                {
                    var dev = TryOpen();
                    if (dev != null) return dev;
                }
                catch (InvalidOperationException)
                {
                    if (Environment.TickCount >= deadline) throw;
                }
                if (Environment.TickCount >= deadline) return null;
                Thread.Sleep(500);
            }
        }

        public void ControlOut(byte request, ushort value, byte[] data)
        {
            var setup = new WINUSB_SETUP_PACKET
            {
                // 0x21 = host-to-device | class | interface
                RequestType = 0x21,
                Request = request,
                Value = value,
                Index = 0,
                Length = (ushort)data.Length,
            };
            if (!WinUsb_ControlTransfer(_winusb, setup, data, (uint)data.Length, out _, IntPtr.Zero))
            {
                throw new InvalidOperationException(
                    "DFU control OUT failed (error " + Marshal.GetLastWin32Error() + ")");
            }
        }

        public byte[] ControlIn(byte request, ushort value, int length)
        {
            var buf = new byte[length];
            var setup = new WINUSB_SETUP_PACKET
            {
                // 0xA1 = device-to-host | class | interface
                RequestType = 0xA1,
                Request = request,
                Value = value,
                Index = 0,
                Length = (ushort)length,
            };
            if (!WinUsb_ControlTransfer(_winusb, setup, buf, (uint)length, out uint transferred, IntPtr.Zero))
            {
                throw new InvalidOperationException(
                    "DFU control IN failed (error " + Marshal.GetLastWin32Error() + ")");
            }
            if (transferred == length) return buf;
            var trimmed = new byte[transferred];
            Array.Copy(buf, trimmed, transferred);
            return trimmed;
        }

        public void Dispose()
        {
            if (_winusb != IntPtr.Zero)
            {
                WinUsb_Free(_winusb);
                _winusb = IntPtr.Zero;
            }
            if (_file != INVALID_HANDLE_VALUE)
            {
                CloseHandle(_file);
                _file = INVALID_HANDLE_VALUE;
            }
        }

        // ---------- discovery ----------

        private static string FindDevicePath()
        {
            var guid = GUID_DEVINTERFACE_USB_DEVICE;
            if (CM_Get_Device_Interface_List_Size(out uint size, ref guid, null,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 || size <= 1)
            {
                return null;
            }
            var buffer = new char[size];
            if (CM_Get_Device_Interface_List(ref guid, null, buffer, size,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0)
            {
                return null;
            }
            foreach (string path in new string(buffer).Split('\0'))
            {
                if (path.IndexOf(DfuHardwareIdFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return path;
                }
            }
            return null;
        }

        /// <summary>
        /// The DfuSe memory-layout string sits in the alt-0 interface descriptor's iInterface.
        /// Best-effort: the flasher falls back to the H743's 128K sectors when this is null.
        /// </summary>
        private string TryReadInterfaceName()
        {
            try
            {
                if (!WinUsb_QueryInterfaceSettings(_winusb, 0, out var desc)) return null;
                if (desc.iInterface == 0) return null;
                var buf = new byte[256];
                if (!WinUsb_GetDescriptor(_winusb, USB_STRING_DESCRIPTOR_TYPE, desc.iInterface,
                        0x0409, buf, (uint)buf.Length, out uint transferred) || transferred < 2)
                {
                    return null;
                }
                // bLength, bDescriptorType, then UTF-16LE payload.
                int payload = Math.Min(buf[0], (int)transferred) - 2;
                if (payload <= 0) return null;
                return Encoding.Unicode.GetString(buf, 2, payload);
            }
            catch
            {
                return null;
            }
        }

        // ---------- P/Invoke ----------

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        private static Guid GUID_DEVINTERFACE_USB_DEVICE =
            new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
        private const byte USB_STRING_DESCRIPTOR_TYPE = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINUSB_SETUP_PACKET
        {
            public byte RequestType;
            public byte Request;
            public ushort Value;
            public ushort Index;
            public ushort Length;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength;
            public byte bDescriptorType;
            public byte bInterfaceNumber;
            public byte bAlternateSetting;
            public byte bNumEndpoints;
            public byte bInterfaceClass;
            public byte bInterfaceSubClass;
            public byte bInterfaceProtocol;
            public byte iInterface;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_Size(
            out uint size, ref Guid interfaceClassGuid, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List(
            ref Guid interfaceClassGuid, string deviceId, char[] buffer, uint bufferLength,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(IntPtr deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(
            IntPtr interfaceHandle, WINUSB_SETUP_PACKET setupPacket, byte[] buffer,
            uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(
            IntPtr interfaceHandle, byte alternateSettingNumber,
            out USB_INTERFACE_DESCRIPTOR interfaceDescriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetDescriptor(
            IntPtr interfaceHandle, byte descriptorType, byte index, ushort languageId,
            byte[] buffer, uint bufferLength, out uint lengthTransferred);
    }
}
