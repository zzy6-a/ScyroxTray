// Probe2.cs - 第二轮探测：V6S（Scyrox Turbo 8k dongle）64 字节厂商集合
//
// 第一轮结论：旧协议（Report ID 8 / 16 字节 / 0x55 校验）在新接收器上零响应。
// 本轮做三件事：
//   A. 静默监听，看设备是否主动上报（无需主机请求）
//   B. 遍历 Report ID 0x00..0x0F，每个 ID 发旧命令集 0x01/0x03/0x04（16 字节报文补齐到 64）
//   C. 针对有响应的 Report ID，再细扫命令字节
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe2
    {
        private static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            string[] p = new string[d.Length];
            for (int i = 0; i < d.Length; i++) p[i] = d[i].ToString("X2");
            return string.Join(" ", p);
        }

        /// <summary>构造 64 字节输出报文：buf[0]=Report ID，buf[1..16]=旧格式 16 字节报文，其余补 0。</summary>
        private static byte[] BuildPadded(byte rid, byte cmd)
        {
            byte[] buf = new byte[64];
            buf[0] = rid;
            byte[] pkt = new byte[16];
            pkt[0] = cmd;
            int sum = 0;
            for (int i = 0; i < 15; i++) sum += pkt[i];
            pkt[15] = (byte)((0x55 - (sum & 0xFF) - rid) & 0xFF);
            Array.Copy(pkt, 0, buf, 1, 16);
            return buf;
        }

        private static int Main(string[] args)
        {
            ushort wantVid = 0x395A, wantPid = 0x000E;
            List<HidCollection> all = HidEnumerator.Enumerate();
            HidCollection target = null;
            foreach (HidCollection c in all)
            {
                if (c.VendorId != wantVid || c.ProductId != wantPid) continue;
                if (c.InputReportLength < 17 || c.OutputReportLength < 17) continue;
                target = c;
            }
            if (target == null) { Console.WriteLine("未找到目标集合"); return 1; }

            Console.WriteLine("目标: " + target);
            Console.WriteLine("path: " + target.DevicePath);
            Console.WriteLine();

            using (HidDevice dev = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!dev.Open()) { Console.WriteLine("打开失败"); return 1; }

                Console.WriteLine("[A] 静默监听 2.5s（设备主动上报？）");
                DateTime endA = DateTime.Now.AddMilliseconds(2500);
                int n = 0;
                while (DateTime.Now < endA)
                {
                    byte[] rx = dev.Read(300);
                    if (rx != null) { n++; Console.WriteLine("   RX " + Hex(rx)); }
                }
                Console.WriteLine("   主动上报 " + n + " 条");
                Console.WriteLine();

                Console.WriteLine("[B] 遍历 Report ID 0x00..0x0F × 命令 0x01/0x03/0x04（16B 报文补齐 64B）");
                List<byte> responsive = new List<byte>();
                for (byte rid = 0x00; rid <= 0x0F; rid++)
                {
                    foreach (byte cmd in new byte[] { 0x01, 0x03, 0x04 })
                    {
                        byte[] pkt = BuildPadded(rid, cmd);
                        bool ok = dev.Write(pkt, 500);
                        Console.Write("   TX rid=" + rid.ToString("X2") + " cmd=" + cmd.ToString("X2") + " write=" + ok);
                        bool got = false;
                        DateTime e = DateTime.Now.AddMilliseconds(220);
                        while (DateTime.Now < e)
                        {
                            byte[] rx = dev.Read(110);
                            if (rx != null) { got = true; Console.Write("\n      RX " + Hex(rx)); }
                        }
                        if (!got) Console.Write("   (无响应)");
                        else if (!responsive.Contains(rid)) responsive.Add(rid);
                        Console.WriteLine();
                    }
                }
                Console.WriteLine();
                Console.WriteLine("[B 结果] 有响应的 Report ID: " + (responsive.Count == 0 ? "无" : string.Join(", ", responsive.ConvertAll(b => "0x" + b.ToString("X2")).ToArray())));
            }
            Console.WriteLine();
            Console.WriteLine("探测结束");
            return 0;
        }
    }
}
