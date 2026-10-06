// Probe11.cs - 长时间监听输入报告，按 report id 归类
//
// 依据 S-Center 2.0 前端（3950-B1GMRg1a.js 的 HID input 处理）：
//   report id 11 → DPI 切换，report id 32 → 某值，report id 14 → 电量（t[3]）
// 说明设备会主动推送多条 report id 的输入报告，而查询式读取（report id 8）
// 拿到的是另一类数据。本工具静默监听全部集合，按 report id 统计并打印样本。
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe11
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
            int seconds = args.Length >= 1 ? Convert.ToInt32(args[0]) : 60;
            ushort wantVid = 0x395A, wantPid = 0x000E;
            List<HidDevice> devices = new List<HidDevice>();
            List<string> labels = new List<string>();

            List<HidCollection> all = HidEnumerator.Enumerate();
            foreach (HidCollection c in all)
            {
                if (c.VendorId != wantVid || c.ProductId != wantPid) continue;
                if (c.InputReportLength < 1) continue;
                HidDevice dev = new HidDevice(c.DevicePath, c.InputReportLength, Math.Max(1, c.OutputReportLength));
                if (!dev.Open()) { dev.Dispose(); continue; }
                devices.Add(dev);
                labels.Add(c.ToString());
                Console.WriteLine("监听 [" + (devices.Count - 1) + "] " + c);
            }
            if (devices.Count == 0) { Console.WriteLine("没有可监听的集合"); return 1; }

            Console.WriteLine();
            Console.WriteLine("静默监听 " + seconds + " 秒（期间请移动鼠标/点击）...");
            Console.WriteLine();

            Dictionary<int, int> counts = new Dictionary<int, int>();
            Dictionary<int, string> samples = new Dictionary<int, string>();
            DateTime end = DateTime.Now.AddSeconds(seconds);
            int total = 0;

            while (DateTime.Now < end)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    byte[] rx = devices[i].Read(60);
                    if (rx == null || rx.Length == 0) continue;
                    total++;
                    int rid = rx[0];
                    if (!counts.ContainsKey(rid)) { counts[rid] = 0; samples[rid] = labels[i] + "  " + Hex(rx); }
                    counts[rid]++;
                }
            }

            foreach (HidDevice dev in devices) dev.Dispose();

            Console.WriteLine("共收到 " + total + " 条输入报告");
            Console.WriteLine();
            Console.WriteLine("==== 按 Report ID 归类 ====");
            foreach (KeyValuePair<int, int> kv in counts)
            {
                Console.WriteLine("  Report ID " + kv.Key + " (0x" + kv.Key.ToString("X2") + ") : " + kv.Value + " 条");
                Console.WriteLine("      样本: " + samples[kv.Key]);
            }
            return 0;
        }
    }
}
