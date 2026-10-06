// Sniffer.cs - HID 报文监听器（用于抓取官方软件与新接收器的通信）
//
// 原理：Scyrox Turbo 8k dongle 会把主机输出报告回送到输入端点（实测 loopback）。
// 因此当官方软件（S-Center 等）在该通道上查询电量时，本工具作为第二个读者
// 可以同时看到它发出的命令与设备返回的响应 —— 无需 hook、无需驱动。
//
// 用法：sniffer.exe [监听秒数] [VID_HEX] [PID_HEX]
//   默认：60 秒、395A、000E
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Sniffer
    {
        private static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            string[] p = new string[d.Length];
            for (int i = 0; i < d.Length; i++) p[i] = d[i].ToString("X2");
            return string.Join(" ", p);
        }

        private static int Main(string[] args)
        {
            int seconds = 60;
            ushort wantVid = 0x395A, wantPid = 0x000E;
            if (args.Length >= 1) seconds = Convert.ToInt32(args[0]);
            if (args.Length >= 3) { wantVid = Convert.ToUInt16(args[1], 16); wantPid = Convert.ToUInt16(args[2], 16); }

            List<HidCollection> all = HidEnumerator.Enumerate();
            List<HidDevice> devices = new List<HidDevice>();
            foreach (HidCollection c in all)
            {
                if (c.VendorId != wantVid || c.ProductId != wantPid) continue;
                if (c.InputReportLength < 1) continue;
                HidDevice dev = new HidDevice(c.DevicePath, c.InputReportLength, Math.Max(1, c.OutputReportLength));
                if (!dev.Open()) { dev.Dispose(); continue; }
                devices.Add(dev);
                Console.WriteLine("监听 " + c);
            }
            if (devices.Count == 0) { Console.WriteLine("没有可监听的集合"); return 1; }

            Console.WriteLine();
            Console.WriteLine("开始监听 " + seconds + " 秒 —— 现在请在官方软件里点一次「刷新电量/查看鼠标」");
            Console.WriteLine("（只打印非全零输入报告）");
            Console.WriteLine();

            DateTime end = DateTime.Now.AddSeconds(seconds);
            int count = 0;
            while (DateTime.Now < end)
            {
                bool any = false;
                for (int i = 0; i < devices.Count; i++)
                {
                    byte[] rx = devices[i].Read(120);
                    if (rx == null) continue;
                    // 过滤鼠标移动/键盘产生的全零或纯位图报告，只保留厂商通道与含数据帧
                    bool allZero = true;
                    for (int k = 1; k < rx.Length; k++) if (rx[k] != 0) { allZero = false; break; }
                    if (allZero && rx.Length <= 16) continue;

                    count++;
                    any = true;
                    Console.WriteLine(string.Format("[{0:HH:mm:ss.fff}] #{1} len={2}  {3}",
                        DateTime.Now, count, rx.Length, Hex(rx)));
                }
                if (!any) System.Threading.Thread.Sleep(20);
            }

            foreach (HidDevice dev in devices) dev.Dispose();
            Console.WriteLine();
            Console.WriteLine("监听结束，共 " + count + " 条报告");
            return 0;
        }
    }
}
