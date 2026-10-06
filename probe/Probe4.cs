// Probe4.cs - 第四轮：读取 HID 报告描述符，还原字段布局
//
// 报告描述符是设备自己声明的数据契约：能直接看出有没有电池字段、
// 每个 Report ID 的字段大小与用途，避免继续盲猜报文。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ScyroxTray
{
    internal static class Probe4
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        private const uint IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0192;

        private static int Main(string[] args)
        {
            ushort wantVid = 0x395A, wantPid = 0x000E;
            if (args.Length >= 2) { wantVid = Convert.ToUInt16(args[0], 16); wantPid = Convert.ToUInt16(args[1], 16); }

            List<HidCollection> all = HidEnumerator.Enumerate();
            int n = 0;
            foreach (HidCollection c in all)
            {
                if (c.VendorId != wantVid || c.ProductId != wantPid) continue;
                n++;
                Console.WriteLine("==== " + c);
                Console.WriteLine("     " + c.DevicePath);

                IntPtr h = Native.CreateFile(c.DevicePath, Native.GENERIC_READ,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == Native.InvalidHandleValue)
                {
                    Console.WriteLine("     打开失败 err=" + Marshal.GetLastWin32Error());
                    Console.WriteLine();
                    continue;
                }
                try
                {
                    byte[] buf = new byte[4096];
                    uint ret;
                    bool ok = DeviceIoControl(h, IOCTL_HID_GET_REPORT_DESCRIPTOR,
                        IntPtr.Zero, 0, buf, (uint)buf.Length, out ret, IntPtr.Zero);
                    if (!ok || ret == 0)
                    {
                        Console.WriteLine("     取报告描述符失败 err=" + Marshal.GetLastWin32Error() + " ret=" + ret);
                        Console.WriteLine();
                        continue;
                    }
                    Console.WriteLine("     报告描述符 " + ret + " 字节:");
                    for (int i = 0; i < ret; i += 16)
                    {
                        StringBuilder hex = new StringBuilder();
                        hex.Append("       " + i.ToString("X4") + "  ");
                        for (int k = 0; k < 16; k++)
                            hex.Append(i + k < ret ? buf[i + k].ToString("X2") + " " : "   ");
                        hex.Append("  ");
                        for (int k = 0; k < 16 && i + k < ret; k++)
                        {
                            byte b = buf[i + k];
                            hex.Append(b >= 32 && b < 127 ? (char)b : '.');
                        }
                        Console.WriteLine(hex.ToString());
                    }
                }
                finally
                {
                    Native.CloseHandle(h);
                }
                Console.WriteLine();
            }
            Console.WriteLine("共 " + n + " 个目标集合");
            return 0;
        }
    }
}
