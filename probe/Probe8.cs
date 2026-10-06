// Probe8.cs - 按官方 S-Center Web 的完整序列执行，逐步打印响应
//
// 协议（从 www.scyrox.net 前端还原）：
//   报文 16 字节： [0]=cmd [1]=0 [2]=0 [3]=0 [4]=payloadLen [5..14]=payload [15]=checksum
//   checksum = (0x55 - sum(n[0..14]) - 8) & 0xFF        （8 = reportId）
//   发送时前置 Report ID 8（Windows HID 视角），故设备响应亦含 reportId 在 [0]
//   解析：We = 响应去掉 [0] 后的视图 → We[0]=cmd, We[1]=status(0=成功)
//         DeviceOnLine: We[5]=online, We[6..8]=addr
//         BatteryLevel: We[5]=level, We[6]=charging, We[7..8]=voltage
//         EncryptionData: We[9]=cid, We[10]=mid, We[11]=type
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe8
    {
        private static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            string[] p = new string[d.Length];
            for (int i = 0; i < d.Length; i++) p[i] = d[i].ToString("X2");
            return string.Join(" ", p);
        }

        private static byte[] BuildCmd(byte cmd, byte[] payload)
        {
            byte[] buf = new byte[64];
            buf[0] = 0x08;
            byte[] n = new byte[16];
            n[0] = cmd;
            int plen = payload == null ? 0 : payload.Length;
            n[4] = (byte)plen;
            if (payload != null) Array.Copy(payload, 0, n, 5, Math.Min(plen, 10));
            int sum = 0;
            for (int i = 0; i < 15; i++) sum += n[i];
            n[15] = (byte)((0x55 - (sum & 0xFF) - 8) & 0xFF);
            Array.Copy(n, 0, buf, 1, 16);
            return buf;
        }

        private static byte[] Transact(HidDevice dev, string label, byte cmd, byte[] payload)
        {
            byte[] tx = BuildCmd(cmd, payload);
            bool ok = dev.Write(tx, 500);
            byte[] last = null;
            DateTime e = DateTime.Now.AddMilliseconds(320);
            while (DateTime.Now < e)
            {
                byte[] rx = dev.Read(120);
                if (rx != null) last = rx;
            }
            Console.WriteLine("---- " + label + "  (cmd=0x" + cmd.ToString("X2") + ") write=" + ok);
            Console.WriteLine("     TX " + Hex(tx));
            if (last == null) { Console.WriteLine("     RX (无响应)"); return null; }
            Console.WriteLine("     RX " + Hex(last));
            if (last.Length >= 16)
            {
                byte c = last[1], st = last[2];
                string extra = "";
                switch (c)
                {
                    case 0x01: extra = "cid=" + last[10] + " mid=" + last[11] + " type=" + last[12]; break;
                    case 0x03: extra = "online=" + last[6] + " addr=" + last[7].ToString("X2") + last[8].ToString("X2") + last[9].ToString("X2"); break;
                    case 0x04: extra = "level=" + last[6] + "% charging=" + last[7] + " voltage=" + ((last[8] << 8) | last[9]) + "mV"; break;
                    case 0x06: extra = "pairState=" + last[6] + " b7=" + last[7] + " b8=" + last[8] + " b9=" + last[9]; break;
                }
                Console.WriteLine("     解析: cmd=0x" + c.ToString("X2") + " status=" + st + "  " + extra);
            }
            Console.WriteLine();
            return last;
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
            Console.WriteLine("（按官方 S-Center Web 的序列执行）");
            Console.WriteLine();

            using (HidDevice dev = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!dev.Open()) { Console.WriteLine("打开失败"); return 1; }

                // 1. 握手：4 个随机字节
                Random rng = new Random();
                byte[] token = new byte[4];
                rng.NextBytes(token);
                Transact(dev, "0x01 握手 EncryptionData", 0x01, token);

                // 2. 驱动状态 = 已连接
                Transact(dev, "0x02 驱动状态 PCDriverStatus(1)", 0x02, new byte[] { 0x01 });

                // 3. 在线查询
                Transact(dev, "0x03 在线查询 DeviceOnLine", 0x03, null);

                // 4. 配对状态
                Transact(dev, "0x06 配对状态 GetPairState", 0x06, null);

                // 5. 官方初始化链
                Transact(dev, "0x0E 读当前配置 GetCurrentConfig", 0x0E, null);
                Transact(dev, "0x12 读版本 ReadVersionID", 0x12, null);
                Transact(dev, "0x1D dongle 版本 GetDongleVersion", 0x1D, null);

                // 6. 电量
                Transact(dev, "0x04 电量 BatteryLevel", 0x04, null);

                // 7. 再确认一次在线（官方每 1.5s 轮询）
                System.Threading.Thread.Sleep(300);
                Transact(dev, "0x03 在线复查", 0x03, null);

                Transact(dev, "0x02 驱动状态 PCDriverStatus(0) 收尾", 0x02, new byte[] { 0x00 });
            }
            Console.WriteLine("诊断结束");
            return 0;
        }
    }
}
