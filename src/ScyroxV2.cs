// ScyroxV2.cs - 新一代接收器（V6S / "Scyrox Turbo 8k dongle"）协议实现
//
// 协议来源：S-Center 2.0 网页版驱动（gaming.scyrox.net/assets/index-*.js）逆向。
// 与旧协议（VID_3554 / 16 字节报文）完全不同：
//
//   设备   : VID_395A，PID 000E（2.4G 8K）/ 000B / 000C
//   集合   : UsagePage=0xFF00 Usage=0x0002，in=64 out=64，唯一 outputReport.reportId = 8
//   报文   : 64 字节 = [reportId, 命令..., 补零, checksum]
//            checksum = (reportId + sum(命令数组)) & 0xFF，放在最后一字节
//   读状态 : 命令 [46, 0, 3]（0x2E）
//            响应 data（去掉 reportId 后）= [0x2E, ?, ?, 电量%, 充电, 已连接, ...]
//            即含 reportId 视角： rx[4]=电量, rx[5]=充电(1/0), rx[6]=已连接(1/0)
//   读版本 : 命令 [24] 或 [33]
using System;

namespace ScyroxTray
{
    /// <summary>V2 设备状态（一条 0x2E 命令同时给出连接与电量）。</summary>
    internal sealed class V2Status
    {
        public int Battery;        // 0-100
        public bool Charging;
        public bool Connected;
        public byte[] Raw;

        public V2Status Clone()
        {
            return (V2Status)MemberwiseClone();
        }
    }

    internal static class ScyroxV2
    {
        public const ushort VendorId = 0x395A;
        public static readonly ushort[] ProductIds = new ushort[] { 0x000E, 0x000B, 0x000C };

        public const byte ReportId = 0x08;
        public const int PacketSize = 64;
        public const byte CmdDeviceStatus = 46;   // 0x2E：电量 + 充电 + 连接
        public const byte CmdVersion = 24;        // 0x18
        public const byte CmdVersionAlt = 33;     // 0x21

        public static bool IsV2Device(ushort vendorId, ushort productId)
        {
            if (vendorId != VendorId) return false;
            for (int i = 0; i < ProductIds.Length; i++)
                if (ProductIds[i] == productId) return true;
            return false;
        }

        /// <summary>按 V2 格式构造 64 字节输出报文。</summary>
        public static byte[] Build(byte[] command)
        {
            byte[] buffer = new byte[PacketSize];
            buffer[0] = ReportId;
            int count = Math.Min(command.Length, PacketSize - 2);
            Array.Copy(command, 0, buffer, 1, count);
            int sum = ReportId;
            for (int i = 0; i < command.Length; i++) sum += command[i];
            buffer[PacketSize - 1] = (byte)(sum & 0xFF);
            return buffer;
        }

        public static byte[] BuildStatusQuery()
        {
            return Build(new byte[] { CmdDeviceStatus, 0x00, 0x03 });
        }

        public static byte[] BuildVersionQuery()
        {
            return Build(new byte[] { CmdVersion });
        }

        /// <summary>校验：(reportId + sum(前 63 字节)) & 0xFF == 末字节。</summary>
        public static bool IsValid(byte[] packet)
        {
            if (packet == null || packet.Length < PacketSize) return false;
            if (packet[0] != ReportId) return false;
            int sum = 0;
            for (int i = 0; i < PacketSize - 1; i++) sum += packet[i];
            return (sum & 0xFF) == packet[PacketSize - 1];
        }

        public static V2Status ParseStatus(byte[] packet)
        {
            if (!IsValid(packet)) return null;
            if (packet[1] != CmdDeviceStatus) return null;
            V2Status status = new V2Status();
            status.Battery = packet[4];
            status.Charging = packet[5] == 1;
            status.Connected = packet[6] == 1;
            status.Raw = packet;
            return status;
        }
    }
}
