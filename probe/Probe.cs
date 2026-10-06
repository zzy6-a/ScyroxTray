// Probe.cs - 一次性 HID 探测工具（不参与 ScyroxTray 主程序）
//
// 目的：V6S 换用新接收器后，先用真实报文探测每个 HID 集合，
// 确认哪个集合、哪套报文能拿到电量，再决定怎么改 BatteryService。
//
// 用法：probe.exe [VID_HEX] [PID_HEX]     默认 395A 000E
using System;
using System.Collections.Generic;
using System.Threading;

namespace ScyroxTray
{
    internal static class Probe
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
            if (args.Length >= 2)
            {
                wantVid = Convert.ToUInt16(args[0], 16);
                wantPid = Convert.ToUInt16(args[1], 16);
            }

            Console.WriteLine("=== 全部 HID 集合 ===");
            List<HidCollection> all = HidEnumerator.Enumerate();
            foreach (HidCollection c in all)
                Console.WriteLine("  " + c + "  prod='" + c.Product + "'");
            Console.WriteLine("  合计 " + all.Count + " 个集合");

            Console.WriteLine();
            Console.WriteLine("=== 目标 VID_" + wantVid.ToString("X4") + "&PID_" + wantPid.ToString("X4") + " ===");
            int hits = 0;
            foreach (HidCollection c in all)
            {
                if (c.VendorId != wantVid || c.ProductId != wantPid) continue;
                hits++;
                Console.WriteLine();
                Console.WriteLine("---- " + c);
                Console.WriteLine("     path    = " + c.DevicePath);
                Console.WriteLine("     product = " + c.Product + " | mfr = " + c.Manufacturer);

                if (c.OutputReportLength < 17 || c.InputReportLength < 17)
                {
                    Console.WriteLine("     跳过：in/out < 17（ScyroxTray 的硬条件）");
                    continue;
                }

                try
                {
                    using (HidDevice dev = new HidDevice(c.DevicePath, c.InputReportLength, c.OutputReportLength))
                    {
                        if (!dev.Open()) { Console.WriteLine("     打开失败"); continue; }

                        byte[] hs = ScyroxProtocol.BuildHandshake(new Random());
                        Console.WriteLine("     TX 0x01 握手    " + Hex(hs));
                        dev.Write(hs, 500);
                        for (int i = 0; i < 3; i++)
                        {
                            byte[] rx = dev.Read(250);
                            if (rx == null) continue;
                            Console.WriteLine("     RX             " + Hex(rx));
                            HandshakeInfo hi = ScyroxProtocol.ParseHandshake(rx);
                            if (hi != null) Console.WriteLine("       握手 OK: " + hi.ModelName() + " / " + hi.ModeName());
                        }

                        dev.Write(ScyroxProtocol.BuildDriverStatus(true), 500);
                        Console.WriteLine("     TX 0x02 驱动=连接");
                        Thread.Sleep(80);

                        byte[] on = ScyroxProtocol.BuildSimple(ScyroxCommand.DeviceOnLine);
                        Console.WriteLine("     TX 0x03 在线查询 " + Hex(on));
                        dev.Write(on, 500);
                        for (int i = 0; i < 3; i++)
                        {
                            byte[] rx = dev.Read(250);
                            if (rx == null) continue;
                            Console.WriteLine("     RX             " + Hex(rx));
                            OnlineStatus os = ScyroxProtocol.ParseOnline(rx);
                            if (os != null) Console.WriteLine("       在线=" + os.Online + " addr=" + os.AddressHex());
                        }

                        byte[] bt = ScyroxProtocol.BuildSimple(ScyroxCommand.BatteryLevel);
                        Console.WriteLine("     TX 0x04 电量查询 " + Hex(bt));
                        dev.Write(bt, 500);
                        for (int i = 0; i < 6; i++)
                        {
                            byte[] rx = dev.Read(400);
                            if (rx == null) continue;
                            Console.WriteLine("     RX             " + Hex(rx));
                            BatteryReading br = ScyroxProtocol.ParseBattery(rx);
                            if (br != null)
                                Console.WriteLine("       电量=" + br.FirmwarePercent + "%  充电=" + br.Charging + "  电压=" + br.VoltageMillivolts + "mV");
                        }

                        try { dev.Write(ScyroxProtocol.BuildDriverStatus(false), 300); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("     异常: " + ex.Message);
                }
            }
            if (hits == 0) Console.WriteLine("  (未找到该 VID/PID 的任何 HID 集合)");
            Console.WriteLine();
            Console.WriteLine("探测结束");
            return 0;
        }
    }
}
