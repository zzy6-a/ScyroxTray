// Probe10.cs - 验证 S-Center 2.0 的真实命令：0x2E 读设备状态（含电量）
//
// 依据 gaming.scyrox.net 前端：
//   F.send([46,0,3]) → Re(resp) → { battery: resp[3], isCharging: resp[4]==1,
//                                    isConnected: resp[5]==1, sn: resp[15..27] }
//   F.send([24])     → 版本高/低字节
//   F.send([33])     → 版本高/低字节
// 报文格式：data(63B) = [cmd, 参数..., 0×补零, checksum]，checksum = (reportId + sum(命令数组)) & 0xFF
// Windows HID 视角：buf[0]=reportId, buf[1..]=data
using System;
using System.Collections.Generic;

namespace ScyroxTray
{
    internal static class Probe10
    {
        private static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            string[] p = new string[d.Length];
            for (int i = 0; i < d.Length; i++) p[i] = d[i].ToString("X2");
            return string.Join(" ", p);
        }

        private static byte[] BuildV2(byte rid, byte[] cmd)
        {
            byte[] buf = new byte[64];
            buf[0] = rid;
            Array.Copy(cmd, 0, buf, 1, cmd.Length);
            int sum = rid;
            for (int i = 0; i < cmd.Length; i++) sum += cmd[i];
            buf[63] = (byte)(sum & 0xFF);
            return buf;
        }

        private static byte[] Send(HidDevice dev, byte rid, byte[] cmd, string label)
        {
            byte[] tx = BuildV2(rid, cmd);
            bool ok = dev.Write(tx, 500);
            byte[] last = null;
            DateTime e = DateTime.Now.AddMilliseconds(350);
            while (DateTime.Now < e)
            {
                byte[] rx = dev.Read(120);
                if (rx != null) last = rx;
            }
            Console.WriteLine("---- " + label + "  cmd=[" + string.Join(",", Array.ConvertAll(cmd, b => b.ToString())) + "] write=" + ok);
            Console.WriteLine("     TX " + Hex(tx));
            Console.WriteLine("     RX " + (last == null ? "(无响应)" : Hex(last)));
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
            Console.WriteLine();

            using (HidDevice dev = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!dev.Open()) { Console.WriteLine("打开失败"); return 1; }
                byte rid = 0x08;

                byte[] st = Send(dev, rid, new byte[] { 46, 0, 3 }, "0x2E 读设备状态（电量）");
                if (st != null && st.Length >= 28)
                {
                    int battery = st[4];                    // data[3] → RX[4]
                    bool charging = st[5] == 1;             // data[4]
                    bool connected = st[6] == 1;            // data[5]
                    char[] snChars = new char[13];
                    for (int i = 0; i < 13; i++) snChars[i] = (char)st[16 + i];
                    Console.WriteLine("     >>> 电量=" + battery + "%  充电=" + charging + "  已连接=" + connected);
                    Console.WriteLine("     >>> SN=\"" + new string(snChars).TrimEnd('\0') + "\"");
                    Console.WriteLine("     >>> data[6..14] = " + st[7] + "," + st[8] + "," + st[9] + "," + st[10] + "," + st[11] + "," + st[12] + "," + st[13] + "," + st[14] + "," + st[15]);
                }
                Console.WriteLine();

                byte[] v1 = Send(dev, rid, new byte[] { 24 }, "0x18 读版本");
                if (v1 != null && v1.Length >= 6) Console.WriteLine("     >>> ver = " + v1[4] + "." + v1[5]);
                Console.WriteLine();
                byte[] v2 = Send(dev, rid, new byte[] { 33 }, "0x21 读版本");
                if (v2 != null && v2.Length >= 6) Console.WriteLine("     >>> ver = " + v2[4] + "." + v2[5]);
                Console.WriteLine();

                // 再读一次状态，确认稳定性
                Send(dev, rid, new byte[] { 46, 0, 3 }, "0x2E 复读");
            }
            Console.WriteLine("验证结束");
            return 0;
        }
    }
}
