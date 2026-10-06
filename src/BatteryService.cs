// BatteryService.cs - 后台采集服务：发现设备 → 握手 → 在线轮询 → 电量轮询 → 状态广播
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ScyroxTray
{
    internal sealed class ScyroxState
    {
        public bool DevicePresent;
        public bool Online;
        public bool Charging;
        public int DisplayPercent;
        public int FirmwarePercent;
        public int VoltagePercent;
        public int VoltageMillivolts;
        public DateTime LastBatteryAt = DateTime.MinValue;
        public DateTime LastOnlineAt = DateTime.MinValue;
        public string DeviceName = "SCYROX";
        public string ConnectionMode = "-";
        public string DevicePath = "";
        public string Address = "";
        public string ProtocolInfo = "";
        public string Error = "";
        public long BatteryPackets;
        public long OnlinePackets;
        public long Notifications;

        public bool HasReading { get { return LastBatteryAt != DateTime.MinValue; } }

        /// <summary>最后一次成功读数的年龄（秒）。</summary>
        public double ReadingAgeSeconds
        {
            get
            {
                if (!HasReading) return double.MaxValue;
                return (DateTime.Now - LastBatteryAt).TotalSeconds;
            }
        }

        /// <summary>鼠标休眠 / 接收器离线（此时继续显示上一次读数）。</summary>
        public bool IsOffline { get { return !Online; } }

        public bool IsStale { get { return !HasReading; } }

        public ScyroxState Clone()
        {
            return (ScyroxState)MemberwiseClone();
        }
    }

    internal sealed class ScyroxBatteryService : IDisposable
    {
        public const ushort VendorId = 0x3554;
        public const ushort ProductWireless = 0xF5F7;
        public const ushort ProductWired = 0xF5F6;

        /// <summary>
        /// 单调时钟：所有“间隔/超时”都用它算，避免系统时间被校准或跳变时计时错乱
        /// （之前就出现过系统时间跳变导致“到点不刷新”的情况）。
        /// </summary>
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private const long Never = -1000000000L;      // ≈ -11.5 天的哨兵值，保证首次判定必然“到点”
        private const int RetryCooldownMs = 15000;    // 查询失败后的最小重试间隔

        private readonly object _sync = new object();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _worker;
        private ScyroxState _state = new ScyroxState();
        private HidDevice _device;
        private HidCollection _collection;
        private bool _isV2;                                   // 新接收器（V6S / Turbo 8K，VID_395A）
        private readonly PercentSmoother _smoother = new PercentSmoother();
        private volatile bool _forceRefresh;

        public int BatteryIntervalMs = 5000;
        public int OnlineIntervalMs = 1500;
        public bool SmoothPercent = true;
        public bool DebugLog = false;

        public event EventHandler StateChanged;
        public event EventHandler<string> Trace;

        public ScyroxState State
        {
            get { lock (_sync) { return _state.Clone(); } }
        }

        public void Start()
        {
            if (_worker != null) return;
            _stop.Reset();
            _worker = new Thread(Loop);
            _worker.IsBackground = true;
            _worker.Name = "ScyroxBatteryService";
            _worker.Start();
        }

        /// <summary>请求下一轮立刻查询电量（“立即刷新”菜单用）。</summary>
        public void RequestRefresh()
        {
            _forceRefresh = true;
        }

        /// <summary>把上次读数缓存注入服务状态，使鼠标休眠/刚启动时也能继续显示上次电量。</summary>
        public void SeedCache(int percent, int firmwarePercent, int voltagePercent, int voltageMv,
            bool charging, DateTime sampledAt)
        {
            lock (_sync)
            {
                _state.DisplayPercent = percent;
                _state.FirmwarePercent = firmwarePercent;
                _state.VoltagePercent = voltagePercent;
                _state.VoltageMillivolts = voltageMv;
                _state.Charging = charging;
                _state.LastBatteryAt = sampledAt;
                _state.Online = false;
            }
            _smoother.Seed(percent);
        }

        public void Stop()
        {
            _stop.Set();
            if (_worker != null)
            {
                if (!_worker.Join(2000)) { }
                _worker = null;
            }
            CloseDevice();
        }

        private void Log(string message)
        {
            EventHandler<string> handler = Trace;
            if (handler != null) handler(this, message);
        }

        private void Publish(Action<ScyroxState> mutate)
        {
            ScyroxState snapshot;
            lock (_sync)
            {
                mutate(_state);
                snapshot = _state.Clone();
            }
            EventHandler handler = StateChanged;
            if (handler != null) handler(snapshot, EventArgs.Empty);
        }

        private void Loop()
        {
            while (!_stop.WaitOne(0))
            {
                try
                {
                    if (_device == null || !_device.IsOpen)
                    {
                        if (!OpenDevice()) { _stop.WaitOne(3000); continue; }
                        if (!_isV2 && !Handshake()) { CloseDevice(); _stop.WaitOne(2000); continue; }
                    }

                    long lastOnline = Never;
                    long lastBatterySuccess = Never;      // 上一次“成功读到电量”的单调时刻
                    long lastBatteryAttempt = Never;      // 上一次“发起查询”的单调时刻
                    int onlineFailures = 0;               // 在线查询连续无应答次数
                    int ioFailures = 0;                   // 写命令连续失败次数
                    bool pendingOnline = false;
                    bool pendingBattery = false;
                    long deadline = 0;

                    while (!_stop.WaitOne(0))
                    {
                        long now = Clock.ElapsedMilliseconds;

                        if (_forceRefresh)
                        {
                            _forceRefresh = false;
                            lastBatterySuccess = Never;
                            lastBatteryAttempt = Never;
                            lastOnline = Never;
                        }

                        if (pendingOnline || pendingBattery)
                        {
                            if (now >= deadline)
                            {
                                if (pendingOnline)
                                {
                                    pendingOnline = false;
                                    onlineFailures++;
                                    Publish(delegate(ScyroxState s)
                                    {
                                        s.Online = false;
                                        s.Error = "接收器无应答（设备可能已拔出）";
                                    });
                                    // 接收器在鼠标休眠时也会应答在线查询，连续超时说明句柄已失效
                                    // （拔插过接收器 / 设备复位），必须重新枚举并重新握手，否则会一直卡住不刷新
                                    if (onlineFailures >= 3)
                                    {
                                        Log("接收器连续 " + onlineFailures + " 次无应答，重新枚举设备");
                                        CloseDevice();
                                        break;
                                    }
                                }
                                if (pendingBattery)
                                {
                                    pendingBattery = false;
                                    Publish(delegate(ScyroxState s)
                                    {
                                        s.Error = "本次电量查询无应答（继续沿用上次读数）";
                                    });
                                }
                            }
                        }

                        if (!pendingOnline && !pendingBattery)
                        {
                            bool needBattery = false;
                            lock (_sync) { needBattery = _state.Online; }

                            // 间隔从“上一次成功读数”算起：到点了但鼠标在休眠就等它醒，醒了立刻补一次。
                            // 失败后至少隔 15 秒再试，避免在鼠标醒着的瞬间反复空转。
                            bool due = (now - lastBatterySuccess) >= BatteryIntervalMs;
                            bool cooled = (now - lastBatteryAttempt) >= RetryCooldownMs;
                            if (_isV2)
                            {
                                // V2（Turbo 8K）：一条 0x2E 同时给出「连接 + 电量 + 充电」，
                                // 因此不需要单独的在线轮询；按电量间隔轮询，失败走冷却重试。
                                if (due && cooled)
                                {
                                    if (!Send(ScyroxV2.BuildStatusQuery()))
                                    {
                                        if (++ioFailures >= 3) { CloseDevice(); break; }
                                    }
                                    else ioFailures = 0;
                                    pendingBattery = true;
                                    deadline = now + 1200;
                                    lastBatteryAttempt = now;
                                    lastOnline = now;
                                }
                            }
                            else if (needBattery && due && cooled)
                            {
                                if (!Send(ScyroxProtocol.BuildSimple(ScyroxCommand.BatteryLevel)))
                                {
                                    if (++ioFailures >= 3) { CloseDevice(); break; }
                                }
                                else ioFailures = 0;
                                pendingBattery = true;
                                deadline = now + 1200;
                                lastBatteryAttempt = now;
                            }
                            else if ((now - lastOnline) >= OnlineIntervalMs)
                            {
                                if (!Send(ScyroxProtocol.BuildSimple(ScyroxCommand.DeviceOnLine)))
                                {
                                    if (++ioFailures >= 3) { CloseDevice(); break; }
                                }
                                else ioFailures = 0;
                                pendingOnline = true;
                                deadline = now + 400;
                                lastOnline = now;
                            }
                        }

                        byte[] packet = _device.Read(40);
                        if (packet != null)
                            HandlePacket(packet, ref pendingOnline, ref pendingBattery, ref lastBatterySuccess,
                                ref onlineFailures);
                    }
                }
                catch (Exception ex)
                {
                    Publish(delegate(ScyroxState s) { s.Error = ex.Message; s.Online = false; });
                    CloseDevice();
                    _stop.WaitOne(1500);
                }
            }
        }

        private void HandlePacket(byte[] packet, ref bool pendingOnline, ref bool pendingBattery,
            ref long lastBatterySuccess, ref int onlineFailures)
        {
            if (DebugLog) Log("RX " + Hex(packet));

            if (_isV2)
            {
                V2Status status = ScyroxV2.ParseStatus(packet);
                if (status == null) return;
                pendingBattery = false;
                pendingOnline = false;
                onlineFailures = 0;

                // 鼠标没连上接收器时，0x2E 会回「电量 0」—— 那不是真实读数。
                // 此时只更新在线状态，保留上一次成功读数（与 README 的承诺一致：
                // 离线/休眠期间托盘继续显示上次电量，而不是被 0 覆盖成"新读数"）。
                // 另注意：这里不能重置 lastBatterySuccess，否则要等满一个轮询间隔才会
                // 再试，鼠标刚连上也不会及时刷新。
                if (!status.Connected)
                {
                    Publish(delegate(ScyroxState s)
                    {
                        s.Online = false;
                        s.LastOnlineAt = DateTime.Now;
                        if (!s.HasReading)
                            s.Error = "鼠标未与接收器连接（开关一次鼠标或拔插接收器）";
                    });
                    if (DebugLog) Log("V2 未连接：保留上次读数");
                    return;
                }

                lastBatterySuccess = Clock.ElapsedMilliseconds;
                int display = SmoothPercent ? _smoother.Update(status.Battery) : status.Battery;
                Publish(delegate(ScyroxState s)
                {
                    s.FirmwarePercent = status.Battery;
                    s.VoltagePercent = 0;
                    s.VoltageMillivolts = 0;
                    s.Charging = status.Charging;
                    s.DisplayPercent = display;
                    s.LastBatteryAt = DateTime.Now;
                    s.Online = true;
                    s.LastOnlineAt = DateTime.Now;
                    s.BatteryPackets++;
                    s.Error = "";
                });
                if (DebugLog) Log(string.Format("V2 BATTERY {0}% (charging={1})", status.Battery, status.Charging));
                return;
            }

            if (ScyroxProtocol.IsNotification(packet))
            {
                if (ScyroxProtocol.IsBatteryChangedNotification(packet))
                {
                    Publish(delegate(ScyroxState s) { s.Notifications++; });
                    // 电量变化通知：立刻插队查询一次
                    Send(ScyroxProtocol.BuildSimple(ScyroxCommand.BatteryLevel));
                    pendingBattery = true;
                }
                return;
            }

            if (packet.Length < ScyroxProtocol.TransferLength) return;
            if (!ScyroxProtocol.IsValid(packet)) { if (DebugLog) Log("校验和错误，丢弃"); return; }

            OnlineStatus online = ScyroxProtocol.ParseOnline(packet);
            if (online != null)
            {
                pendingOnline = false;
                onlineFailures = 0;                     // 接收器有应答，健康计数清零
                Publish(delegate(ScyroxState s)
                {
                    s.Online = online.Online;
                    s.Address = online.AddressHex();
                    s.LastOnlineAt = DateTime.Now;
                    s.OnlinePackets++;
                    if (online.Online) s.Error = "";
                });
                return;
            }

            BatteryReading reading = ScyroxProtocol.ParseBattery(packet);
            if (reading != null)
            {
                pendingBattery = false;
                lastBatterySuccess = Clock.ElapsedMilliseconds;   // 间隔从成功读数开始重新计时
                int display = SmoothPercent ? _smoother.Update(reading.DisplayPercent) : reading.DisplayPercent;
                Publish(delegate(ScyroxState s)
                {
                    s.FirmwarePercent = reading.FirmwarePercent;
                    s.VoltagePercent = reading.VoltagePercent;
                    s.VoltageMillivolts = reading.VoltageMillivolts;
                    s.Charging = reading.Charging;
                    s.DisplayPercent = display;
                    s.LastBatteryAt = reading.SampledAt;
                    s.Online = true;
                    s.BatteryPackets++;
                    s.Error = "";
                });
                if (DebugLog) Log(string.Format("BATTERY {0}% ({1} mV, charging={2})", reading.DisplayPercent,
                    reading.VoltageMillivolts, reading.Charging));
                return;
            }

            if (DebugLog) Log("未知报文，忽略");
        }

        /// <summary>发送命令，返回是否写成功（失败说明句柄已失效，调用方据此重连）。</summary>
        private bool Send(byte[] packet)
        {
            if (_device == null || !_device.IsOpen) return false;
            if (DebugLog) Log("TX " + Hex(packet));
            return _device.Write(packet, 500);
        }

        private bool Handshake()
        {
            Random rng = new Random();
            if (!_device.Write(ScyroxProtocol.BuildHandshake(rng), 500)) return false;

            DateTime end = DateTime.Now.AddMilliseconds(250);
            while (DateTime.Now < end)
            {
                byte[] packet = _device.Read(60);
                if (packet == null) continue;
                if (DebugLog) Log("RX " + Hex(packet));
                HandshakeInfo info = ScyroxProtocol.ParseHandshake(packet);
                if (info != null)
                {
                    Log("握手成功: " + info.ModelName() + " / " + info.ModeName());
                    Publish(delegate(ScyroxState s)
                    {
                        s.ConnectionMode = info.ModeName();
                        s.ProtocolInfo = string.Format("CID {0:X2} · MID {1:X2}", info.CompanyId, info.ModelId);
                    });
                    break;
                }
            }

            _device.Write(ScyroxProtocol.BuildDriverStatus(true), 500);
            Drain(150);
            Log("已注册 0x02 驱动状态（已连接）");
            return true;
        }

        private void Drain(int milliseconds)
        {
            DateTime end = DateTime.Now.AddMilliseconds(milliseconds);
            while (DateTime.Now < end)
            {
                byte[] packet = _device.Read(30);
                if (packet == null) continue;
                if (DebugLog) Log("RX(drain) " + Hex(packet));
            }
        }

        private bool OpenDevice()
        {
            List<HidCollection> collections = HidEnumerator.Enumerate();
            HidCollection target = SelectTarget(collections);
            if (target == null)
            {
                Publish(delegate(ScyroxState s)
                {
                    s.DevicePresent = false;
                    s.Online = false;
                    s.DeviceName = "SCYROX";
                    s.ConnectionMode = "-";
                    s.Error = "未检测到 SCYROX 接收器（VID_3554 / PID_F5F7）";
                });
                return false;
            }

            HidDevice device = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength);
            if (!device.Open())
            {
                device.Dispose();
                Publish(delegate(ScyroxState s)
                {
                    s.DevicePresent = true;
                    s.Error = "无法打开 HID 设备（可能被官方驱动占用）";
                });
                return false;
            }

            _device = device;
            _collection = target;
            _isV2 = ScyroxV2.IsV2Device(target.VendorId, target.ProductId);
            string mode = _isV2
                ? "2.4G 无线 (Turbo 8K)"
                : (target.ProductId == ProductWired ? "有线 USB" : "2.4G 8K 无线");
            string name = string.IsNullOrEmpty(target.Product) ? "SCYROX 鼠标" : target.Product;
            Log("已连接 " + name + " [" + target + "]");
            Publish(delegate(ScyroxState s)
            {
                s.DevicePresent = true;
                s.DeviceName = name;
                s.ConnectionMode = mode;
                s.DevicePath = target.DevicePath;
                s.Error = "";
            });
            return true;
        }

        /// <summary>
        /// 选择厂商控制集合。
        ///   新接收器（V6S / Scyrox Turbo 8k dongle，VID_395A）：64 字节厂商集合，UsagePage 0xFF00；
        ///   旧接收器（V6 / V8，VID_3554）：17 字节集合，优先路径含 col05。
        /// </summary>
        private static HidCollection SelectTarget(List<HidCollection> collections)
        {
            // 新接收器优先：整台设备里只有它有 ≥17 字节的厂商集合
            foreach (HidCollection col in collections)
            {
                if (!ScyroxV2.IsV2Device(col.VendorId, col.ProductId)) continue;
                if (col.InputReportLength < 17 || col.OutputReportLength < 17) continue;
                if (col.UsagePage == 0xFF00) return col;        // 精确命中厂商通道
                if (col.InputReportLength >= 64) return col;    // 兜底：64 字节集合
            }

            HidCollection best = null;
            foreach (HidCollection col in collections)
            {
                if (col.VendorId != VendorId) continue;
                if (col.ProductId != ProductWireless && col.ProductId != ProductWired) continue;
                if (col.InputReportLength < ScyroxProtocol.TransferLength) continue;
                if (col.OutputReportLength < ScyroxProtocol.TransferLength) continue;
                bool isControl = col.DevicePath.ToLowerInvariant().Contains("col05");
                if (best == null) best = col;
                if (isControl) return col;
            }
            return best;
        }

        private void CloseDevice()
        {
            if (_device != null)
            {
                try { _device.Write(ScyroxProtocol.BuildDriverStatus(false), 300); }
                catch { }
                _device.Dispose();
                _device = null;
            }
            _collection = null;
        }

        public void Dispose()
        {
            Stop();
            _stop.Close();
        }

        internal static string Hex(byte[] data)
        {
            if (data == null) return "";
            string[] parts = new string[data.Length];
            for (int i = 0; i < data.Length; i++) parts[i] = data[i].ToString("X2");
            return string.Join(" ", parts);
        }

        /// <summary>一次性查询（CLI 用），不做平滑，返回原始读数。</summary>
        public static BatteryReading QueryOnce(int timeoutMs, bool verbose, Action<string> log)
        {
            List<HidCollection> collections = HidEnumerator.Enumerate();
            HidCollection target = SelectTarget(collections);
            if (target == null) throw new InvalidOperationException("未检测到 SCYROX 接收器（旧款 VID_3554/PID_F5F7，或新款 VID_395A/PID_000E）");
            if (verbose && log != null) log("目标集合: " + target + "  " + target.DevicePath);

            using (HidDevice device = new HidDevice(target.DevicePath, target.InputReportLength, target.OutputReportLength))
            {
                if (!device.Open()) throw new InvalidOperationException("无法打开 HID 设备");

                // 新接收器（V6S / Turbo 8K）：一条 0x2E 同时返回「连接 + 电量 + 充电」
                if (ScyroxV2.IsV2Device(target.VendorId, target.ProductId))
                {
                    DateTime endV2 = DateTime.Now.AddMilliseconds(timeoutMs);
                    DateTime lastQuery = DateTime.MinValue;
                    while (DateTime.Now < endV2)
                    {
                        if ((DateTime.Now - lastQuery).TotalMilliseconds > 300)
                        {
                            if (verbose && log != null) log("TX 0x2E 读设备状态");
                            device.Write(ScyroxV2.BuildStatusQuery(), 500);
                            lastQuery = DateTime.Now;
                        }
                        byte[] pkt = device.Read(120);
                        if (pkt == null) continue;
                        if (verbose && log != null) log("RX " + Hex(pkt));
                        V2Status st = ScyroxV2.ParseStatus(pkt);
                        if (st == null) continue;
                        if (verbose && log != null)
                            log("连接=" + st.Connected + "  电量=" + st.Battery + "%  充电=" + st.Charging);
                        if (!st.Connected)
                            throw new TimeoutException("鼠标未与接收器连接（请确认鼠标开关处于 2.4G 档并已完成对码）");
                        BatteryReading v2Reading = new BatteryReading();
                        v2Reading.FirmwarePercent = st.Battery;
                        v2Reading.Charging = st.Charging;
                        v2Reading.VoltageMillivolts = 0;
                        v2Reading.VoltagePercent = 0;
                        v2Reading.SampledAt = DateTime.Now;
                        v2Reading.RawPacket = pkt;
                        return v2Reading;
                    }
                    throw new TimeoutException("接收器未返回设备状态（稍后重试）");
                }

                Random rng = new Random();
                device.Write(ScyroxProtocol.BuildHandshake(rng), 500);
                if (verbose && log != null) log("TX 0x01 握手");
                DateTime handshakeEnd = DateTime.Now.AddMilliseconds(250);
                while (DateTime.Now < handshakeEnd)
                {
                    byte[] reply = device.Read(60);
                    if (reply == null) continue;
                    if (verbose && log != null) log("RX " + Hex(reply));
                    HandshakeInfo info = ScyroxProtocol.ParseHandshake(reply);
                    if (info != null && log != null)
                        log("握手成功: " + info.ModelName() + " / " + info.ModeName());
                }
                device.Write(ScyroxProtocol.BuildDriverStatus(true), 500);
                if (verbose && log != null) log("TX 0x02 驱动状态=已连接");
                System.Threading.Thread.Sleep(60);

                DateTime end = DateTime.Now.AddMilliseconds(timeoutMs);
                bool online = false;
                BatteryReading last = null;
                DateTime lastBatteryRequest = DateTime.MinValue;
                while (DateTime.Now < end)
                {
                    DateTime now = DateTime.Now;
                    if (!online && (now - lastBatteryRequest).TotalMilliseconds > 300)
                    {
                        device.Write(ScyroxProtocol.BuildSimple(ScyroxCommand.DeviceOnLine), 400);
                        lastBatteryRequest = now;
                    }
                    else if (online && (now - lastBatteryRequest).TotalMilliseconds > 400)
                    {
                        device.Write(ScyroxProtocol.BuildSimple(ScyroxCommand.BatteryLevel), 400);
                        lastBatteryRequest = now;
                    }

                    byte[] packet = device.Read(80);
                    if (packet == null) continue;
                    if (verbose && log != null) log("RX " + Hex(packet));
                    OnlineStatus status = ScyroxProtocol.ParseOnline(packet);
                    if (status != null)
                    {
                        online = status.Online;
                        if (verbose && log != null)
                            log("在线状态: " + (online ? "在线" : "休眠/离线") + "  地址 " + status.AddressHex());
                        if (online) lastBatteryRequest = DateTime.MinValue;
                        continue;
                    }
                    BatteryReading reading = ScyroxProtocol.ParseBattery(packet);
                    if (reading != null)
                    {
                        last = reading;
                        break;
                    }
                }
                device.Write(ScyroxProtocol.BuildDriverStatus(false), 300);
                if (last == null)
                    throw new TimeoutException(online ? "鼠标已在线但未返回电量（稍后重试）" : "鼠标处于休眠/离线状态，晃动鼠标后重试");
                return last;
            }
        }
    }
}
