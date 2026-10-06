// Probe12.cs - 发命令 + 归类所有输入报告，定位 report id 14（电量推送）
//
// 依据 S-Center 2.0（3950 / next chunk）：
//   Bt() 初始化序列 = send([1]) → [32] → [42] → [43] → [54] → [49]
//   电量只在 input 处理器里被更新：report id 14 → t[3] 为电量
// 因此需要「发命令激活 + 同时监听」，本工具逐个命令发送并归类全部输入报告。
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe12
    {
        private static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            string[] p = new string[d.Length];
            for (int i = 0; i < d.Length; i++) p[i] = d[i].ToString("X2");
            return string.Join(" ", p);
        }

        private static byte[] Build(byte rid, byte[] cmd)
        {
            byte[] buf = new byte[64];
            buf[0] = rid;
            Array.Copy(cmd, 0, buf, 1, Math.Min(cmd.Length, 62));
            int sum = rid;
            for (int i = 0; i < cmd.Length; i++) sum += cmd[i];
            buf[63] = (byte)(sum & 0xFF);
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

            Dictionary<int, int> counts = new Dictionary<int, int>();
            Dictionary<int, string> samples = new Dictionary<int, string>();

            using (HidDevice dev = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!dev.Open()) { Console.WriteLine("打开失败"); return 1; }

                byte[][] cmds = new byte[][]
                {
                    new byte[] { 1 },            // 读 profile（官方 qi）
                    new byte[] { 46, 0, 3 },     // 设备状态（含 battery 字段）
                    new byte[] { 32 },           // 官方 on()
                    new byte[] { 42 },           // 官方 cn() 连接状态
                    new byte[] { 43 },           // 官方 un()
                    new byte[] { 54 },           // 官方 an() 充电灯效
                    new byte[] { 49 },           // 官方 dn() 接收器
                };
                string[] names = { "[1] profile", "[46,0,3] 状态", "[32]", "[42] 连接", "[43]", "[54]", "[49]" };

                for (int k = 0; k < cmds.Length; k++)
                {
                    byte[] tx = Build(0x08, cmds[k]);
                    bool ok = dev.Write(tx, 500);
                    Console.WriteLine("---- 发送 " + names[k] + "  write=" + ok);
                    DateTime e = DateTime.Now.AddMilliseconds(700);
                    while (DateTime.Now < e)
                    {
                        byte[] rx = dev.Read(100);
                        if (rx == null || rx.Length == 0) continue;
                        int rid = rx[0];
                        if (!counts.ContainsKey(rid)) { counts[rid] = 0; samples[rid] = Hex(rx); }
                        counts[rid]++;
                    }
                }

                // 静默再听 3 秒，看是否有周期性推送
                Console.WriteLine();
                Console.WriteLine("---- 静默监听 3 秒");
                DateTime end = DateTime.Now.AddSeconds(3);
                while (DateTime.Now < end)
                {
                    byte[] rx = dev.Read(120);
                    if (rx == null || rx.Length == 0) continue;
                    int rid = rx[0];
                    if (!counts.ContainsKey(rid)) { counts[rid] = 0; samples[rid] = Hex(rx); }
                    counts[rid]++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("==== 收到的输入报告（按 Report ID） ====");
            if (counts.Count == 0) Console.WriteLine("  （无）");
            foreach (KeyValuePair<int, int> kv in counts)
            {
                Console.WriteLine("  Report ID " + kv.Key + " (0x" + kv.Key.ToString("X2") + ") : " + kv.Value + " 条");
                Console.WriteLine("      样本: " + samples[kv.Key]);
            }
            return 0;
        }
    }
}
