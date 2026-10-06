// Probe6.cs - 第六轮：解析 HID 字段布局（HidP_GetValueCaps）
//
// 目的：报告描述符取不到（驱动不支持该 IOCTL），改用 preparsed data 反查
// 每个集合声明的字段（Usage Page / Usage / Report ID / BitSize / ReportCount）。
// 若出现 Usage Page 0x06/0x85 之类的电池用途，就能直接定位电量字段。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScyroxTray
{
    internal static class Probe6
    {
        [DllImport("hid.dll")]
        private static extern int HidP_GetValueCaps(int ReportType, byte[] ValueCaps, ref ushort ValueCapsLength, IntPtr PreparsedData);

        // HIDP_VALUE_CAPS 固定 48 字节（x64 对齐）
        private const int CapsSize = 48;

        private static ushort U16(byte[] b, int off) { return (ushort)(b[off] | (b[off + 1] << 8)); }

        private static void DumpCaps(string title, int reportType, IntPtr preparsed)
        {
            ushort len = 128;
            byte[] buf = new byte[CapsSize * len];
            int st = HidP_GetValueCaps(reportType, buf, ref len, preparsed);
            if (st != 0 && len == 0) { Console.WriteLine("     " + title + ": 无字段 (status=0x" + st.ToString("X8") + ")"); return; }
            if (len == 0) { Console.WriteLine("     " + title + ": 0 个字段"); return; }

            Console.WriteLine("     " + title + ": " + len + " 个字段");
            for (int i = 0; i < len; i++)
            {
                int o = i * CapsSize;
                ushort usagePage = U16(buf, o + 0);
                byte reportId = buf[o + 2];
                ushort linkUsagePage = U16(buf, o + 10);
                ushort linkUsage = U16(buf, o + 8);
                bool isRange = buf[o + 12] != 0;
                ushort bitSize = U16(buf, o + 18);
                ushort reportCount = U16(buf, o + 20);
                ushort usageMin = U16(buf, o + 32);
                ushort usageMax = U16(buf, o + 34);
                ushort usage = U16(buf, o + 32);

                string usageText = isRange
                    ? "0x" + usageMin.ToString("X4") + "..0x" + usageMax.ToString("X4")
                    : "0x" + usage.ToString("X4");

                Console.WriteLine(string.Format(
                    "       [{0}] UP=0x{1:X4} Usage={2} RID={3} BitSize={4} Count={5} link=(UP=0x{6:X4} U=0x{7:X4})",
                    i, usagePage, usageText, reportId, bitSize, reportCount, linkUsagePage, linkUsage));
            }
        }

        private static int Main(string[] args)
        {
            ushort wantVid = 0x395A, wantPid = 0x000E;
            List<HidCollection> all = HidEnumerator.Enumerate();
            foreach (HidCollection c in all)
            {
                if (c.VendorId != wantVid || c.ProductId != wantPid) continue;
                Console.WriteLine("==== " + c);

                IntPtr h = Native.CreateFile(c.DevicePath, 0,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == Native.InvalidHandleValue) { Console.WriteLine("     打开失败 err=" + Marshal.GetLastWin32Error()); Console.WriteLine(); continue; }

                IntPtr preparsed;
                if (!Native.HidD_GetPreparsedData(h, out preparsed))
                {
                    Console.WriteLine("     GetPreparsedData 失败 err=" + Marshal.GetLastWin32Error());
                    Native.CloseHandle(h);
                    Console.WriteLine();
                    continue;
                }
                try
                {
                    DumpCaps("Input ", 0, preparsed);
                    DumpCaps("Output", 1, preparsed);
                    DumpCaps("Feature", 2, preparsed);
                }
                finally
                {
                    Native.HidD_FreePreparsedData(preparsed);
                    Native.CloseHandle(h);
                }
                Console.WriteLine();
            }
            return 0;
        }
    }
}
