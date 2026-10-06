// Probe7.cs - 第七轮：命令字节偏移扫描
//
// 旧协议假设 Report ID 8 之后 buf[1] 是命令。新接收器完全回显该结构，
// 因此换一个假设：命令字节可能落在报文的其他偏移上。逐个偏移 × 命令值扫描，
// 只报告「非回显」的响应。
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe7
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

            byte[] cmdValues = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                                            0x0A, 0x0B, 0x0C, 0x0E, 0x0F, 0x10, 0x12, 0x1D,
                                            0x20, 0x40, 0x80, 0xA0, 0xF0, 0xFF };

            using (HidDevice dev = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!dev.Open()) { Console.WriteLine("打开失败"); return 1; }

                int echo = 0, diff = 0, silent = 0;
                for (int off = 1; off <= 6; off++)
                {
                    foreach (byte v in cmdValues)
                    {
                        byte[] pkt = new byte[64];
                        pkt[0] = 0x08;
                        pkt[off] = v;
                        if (!dev.Write(pkt, 300)) { silent++; continue; }

                        bool got = false;
                        DateTime e = DateTime.Now.AddMilliseconds(140);
                        while (DateTime.Now < e)
                        {
                            byte[] rx = dev.Read(70);
                            if (rx == null) continue;
                            got = true;
                            bool isEcho = rx.Length >= 64;
                            if (isEcho)
                                for (int k = 0; k < 64; k++) if (rx[k] != pkt[k]) { isEcho = false; break; }
                            if (isEcho) echo++;
                            else
                            {
                                diff++;
                                Console.WriteLine("*** 非回显  偏移=" + off + " 值=0x" + v.ToString("X2"));
                                Console.WriteLine("    TX " + Hex(pkt));
                                Console.WriteLine("    RX " + Hex(rx));
                            }
                        }
                        if (!got) silent++;
                    }
                }
                Console.WriteLine();
                Console.WriteLine("扫描完毕: 回显 " + echo + " / 非回显 " + diff + " / 无响应 " + silent);
                if (diff == 0)
                    Console.WriteLine("结论: 该通道对任何偏移/命令组合都只回显 —— 确认协议不兼容，需按新协议逆向");
            }
            return 0;
        }
    }
}
