// Probe5.cs - 第五轮：持续探测，区分「接收器回显」与「鼠标在线时的真实应答」
//
// 背景：V6S 接收器（VID_395A/PID_000E）的 64 字节厂商集合在鼠标不在线时
// 表现为纯 loopback（主机报文原样回送）。本工具在 15 秒窗口内反复询问，
// 只要鼠标处于活动状态，就可能拿到真实数据帧。
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe5
    {
        private static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            string[] p = new string[d.Length];
            for (int i = 0; i < d.Length; i++) p[i] = d[i].ToString("X2");
            return string.Join(" ", p);
        }

        private static byte[] Nested(byte rid, byte cmd)
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
            Console.WriteLine("持续探测 15 秒（期间请移动鼠标）...");
            Console.WriteLine();

            using (HidDevice dev = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!dev.Open()) { Console.WriteLine("打开失败"); return 1; }

                int echo = 0, diff = 0, silent = 0;
                DateTime end = DateTime.Now.AddSeconds(15);
                byte[] lastCmd = null;
                int round = 0;
                while (DateTime.Now < end)
                {
                    round++;
                    foreach (byte cmd in new byte[] { 0x03, 0x04 })
                    {
                        byte[] pkt = Nested(0x08, cmd);
                        lastCmd = pkt;
                        if (!dev.Write(pkt, 300)) { silent++; continue; }

                        bool got = false;
                        DateTime e = DateTime.Now.AddMilliseconds(200);
                        while (DateTime.Now < e)
                        {
                            byte[] rx = dev.Read(80);
                            if (rx == null) continue;
                            got = true;
                            bool isEcho = rx.Length >= 17;
                            if (isEcho)
                                for (int k = 0; k < 17; k++) if (rx[k] != pkt[k]) { isEcho = false; break; }

                            if (isEcho) echo++;
                            else
                            {
                                diff++;
                                Console.WriteLine("[第 " + round + " 轮] cmd=0x" + cmd.ToString("X2") + "  *** 非回显响应 ***");
                                Console.WriteLine("   TX " + Hex(pkt));
                                Console.WriteLine("   RX " + Hex(rx));
                                Console.WriteLine();
                            }
                        }
                        if (!got) silent++;
                    }
                    System.Threading.Thread.Sleep(250);
                }
                Console.WriteLine("15 秒统计: 回显 " + echo + " 次 / 非回显 " + diff + " 次 / 无响应 " + silent + " 次");
                if (diff == 0)
                    Console.WriteLine("结论: 全程均为回显 —— 接收器未把请求转发给鼠标（或该集合在本固件下不承载电量协议）");
            }
            return 0;
        }
    }
}
