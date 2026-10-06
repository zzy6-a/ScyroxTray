// ScyroxProtocol.cs - SCYROX / Compx 8K 无线鼠标 HID 协议（Report ID 8，16 字节报文）
//
// 报文结构（不含 Report ID 时的偏移，主机 → 设备）：
//   0      Command ID
//   1      Reserved
//   2-3    Flash 地址高/低字节
//   4      Data Length
//   5-14   Data Payload
//   15     Checksum = (0x55 - sum(bytes[0..14]) - ReportId) & 0xFF
//
// 设备 → 主机：0 = Command ID 回显，1 = 状态(0=成功/1=错误)，2-3 = 地址，4 = 长度，5-14 = 数据，15 = 校验
// 实际写句柄时首字节补 Report ID(0x08)，因此缓冲区下标整体 +1：
//   response[0]=0x08  response[1]=命令  response[2]=状态
//   BatteryLevel: response[6]=电量%  response[7]=充电(1/0)  response[8..9]=电压 mV(大端)
//   DeviceOnLine: response[6]=在线(1/0)  response[7..9]=设备地址
using System;

namespace ScyroxTray
{
    internal enum ScyroxCommand : byte
    {
        EncryptionData = 0x01,
        PcDriverStatus = 0x02,
        DeviceOnLine = 0x03,
        BatteryLevel = 0x04,
        DongleEnterPair = 0x05,
        GetPairState = 0x06,
        StatusChanged = 0x0A,
        GetCurrentConfig = 0x0E,
        SetCurrentConfig = 0x0F,
        ReadVersionId = 0x12,
        GetDongleVersion = 0x1D
    }

    internal sealed class BatteryReading
    {
        public int FirmwarePercent;      // 固件自报电量（response[6]）
        public int VoltageMillivolts;    // response[8]<<8 | response[9]
        public bool Charging;            // response[7] == 1
        public int VoltagePercent;       // 电压曲线估算
        public DateTime SampledAt;
        public byte[] RawPacket;

        public int DisplayPercent
        {
            get
            {
                if (FirmwarePercent > 0 && FirmwarePercent <= 100) return FirmwarePercent;
                return VoltagePercent;
            }
        }

        public string RawHex()
        {
            if (RawPacket == null) return "";
            string[] parts = new string[RawPacket.Length];
            for (int i = 0; i < RawPacket.Length; i++) parts[i] = RawPacket[i].ToString("X2");
            return string.Join(" ", parts);
        }
    }

    internal sealed class OnlineStatus
    {
        public bool Online;
        public byte AddressHigh;
        public byte AddressMiddle;
        public byte AddressLow;
        public byte[] RawPacket;

        public string AddressHex()
        {
            return string.Format("{0:X2}{1:X2}{2:X2}", AddressHigh, AddressMiddle, AddressLow);
        }
    }

    internal sealed class HandshakeInfo
    {
        public byte CompanyId;
        public byte ModelId;
        public byte ConnectionType;
        public byte[] TokenEcho;
        public byte[] RawPacket;

        /// <summary>0=无线 1000Hz，1=无线 4K，2=有线，3=有线 8K，4=无线 2K，5=无线 8K。</summary>
        public string ModeName()
        {
            switch (ConnectionType)
            {
                case 0: return "2.4G 无线 (1K)";
                case 1: return "2.4G 无线 (4K)";
                case 2: return "有线 USB";
                case 3: return "有线 USB (8K)";
                case 4: return "2.4G 无线 (2K)";
                case 5: return "2.4G 无线 (8K)";
                default: return "未知模式 (" + ConnectionType + ")";
            }
        }

        public string ModelName()
        {
            return string.Format("SCYROX (CID {0:X2} / MID {1:X2})", CompanyId, ModelId);
        }
    }

    internal static class ScyroxProtocol
    {
        public const byte ReportId = 8;
        public const int PacketLength = 16;
        public const int TransferLength = PacketLength + 1;   // 含 Report ID
        public const byte ChecksumBase = 0x55;

        /// <summary>S-Center 原厂电压-电量表（mV），用于兜底估算。</summary>
        private static readonly int[] VendorVoltageCurve = new int[]
        {
            3050, 3420, 3480, 3540, 3600, 3660, 3720, 3760, 3800, 3840, 3880,
            3920, 3940, 3960, 3980, 4000, 4020, 4040, 4060, 4080, 4110
        };

        /// <summary>实测拟合的放电曲线（mV → 剩余可用时长百分比），比原厂表更贴近真实续航。</summary>
        private static readonly int[][] DischargeTable = new int[][]
        {
            new int[] { 3089, 0 },   new int[] { 3200, 1 },   new int[] { 3357, 5 },
            new int[] { 3394, 7 },   new int[] { 3524, 18 },  new int[] { 3549, 21 },
            new int[] { 3582, 26 },  new int[] { 3604, 30 },  new int[] { 3649, 35 },
            new int[] { 3705, 44 },  new int[] { 3758, 51 },  new int[] { 3846, 58 },
            new int[] { 3875, 61 },  new int[] { 3909, 66 },  new int[] { 3997, 74 },
            new int[] { 4056, 81 },  new int[] { 4073, 84 },  new int[] { 4080, 86 },
            new int[] { 4088, 90 },  new int[] { 4106, 94 },  new int[] { 4129, 97 },
            new int[] { 4145, 98 },  new int[] { 4152, 99 },  new int[] { 4193, 100 }
        };

        public static byte CalculateChecksum(byte[] packet, int length)
        {
            int sum = 0;
            for (int i = 0; i < length - 1; i++) sum += packet[i];
            return (byte)((ChecksumBase - (sum & 0xFF) - ReportId) & 0xFF);
        }

        public static byte[] BuildSimple(ScyroxCommand command)
        {
            return Build(command, null, 0, 0, 0);
        }

        public static byte[] BuildWithData(ScyroxCommand command, byte dataLength, byte[] payload)
        {
            return Build(command, payload, 0, 0, dataLength);
        }

        /// <summary>0x01 握手：4 个随机字节 + data length 8。</summary>
        public static byte[] BuildHandshake(Random rng)
        {
            byte[] token = new byte[4];
            rng.NextBytes(token);
            return Build(ScyroxCommand.EncryptionData, token, 0, 0, 8);
        }

        /// <summary>0x02 驱动状态：payload[0] = 1(连接) / 0(断开)。</summary>
        public static byte[] BuildDriverStatus(bool connected)
        {
            return Build(ScyroxCommand.PcDriverStatus, new byte[] { connected ? (byte)1 : (byte)0 }, 0, 0, 1);
        }

        private static byte[] Build(ScyroxCommand command, byte[] payload, byte addressHigh, byte addressLow, byte dataLength)
        {
            byte[] packet = new byte[PacketLength];
            packet[0] = (byte)command;
            packet[1] = 0x00;
            packet[2] = addressHigh;
            packet[3] = addressLow;
            packet[4] = dataLength;
            if (payload != null)
            {
                int count = Math.Min(payload.Length, 10);
                Array.Copy(payload, 0, packet, 5, count);
            }
            packet[15] = CalculateChecksum(packet, PacketLength);
            byte[] transfer = new byte[TransferLength];
            transfer[0] = ReportId;
            Array.Copy(packet, 0, transfer, 1, PacketLength);
            return transfer;
        }

        public static bool IsValid(byte[] packet)
        {
            if (packet == null || packet.Length < TransferLength) return false;
            if (packet[0] != ReportId) return false;
            int sum = 0;
            for (int i = 0; i < packet.Length; i++) sum += packet[i];
            return (sum & 0xFF) == ChecksumBase;
        }

        /// <summary>0x01 握手应答：回显 token + CID + MID + 连接类型。</summary>
        public static HandshakeInfo ParseHandshake(byte[] packet)
        {
            if (packet == null || packet.Length < TransferLength) return null;
            if (packet[1] != (byte)ScyroxCommand.EncryptionData) return null;
            HandshakeInfo info = new HandshakeInfo();
            info.TokenEcho = new byte[] { packet[6], packet[7], packet[8], packet[9] };
            info.CompanyId = packet[10];
            info.ModelId = packet[11];
            info.ConnectionType = packet[12];
            info.RawPacket = packet;
            return info;
        }

        public static OnlineStatus ParseOnline(byte[] packet)        {
            if (packet == null || packet.Length < TransferLength) return null;
            if (packet[1] != (byte)ScyroxCommand.DeviceOnLine) return null;
            OnlineStatus status = new OnlineStatus();
            status.Online = packet[6] == 0x01;
            status.AddressHigh = packet[7];
            status.AddressMiddle = packet[8];
            status.AddressLow = packet[9];
            status.RawPacket = packet;
            return status;
        }

        public static BatteryReading ParseBattery(byte[] packet)
        {
            if (packet == null || packet.Length < TransferLength) return null;
            if (packet[1] != (byte)ScyroxCommand.BatteryLevel) return null;
            if (packet[2] != 0x00) return null;      // 非 0 视为设备报错
            BatteryReading reading = new BatteryReading();
            reading.FirmwarePercent = packet[6];
            reading.Charging = packet[7] == 0x01;
            reading.VoltageMillivolts = (packet[8] << 8) | packet[9];
            reading.VoltagePercent = VoltageToPercent(reading.VoltageMillivolts, reading.Charging);
            reading.SampledAt = DateTime.Now;
            reading.RawPacket = packet;
            return reading;
        }

        /// <summary>0x0A 通知：flags 的 bit6(0x40) 表示电量变化，需要立刻重读。</summary>
        public static bool IsBatteryChangedNotification(byte[] packet)
        {
            if (packet == null || packet.Length < TransferLength) return false;
            if (packet[1] != (byte)ScyroxCommand.StatusChanged) return false;
            return (packet[6] & 0x40) != 0;
        }

        public static bool IsNotification(byte[] packet)
        {
            return packet != null && packet.Length >= 2 && packet[1] == (byte)ScyroxCommand.StatusChanged;
        }

        /// <summary>电压 → 百分比：优先用实测放电曲线，越界时回落到原厂表。</summary>
        public static int VoltageToPercent(int millivolts, bool charging)
        {
            if (millivolts <= 0) return 0;
            double value = Interpolate(DischargeTable, millivolts);
            if (value <= 0)
            {
                int vendor = VendorCurvePercent(millivolts, charging);
                if (vendor > value) value = vendor;
            }
            int percent = (int)Math.Round(value);
            if (charging && percent > 99 && millivolts < 4200) percent = 99;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            return percent;
        }

        private static double Interpolate(int[][] table, int millivolts)
        {
            if (millivolts <= table[0][0]) return table[0][1];
            if (millivolts >= table[table.Length - 1][0]) return table[table.Length - 1][1];
            for (int i = 1; i < table.Length; i++)
            {
                if (millivolts <= table[i][0])
                {
                    int v0 = table[i - 1][0], p0 = table[i - 1][1];
                    int v1 = table[i][0], p1 = table[i][1];
                    double ratio = (double)(millivolts - v0) / (v1 - v0);
                    return p0 + ratio * (p1 - p0);
                }
            }
            return table[table.Length - 1][1];
        }

        /// <summary>S-Center 风格：相邻阈值间再切 5 段线性插值。</summary>
        private static int VendorCurvePercent(int millivolts, bool charging)
        {
            int last = VendorVoltageCurve[VendorVoltageCurve.Length - 1];
            if (millivolts > last) return charging ? 99 : 100;
            int index = -1;
            for (int i = 0; i < VendorVoltageCurve.Length; i++)
            {
                if (millivolts < VendorVoltageCurve[i]) { index = i; break; }
            }
            if (index <= 0) return index == 0 ? 1 : 0;
            int lower = VendorVoltageCurve[index - 1];
            int upper = VendorVoltageCurve[index];
            double segment = (upper - lower) / 5.0;
            double level = (millivolts - lower) / segment + (index - 1) * 5;
            int rounded = (int)Math.Floor(level + 0.5);
            if (rounded == 0 || rounded == 15) rounded++;
            return Math.Max(0, Math.Min(100, rounded));
        }
    }

    /// <summary>显示值平滑：每 10 秒最多变化 1 个百分点（对齐原厂工具的观感）。</summary>
    internal sealed class PercentSmoother
    {
        private int _display = -1;
        private int _target = -1;
        private DateTime _lastStep = DateTime.MinValue;
        private const double StepSeconds = 10.0;
        private const double ResetSeconds = 1800.0;

        public int Display { get { return _display < 0 ? 0 : _display; } }

        /// <summary>用缓存值作为平滑起点（避免启动时从 0 开始爬升）。</summary>
        public void Seed(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            _display = percent;
            _target = percent;
            _lastStep = DateTime.Now;
        }

        public int Update(int target)
        {
            if (target < 0) target = 0;
            if (target > 100) target = 100;
            DateTime now = DateTime.Now;
            if (_display < 0)
            {
                _display = target;
                _target = target;
                _lastStep = now;
                return _display;
            }
            _target = target;
            double elapsed = (now - _lastStep).TotalSeconds;
            if (elapsed < 0 || elapsed > ResetSeconds)
            {
                _display = target;
                _lastStep = now;
                return _display;
            }
            if (Math.Abs(target - _display) > 10)
            {
                _display = target;
                _lastStep = now;
                return _display;
            }
            int steps = (int)Math.Floor(elapsed / StepSeconds);
            if (steps > 0)
            {
                int diff = target - _display;
                int move = Math.Min(Math.Abs(diff), steps);
                if (move > 0) _display += Math.Sign(diff) * move;
                _lastStep = _lastStep.AddSeconds(steps * StepSeconds);
            }
            return _display;
        }
    }
}
