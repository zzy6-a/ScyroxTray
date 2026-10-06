// Probe3.cs - 第三轮：区分「回显」与「真实应答」，并扫描命令空间
//
// A. 回显判定：发三条内容不同的报文（含故意错误校验），看 RX 是否原样回送
// B. 命令扫描：Report ID 8 下遍历 cmd 0x00..0x20（旧 16 字节结构补齐 64 字节）
// C. 扁平结构试探：buf[0]=08, buf[1]=cmd, 其余 0（不做 16 字节嵌套）
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe3
    {
        private static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            string[] p = new string[d.Length];
            for (int i = 0; i < d.Length; i++) p[i] = d[i].ToString("X2");
            return string.Join(" ", p);
        }

        private static byte[] ReadFor(HidDevice dev, int ms, out bool got)
        {
            got = false;
            byte[] last = null;
            DateTime e = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < e)
            {
                byte[] rx = dev.Read(100);
                if (rx != null) { got = true; last = rx; }
            }
            return last;
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
            Console.WriteLine();

            using (HidDevice dev = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!dev.Open()) { Console.WriteLine("打开失败"); return 1; }

                Console.WriteLine("[A] 回显判定");
                byte[] a1 = Nested(0x08, 0x04);
                byte[] a2 = Nested(0x08, 0x04);
                a2[15] = 0x11;                       // 故意破坏校验
                byte[] a3 = new byte[64];
                a3[0] = 0x08; a3[1] = 0x04; a3[2] = 0xAA; a3[3] = 0xBB; a3[4] = 0xCC;

                string[] labels = { "合法校验 0x04", "错误校验 0x11", "任意内容 AA BB CC" };
                byte[][] cases = { a1, a2, a3 };
                for (int i = 0; i < cases.Length; i++)
                {
                    bool ok = dev.Write(cases[i], 500);
                    bool got;
                    byte[] rx = ReadFor(dev, 260, out got);
                    Console.WriteLine("   TX[" + labels[i] + "] write=" + ok);
                    Console.WriteLine("      " + Hex(cases[i]));
                    Console.WriteLine("   RX " + (got ? Hex(rx) : "(无响应)"));
                    if (got && rx.Length >= 17)
                    {
                        bool echo = true;
                        for (int k = 0; k < 17; k++) if (rx[k] != cases[i][k]) { echo = false; break; }
                        Console.WriteLine("      判定: " + (echo ? "完全回显（接收器 loopback，非真实应答）" : "内容有差异 → 可能是真实应答"));
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("[B] 命令扫描 cmd=0x00..0x20（Report ID 8，嵌套 16 字节）");
                for (byte cmd = 0x00; cmd <= 0x20; cmd++)
                {
                    byte[] pkt = Nested(0x08, cmd);
                    bool ok = dev.Write(pkt, 400);
                    bool got;
                    byte[] rx = ReadFor(dev, 180, out got);
                    bool echo = false;
                    if (got && rx != null && rx.Length >= 17)
                    {
                        echo = true;
                        for (int k = 0; k < 17; k++) if (rx[k] != pkt[k]) { echo = false; break; }
                    }
                    string tail = "";
                    if (got && rx != null && rx.Length > 17)
                    {
                        // 尾部非零字节（回显之外的信息）
                        for (int k = 17; k < rx.Length; k++)
                            if (rx[k] != 0x00 && !(k == rx.Length - 2 && rx[k] == 0x01) && !(k == rx.Length - 1 && rx[k] == 0x29))
                            { tail += " [" + k + "]=0x" + rx[k].ToString("X2"); }
                    }
                    Console.WriteLine("   cmd=0x" + cmd.ToString("X2") + " write=" + ok + " rx=" + (got ? (echo ? "回显" : "差异") : "无") + tail);
                }
            }
            Console.WriteLine();
            Console.WriteLine("探测结束");
            return 0;
        }
    }
}
