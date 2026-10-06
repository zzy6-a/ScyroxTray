// Hid.cs - 原生 HID 枚举与重叠 I/O（思路来自 rapoo-tray 的 device_manager.cpp）
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScyroxTray
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string DevicePath;
    }

    internal static class Native
    {
        public const int GENERIC_READ = unchecked((int)0x80000000);
        public const int GENERIC_WRITE = 0x40000000;
        public const int FILE_SHARE_READ = 0x1;
        public const int FILE_SHARE_WRITE = 0x2;
        public const int OPEN_EXISTING = 3;
        public const int FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;
        public const int ERROR_IO_PENDING = 997;
        public const int WAIT_OBJECT_0 = 0;
        public const int WAIT_TIMEOUT = 258;

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetAttributes(IntPtr handle, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetProductString(IntPtr handle, System.Text.StringBuilder buffer, int length);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetManufacturerString(IntPtr handle, System.Text.StringBuilder buffer, int length);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetSerialNumberString(IntPtr handle, System.Text.StringBuilder buffer, int length);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData,
            int detailSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, System.Text.StringBuilder instanceId,
            int instanceIdSize, out int requiredSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string fileName, int desiredAccess, int shareMode,
            IntPtr securityAttributes, int creationDisposition, int flagsAndAttributes, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr handle, IntPtr buffer, int bytesToRead,
            out int bytesRead, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr handle, IntPtr buffer, int bytesToWrite,
            out int bytesWritten, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr handle, ref NativeOverlapped overlapped,
            out int bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIo(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEvent(IntPtr securityAttributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ResetEvent(IntPtr handle);

        public static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
    }

    /// <summary>枚举到的单个 HID 集合。</summary>
    internal sealed class HidCollection
    {
        public string DevicePath;
        public string InstanceId;
        public ushort VendorId;
        public ushort ProductId;
        public string Product = "";
        public string Manufacturer = "";
        public ushort UsagePage;
        public ushort Usage;
        public int InputReportLength;
        public int OutputReportLength;
        public int FeatureReportLength;

        public override string ToString()
        {
            return string.Format("VID_{0:X4}&PID_{1:X4} UP=0x{2:X4} US=0x{3:X4} in={4} out={5} feat={6}",
                VendorId, ProductId, UsagePage, Usage, InputReportLength, OutputReportLength, FeatureReportLength);
        }
    }

    /// <summary>HID 集合枚举（SetupAPI + hid.dll，等价于 rapoo-tray 的 FindRapooEndpoints 扫描）。</summary>
    internal static class HidEnumerator
    {
        public static List<HidCollection> Enumerate()
        {
            List<HidCollection> list = new List<HidCollection>();
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.InvalidHandleValue) return list;

            try
            {
                SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (int i = 0; ; i++)
                {
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data)) break;
                    SP_DEVICE_INTERFACE_DETAIL_DATA detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                    detail.cbSize = IntPtr.Size == 8 ? 8 : 6;
                    int required = 0;
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, ref detail,
                            Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DETAIL_DATA)), out required, IntPtr.Zero))
                        continue;

                    HidCollection col = new HidCollection();
                    col.DevicePath = detail.DevicePath;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
                    int need = 0;
                    if (Native.SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out need))
                        col.InstanceId = sb.ToString();

                    IntPtr h = Native.CreateFile(col.DevicePath, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                        IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == Native.InvalidHandleValue) continue;
                    try
                    {
                        HIDD_ATTRIBUTES attrs = new HIDD_ATTRIBUTES();
                        attrs.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                        if (Native.HidD_GetAttributes(h, ref attrs))
                        {
                            col.VendorId = attrs.VendorID;
                            col.ProductId = attrs.ProductID;
                        }
                        IntPtr preparsed;
                        if (Native.HidD_GetPreparsedData(h, out preparsed))
                        {
                            HIDP_CAPS caps = new HIDP_CAPS();
                            if (Native.HidP_GetCaps(preparsed, ref caps) == 0x00110000)
                            {
                                col.UsagePage = caps.UsagePage;
                                col.Usage = caps.Usage;
                                col.InputReportLength = caps.InputReportByteLength;
                                col.OutputReportLength = caps.OutputReportByteLength;
                                col.FeatureReportLength = caps.FeatureReportByteLength;
                            }
                            Native.HidD_FreePreparsedData(preparsed);
                        }
                        System.Text.StringBuilder text = new System.Text.StringBuilder(256);
                        if (Native.HidD_GetProductString(h, text, text.Capacity)) col.Product = text.ToString().Trim();
                        text = new System.Text.StringBuilder(256);
                        if (Native.HidD_GetManufacturerString(h, text, text.Capacity)) col.Manufacturer = text.ToString().Trim();
                    }
                    finally
                    {
                        Native.CloseHandle(h);
                    }
                    list.Add(col);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return list;
        }
    }

    /// <summary>重叠 I/O 的 HID 句柄：读带超时，写带超时，可随时关闭。</summary>
    internal sealed class HidDevice : IDisposable
    {
        private IntPtr _handle = Native.InvalidHandleValue;
        private IntPtr _readEvent = IntPtr.Zero;
        private IntPtr _writeEvent = IntPtr.Zero;
        private IntPtr _readBuffer = IntPtr.Zero;
        private IntPtr _writeBuffer = IntPtr.Zero;
        private readonly int _readBufferSize;
        private readonly int _writeBufferSize;
        private readonly object _ioLock = new object();

        public string DevicePath { get; private set; }
        public int InputReportLength { get; private set; }
        public int OutputReportLength { get; private set; }
        public bool IsOpen { get { return _handle != Native.InvalidHandleValue; } }

        public HidDevice(string devicePath, int inputReportLength, int outputReportLength)
        {
            DevicePath = devicePath;
            InputReportLength = Math.Max(64, inputReportLength);
            OutputReportLength = Math.Max(64, outputReportLength);
            _readBufferSize = InputReportLength;
            _writeBufferSize = OutputReportLength;
        }

        public bool Open()
        {
            _handle = Native.CreateFile(DevicePath,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_handle == Native.InvalidHandleValue)
            {
                _handle = Native.InvalidHandleValue;
                return false;
            }
            _readEvent = Native.CreateEvent(IntPtr.Zero, true, false, null);
            _writeEvent = Native.CreateEvent(IntPtr.Zero, true, false, null);
            _readBuffer = Marshal.AllocHGlobal(_readBufferSize);
            _writeBuffer = Marshal.AllocHGlobal(_writeBufferSize);
            return true;
        }

        /// <summary>阻塞读取一个报文（含 Report ID），超时返回 null。</summary>
        public byte[] Read(int timeoutMs)
        {
            lock (_ioLock)
            {
                if (!IsOpen) return null;
                Native.ResetEvent(_readEvent);
                NativeOverlapped ov = new NativeOverlapped();
                ov.EventHandle = _readEvent;
                int read = 0;
                bool ok = Native.ReadFile(_handle, _readBuffer, _readBufferSize, out read, ref ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING) return null;
                    int wait = Native.WaitForSingleObject(_readEvent, timeoutMs);
                    if (wait != Native.WAIT_OBJECT_0)
                    {
                        Native.CancelIo(_handle);
                        return null;
                    }
                    if (!Native.GetOverlappedResult(_handle, ref ov, out read, false)) return null;
                }
                if (read <= 0) return null;
                byte[] data = new byte[read];
                Marshal.Copy(_readBuffer, data, 0, read);
                return data;
            }
        }

        /// <summary>写一个报文（含 Report ID），超时返回 false。</summary>
        public bool Write(byte[] payload, int timeoutMs)
        {
            lock (_ioLock)
            {
                if (!IsOpen) return false;
                int len = Math.Min(payload.Length, _writeBufferSize);
                Marshal.Copy(payload, 0, _writeBuffer, len);
                Native.ResetEvent(_writeEvent);
                NativeOverlapped ov = new NativeOverlapped();
                ov.EventHandle = _writeEvent;
                int written = 0;
                bool ok = Native.WriteFile(_handle, _writeBuffer, len, out written, ref ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING) return false;
                    int wait = Native.WaitForSingleObject(_writeEvent, timeoutMs);
                    if (wait != Native.WAIT_OBJECT_0)
                    {
                        Native.CancelIo(_handle);
                        return false;
                    }
                    if (!Native.GetOverlappedResult(_handle, ref ov, out written, false)) return false;
                }
                return written == len;
            }
        }

        public void Dispose()
        {
            lock (_ioLock)
            {
                if (_handle != Native.InvalidHandleValue)
                {
                    Native.CancelIo(_handle);
                    Native.CloseHandle(_handle);
                    _handle = Native.InvalidHandleValue;
                }
                if (_readEvent != IntPtr.Zero) { Native.CloseHandle(_readEvent); _readEvent = IntPtr.Zero; }
                if (_writeEvent != IntPtr.Zero) { Native.CloseHandle(_writeEvent); _writeEvent = IntPtr.Zero; }
                if (_readBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_readBuffer); _readBuffer = IntPtr.Zero; }
                if (_writeBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_writeBuffer); _writeBuffer = IntPtr.Zero; }
            }
        }
    }
}
