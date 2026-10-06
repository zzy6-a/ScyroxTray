// Probe9.cs - 按 S-Center 2.0（gaming.scyrox.net）的报文格式发命令
//
// 2.0 协议（从其前端 Ac 类还原）：
//   packetSize = 64，reportId = 集合的 outputReports[0].reportId
//   generatePacket(n) = [...n, ...zeros(packetSize-2-n.length), checksum]
//     → 数据区 63 字节： [0]=cmd, [1..60]=参数, [61]=错误标志, [62]=校验
//   checkSum(n) = (reportId + sum(n)) & 0xFF        （n = 命令数组，不含补零与校验）
//   Windows HID 视角：buf[0]=reportId，buf[1..62]=数据区前 62 字节，buf[63]=校验
//
// 本工具遍历命令号，只报告「非回显」的响应（回显 = 设备原样送回主机报文）。
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe9
    {
        private static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            string[] p = new string[d.Length];
            for (int i = 0; i < d.Length; i++) p[i] = d[i].ToString("X2");
            return string.Join(" ", p);
        }

        /// <summary>按 2.0 协议构造 64 字节输出报文。</summary>
        private static byte[] BuildV2(byte rid, byte cmd, byte[] payload)
        {
            byte[] buf = new byte[64];
            buf[0] = rid;
            int plen = payload == null ? 0 : payload.Length;
            byte[] n = new byte[1 + plen];
            n[0] = cmd;
            if (payload != null) Array.Copy(payload, 0, n, 1, plen);
            Array.Copy(n, 0, buf, 1, n.Length);            // 命令放在数据区开头
            int sum = rid;
            for (int i = 0; i < n.Length; i++) sum += n[i];
            buf[63] = (byte)(sum & 0xFF);                  // 校验在最后一字节
            return buf;
        }

        private static bool IsEcho(byte[] rx, byte[] tx)
        {
            // 设备回显：前 62 字节与主机报文一致，尾部固定 01 29
            if (rx == null || rx.Length < 64) return false;
            for (int i = 0; i < 62; i++) if (rx[i] != tx[i]) return false;
            return true;
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
            Console.WriteLine("按 S-Center 2.0 格式发命令（校验在最后一字节）");
            Console.WriteLine();

            using (HidDevice dev = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!dev.Open()) { Console.WriteLine("打开失败"); return 1; }

                int echo = 0, diff = 0, silent = 0;
                for (byte cmd = 0x01; cmd <= 0x30; cmd++)
                {
                    byte[] tx = BuildV2(0x08, cmd, null);
                    if (!dev.Write(tx, 300)) { silent++; continue; }
                    byte[] last = null;
                    DateTime e = DateTime.Now.AddMilliseconds(200);
                    while (DateTime.Now < e)
                    {
                        byte[] rx = dev.Read(90);
                        if (rx != null) last = rx;
                    }
                    if (last == null) { silent++; continue; }
                    if (IsEcho(last, tx)) { echo++; continue; }

                    diff++;
                    Console.WriteLine("*** 非回显  cmd=0x" + cmd.ToString("X2"));
                    Console.WriteLine("    TX " + Hex(tx));
                    Console.WriteLine("    RX " + Hex(last));
                    Console.WriteLine();
                }
                Console.WriteLine();
                Console.WriteLine("统计: 回显 " + echo + " / 非回显 " + diff + " / 无响应 " + silent);
                if (diff == 0)
                    Console.WriteLine("结论: 2.0 格式同样只得到回显 —— 该接收器在此状态下不解析主机命令");
                else
                    Console.WriteLine("结论: 存在真实响应，可据此定命令集");
            }
            return 0;
        }
    }
}
