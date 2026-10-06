// TrayApp.cs - 托盘 UI：动态电量图标（3 种样式）、Fluent 风格右键菜单、屏幕右下角 OSD 悬浮窗
// 交互设计对齐 rapoo-tray：左键单击 → OSD；左键双击 → 切换图标样式；右键 → 控制面板
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScyroxTray
{
    internal sealed class Settings
    {
        private const string KeyPath = @"Software\ScyroxTray";

        public int IconStyle = 0;              // 0 胶囊 / 1 环形 / 2 数字
        public int IntervalMs = 600000;        // 默认 10 分钟
        public bool Autostart = false;
        public bool Smooth = true;
        public bool LowBatteryAlert = true;
        public int LowBatteryThreshold = 15;

        // 上次读数缓存：鼠标休眠时继续显示（跨重启保留）
        public int CachedPercent = -1;
        public int CachedFirmwarePercent = -1;
        public int CachedVoltagePercent = -1;
        public int CachedVoltageMv = -1;
        public bool CachedCharging = false;
        public long CachedSampledTicks = 0;

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key == null) return s;
                    s.IconStyle = ReadInt(key, "IconStyle", s.IconStyle);
                    s.IntervalMs = ReadInt(key, "IntervalMs", s.IntervalMs);
                    s.Autostart = ReadInt(key, "Autostart", 0) == 1;
                    s.Smooth = ReadInt(key, "Smooth", 1) == 1;
                    s.LowBatteryAlert = ReadInt(key, "LowBatteryAlert", 1) == 1;
                    s.LowBatteryThreshold = ReadInt(key, "LowBatteryThreshold", s.LowBatteryThreshold);
                    s.CachedPercent = ReadInt(key, "CachedPercent", -1);
                    s.CachedFirmwarePercent = ReadInt(key, "CachedFirmwarePercent", -1);
                    s.CachedVoltagePercent = ReadInt(key, "CachedVoltagePercent", -1);
                    s.CachedVoltageMv = ReadInt(key, "CachedVoltageMv", -1);
                    s.CachedCharging = ReadInt(key, "CachedCharging", 0) == 1;
                    object ticks = key.GetValue("CachedSampledTicks");
                    if (ticks != null)
                    {
                        try { s.CachedSampledTicks = Convert.ToInt64(ticks); }
                        catch { s.CachedSampledTicks = 0; }
                    }
                }
            }
            catch { }
            return s;
        }

        private static int ReadInt(RegistryKey key, string name, int fallback)
        {
            object value = key.GetValue(name);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value); }
            catch { return fallback; }
        }

        public void Save()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null) return;
                    key.SetValue("IconStyle", IconStyle, RegistryValueKind.DWord);
                    key.SetValue("IntervalMs", IntervalMs, RegistryValueKind.DWord);
                    key.SetValue("Autostart", Autostart ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("Smooth", Smooth ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("LowBatteryAlert", LowBatteryAlert ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("LowBatteryThreshold", LowBatteryThreshold, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        /// <summary>写入/更新上次读数缓存（鼠标休眠时托盘继续显示这个值）。</summary>
        public void SaveReading(int percent, int firmwarePercent, int voltagePercent, int voltageMv,
            bool charging, DateTime sampledAt)
        {
            CachedPercent = percent;
            CachedFirmwarePercent = firmwarePercent;
            CachedVoltagePercent = voltagePercent;
            CachedVoltageMv = voltageMv;
            CachedCharging = charging;
            CachedSampledTicks = sampledAt.Ticks;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null) return;
                    key.SetValue("CachedPercent", CachedPercent, RegistryValueKind.DWord);
                    key.SetValue("CachedFirmwarePercent", CachedFirmwarePercent, RegistryValueKind.DWord);
                    key.SetValue("CachedVoltagePercent", CachedVoltagePercent, RegistryValueKind.DWord);
                    key.SetValue("CachedVoltageMv", CachedVoltageMv, RegistryValueKind.DWord);
                    key.SetValue("CachedCharging", CachedCharging ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("CachedSampledTicks", CachedSampledTicks.ToString(), RegistryValueKind.String);
                }
            }
            catch { }
        }

        /// <summary>用缓存构造初始状态，使程序启动时（鼠标还在休眠）也能立刻显示上次电量。</summary>
        public ScyroxState ToCachedState()
        {
            ScyroxState state = new ScyroxState();
            if (CachedPercent < 0 || CachedSampledTicks <= 0) return state;
            state.DisplayPercent = CachedPercent;
            state.FirmwarePercent = CachedFirmwarePercent < 0 ? CachedPercent : CachedFirmwarePercent;
            state.VoltagePercent = CachedVoltagePercent < 0 ? CachedPercent : CachedVoltagePercent;
            state.VoltageMillivolts = CachedVoltageMv < 0 ? 0 : CachedVoltageMv;
            state.Charging = CachedCharging;
            try { state.LastBatteryAt = new DateTime(CachedSampledTicks); }
            catch { state.LastBatteryAt = DateTime.MinValue; }
            state.Online = false;
            return state;
        }
    }

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ScyroxTray";

        public static void Apply(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        string exe = Application.ExecutablePath;
                        key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 托盘图标绘制：先在 4 倍画布上用抗锯齿绘制，再高质量缩放回 16px，
    /// 这样细笔画与数字边缘都不会出现锯齿/彩色描边（16px 直接画必然发毛）。
    /// </summary>
    internal static class IconFactory
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private const int Supersample = 4;

        /// <summary>画在白色填充之上的闪电必须用深色。</summary>
        private static readonly Color BoltColor = Color.FromArgb(255, 22, 22, 26);

        public static Icon Create(int style, int percent, bool charging, bool offline, bool hasReading)
        {
            Size size = SystemInformation.SmallIconSize;
            int w = Math.Max(16, size.Width);
            int h = Math.Max(16, size.Height);
            Color color = hasReading ? Color.FromArgb(255, 255, 255, 255)
                                     : Color.FromArgb(112, 255, 255, 255);

            using (Bitmap big = new Bitmap(w * Supersample, h * Supersample,
                       System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.Clear(Color.Transparent);
                    float W = big.Width;
                    float H = big.Height;
                    switch (style)
                    {
                        case 1: DrawRing(g, W, H, percent, color, charging, hasReading); break;
                        case 2: DrawDigits(g, W, H, percent, color, charging, hasReading); break;
                        default: DrawCapsule(g, W, H, percent, color, charging, hasReading); break;
                    }
                }

                using (Bitmap small = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.Clear(Color.Transparent);
                        g.DrawImage(big, new Rectangle(0, 0, w, h));
                    }
                    IntPtr handle = small.GetHicon();
                    try
                    {
                        using (Icon temp = Icon.FromHandle(handle))
                        {
                            return (Icon)temp.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(handle);
                    }
                }
            }
        }

        /// <summary>样式一：横向电池胶囊 + 按比例填充 + 充电闪电。</summary>
        private static void DrawCapsule(Graphics g, float W, float H, int percent, Color color,
            bool charging, bool hasReading)
        {
            float stroke = H * 0.085f;
            float nubWidth = W * 0.095f;
            RectangleF body = new RectangleF(W * 0.055f, H * 0.285f,
                W * 0.845f - nubWidth - W * 0.045f, H * 0.43f);
            float radius = body.Height * 0.42f;

            using (SolidBrush track = new SolidBrush(Color.FromArgb(48, color)))
            using (GraphicsPath path = Rounded(body, radius))
            {
                g.FillPath(track, path);
            }
            using (Pen pen = new Pen(color, stroke))
            using (GraphicsPath path = Rounded(body, radius))
            {
                g.DrawPath(pen, path);
            }

            if (hasReading && percent > 0)
            {
                float inset = stroke * 1.15f;
                RectangleF inner = RectangleF.Inflate(body, -inset, -inset);
                float ratio = Math.Max(0f, Math.Min(1f, percent / 100f));
                float fillWidth = Math.Max(inner.Height * 0.9f, inner.Width * ratio);
                RectangleF fill = new RectangleF(inner.X, inner.Y, fillWidth, inner.Height);
                using (SolidBrush brush = new SolidBrush(color))
                using (GraphicsPath path = Rounded(fill, Math.Max(1f, inner.Height * 0.36f)))
                {
                    g.FillPath(brush, path);
                }
            }

            RectangleF nub = new RectangleF(body.Right + W * 0.028f, H * 0.405f, nubWidth, H * 0.19f);
            using (SolidBrush brush = new SolidBrush(color))
            using (GraphicsPath path = Rounded(nub, nub.Height * 0.45f))
            {
                g.FillPath(brush, path);
            }

            if (charging)
            {
                float boltHeight = body.Height * 1.15f;
                RectangleF bolt = new RectangleF(body.X + body.Width / 2f - boltHeight * 0.32f,
                    body.Y + body.Height / 2f - boltHeight / 2f, boltHeight * 0.64f, boltHeight);
                DrawBolt(g, bolt, BoltColor);
            }
        }

        /// <summary>样式二：环形进度（16px 下塞数字会糊，所以环内不放数字，充电时放闪电）。</summary>
        private static void DrawRing(Graphics g, float W, float H, int percent, Color color,
            bool charging, bool hasReading)
        {
            float stroke = H * 0.105f;
            RectangleF rect = new RectangleF(stroke * 0.55f, stroke * 0.55f,
                W - stroke * 1.1f, H - stroke * 1.1f);

            using (Pen track = new Pen(Color.FromArgb(52, color), stroke))
            {
                g.DrawEllipse(track, rect);
            }
            if (hasReading)
            {
                float sweep = 360f * Math.Max(0f, Math.Min(1f, percent / 100f));
                using (Pen pen = new Pen(color, stroke))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, rect, -90f, Math.Max(sweep, 1.5f));
                }
            }
            if (charging)
            {
                float boltHeight = H * 0.42f;
                RectangleF bolt = new RectangleF(W / 2f - boltHeight * 0.32f, H / 2f - boltHeight / 2f,
                    boltHeight * 0.64f, boltHeight);
                DrawBolt(g, bolt, Color.FromArgb(255, 255, 255, 255));
            }
        }

        /// <summary>样式三：大号数字 + 底部进度条；充电时数字右移，右侧显示闪电。</summary>
        private static void DrawDigits(Graphics g, float W, float H, int percent, Color color,
            bool charging, bool hasReading)
        {
            // 底部进度条：轨道半透明，填充为白色圆角条
            float barHeight = H * 0.135f;
            RectangleF bar = new RectangleF(W * 0.07f, H * 0.80f, W * 0.86f, barHeight);
            float barRadius = barHeight / 2f;
            using (SolidBrush track = new SolidBrush(Color.FromArgb(58, color)))
            using (GraphicsPath path = Rounded(bar, barRadius))
            {
                g.FillPath(track, path);
            }
            if (hasReading && percent > 0)
            {
                float ratio = Math.Max(0f, Math.Min(1f, percent / 100f));
                float fillWidth = Math.Max(barHeight, bar.Width * ratio);
                RectangleF fill = new RectangleF(bar.X, bar.Y, fillWidth, bar.Height);
                using (SolidBrush brush = new SolidBrush(color))
                using (GraphicsPath path = Rounded(fill, barRadius))
                {
                    g.FillPath(brush, path);
                }
            }

            bool showBolt = charging && hasReading;
            string text = hasReading ? percent.ToString() : "--";

            // 有闪电时数字整体左移并略微缩小，给闪电腾位置（三位数要再缩一点）
            float textAreaWidth = showBolt ? W * 0.70f : W;
            float maxTextWidth = showBolt ? (text.Length >= 3 ? W * 0.62f : W * 0.66f) : W * 0.94f;
            float fontSize = text.Length >= 3 ? (showBolt ? H * 0.40f : H * 0.46f)
                                              : (text.Length == 2 ? H * 0.62f : H * 0.70f);
            DrawCenteredText(g, text, color, fontSize, maxTextWidth,
                new RectangleF(0, -H * 0.035f, textAreaWidth, H * 0.78f), false);

            if (showBolt)
            {
                float boltHeight = text.Length >= 3 ? H * 0.38f : H * 0.46f;
                RectangleF bolt = new RectangleF(W * 0.725f, H * 0.125f, boltHeight * 0.60f, boltHeight);
                DrawBolt(g, bolt, color);
            }
        }

        private static void DrawCenteredText(Graphics g, string text, Color color, float fontSize,
            float maxWidth, RectangleF box, bool narrow)
        {
            string family = narrow ? "Segoe UI" : "Segoe UI";
            while (fontSize > 4f)
            {
                using (Font probe = new Font(family, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF measured = g.MeasureString(text, probe, PointF.Empty, StringFormat.GenericTypographic);
                    if (measured.Width <= maxWidth) break;
                }
                fontSize -= 0.6f;
            }

            using (Font font = new Font(family, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            using (SolidBrush brush = new SolidBrush(color))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                SizeF measured = g.MeasureString(text, font, PointF.Empty, format);
                // 数字字形在行框里偏上，这里做一次光学垂直居中
                float offsetY = -measured.Height * 0.035f;
                g.DrawString(text, font, brush, new RectangleF(box.X, box.Y + offsetY, box.Width, box.Height), format);
            }
        }

        private static void DrawBolt(Graphics g, RectangleF rect, Color color)
        {
            PointF[] points = new PointF[]
            {
                new PointF(rect.X + rect.Width * 0.62f, rect.Y),
                new PointF(rect.X, rect.Y + rect.Height * 0.56f),
                new PointF(rect.X + rect.Width * 0.44f, rect.Y + rect.Height * 0.56f),
                new PointF(rect.X + rect.Width * 0.34f, rect.Y + rect.Height),
                new PointF(rect.X + rect.Width, rect.Y + rect.Height * 0.42f),
                new PointF(rect.X + rect.Width * 0.54f, rect.Y + rect.Height * 0.42f)
            };
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPolygon(brush, points);
            }
        }

        private static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (d <= 0.2f)
            {
                path.AddRectangle(rect);
                path.CloseFigure();
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>
    /// 监听 shell 的 TaskbarCreated 广播：Explorer 崩溃/重启后系统会清空托盘图标，
    /// 只有收到这条广播并重新注册，图标才会回来（否则进程还活着，图标却永远消失）。
    /// </summary>
    internal sealed class TaskbarCreatedWatcher : NativeWindow, IDisposable
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);

        private readonly uint _taskbarCreated;
        private readonly NotifyIcon _icon;
        private readonly Action _onShellRestart;

        public TaskbarCreatedWatcher(NotifyIcon icon, Action onShellRestart)
        {
            _icon = icon;
            _onShellRestart = onShellRestart;
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (_taskbarCreated != 0 && (uint)m.Msg == _taskbarCreated)
            {
                try
                {
                    _icon.Visible = false;      // 摘掉残留注册再重新挂上
                    _icon.Visible = true;
                    if (_onShellRestart != null) _onShellRestart();
                }
                catch { }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try { DestroyHandle(); }
            catch { }
        }
    }

    /// <summary>暗色菜单渲染器（Fluent 观感）。</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(236, 236, 240) : Color.FromArgb(120, 120, 126);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(200, 200, 208);
            base.OnRenderArrow(e);
        }

        /// <summary>自绘勾选标记：默认勾是深色的，在暗色菜单里根本看不见。</summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle area = e.ImageRectangle;
            if (area.Width <= 0 || area.Height <= 0)
                area = new Rectangle(4, 0, 16, e.Item.Height);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = area.Width;
            float h = area.Height;
            using (Pen pen = new Pen(Color.FromArgb(255, 255, 255, 255), Math.Max(1.6f, h * 0.11f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                PointF[] tick = new PointF[]
                {
                    new PointF(area.Left + w * 0.20f, area.Top + h * 0.52f),
                    new PointF(area.Left + w * 0.42f, area.Top + h * 0.72f),
                    new PointF(area.Left + w * 0.82f, area.Top + h * 0.28f)
                };
                g.DrawLines(pen, tick);
            }
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(38, 38, 42); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(38, 38, 42); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(38, 38, 42); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(38, 38, 42); } }
        public override Color MenuBorder { get { return Color.FromArgb(70, 70, 76); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(0, 168, 255); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(58, 58, 64); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(58, 58, 64); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(58, 58, 64); } }
        public override Color SeparatorDark { get { return Color.FromArgb(62, 62, 68); } }
        public override Color SeparatorLight { get { return Color.FromArgb(62, 62, 68); } }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ScyroxBatteryService _service;
        private readonly Settings _settings;
        private ScyroxState _state = new ScyroxState();
        private Icon _currentIcon;
        private bool _alerted;
        private readonly List<string> _log = new List<string>();
        private readonly Control _marshal = new Control();
        private TaskbarCreatedWatcher _taskbarWatcher;
        private string _logPath;

        private ToolStripMenuItem _statusItem;
        private ToolStripMenuItem _styleCapsule;
        private ToolStripMenuItem _styleRing;
        private ToolStripMenuItem _styleNumber;
        private ToolStripMenuItem _autostartItem;
        private ToolStripMenuItem _smoothItem;
        private ToolStripMenuItem _alertItem;
        private ToolStripMenuItem _intervalMenu;
        private ToolStripMenuItem _alertMenu;

        public TrayContext(bool startHidden, string logPath, bool verbose)
        {
            _settings = Settings.Load();
            _logPath = logPath;
            IntPtr forceHandle = _marshal.Handle;      // 建立 UI 线程消息泵，用于跨线程回调
            _service = new ScyroxBatteryService();
            _service.BatteryIntervalMs = _settings.IntervalMs;
            _service.SmoothPercent = _settings.Smooth;
            _service.DebugLog = verbose;
            _service.StateChanged += OnStateChanged;
            _service.Trace += delegate(object sender, string message) { Record(message); };

            _menu = BuildMenu();
            _notifyIcon = new NotifyIcon();
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "SCYROX 鼠标电量";
            _notifyIcon.MouseClick += OnMouseClick;
            _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;

            // Explorer 重启后自动把图标挂回来（否则进程还在、图标却没了）
            _taskbarWatcher = new TaskbarCreatedWatcher(_notifyIcon, delegate
            {
                Record("检测到 Explorer 重启，已重新注册托盘图标");
                try { UpdateIcon(_state); }
                catch { }
            });

            // 用上次读数缓存预热状态：鼠标还在休眠时托盘直接显示上次电量
            _state = _settings.ToCachedState();
            if (_state.HasReading)
            {
                Record("载入上次读数缓存 " + _state.DisplayPercent + "%");
                _service.SeedCache(_state.DisplayPercent, _state.FirmwarePercent, _state.VoltagePercent,
                    _state.VoltageMillivolts, _state.Charging, _state.LastBatteryAt);
            }
            UpdateIcon(_state);
            UpdateStatusItem(_state);

            Autostart.Apply(_settings.Autostart);
            _service.Start();
        }

        private void Record(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
            lock (_log)
            {
                _log.Add(line);
                if (_log.Count > 400) _log.RemoveAt(0);
            }
            if (!string.IsNullOrEmpty(_logPath))
            {
                try
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;          // 保留勾选列，否则勾选状态根本不显示
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.BackColor = Color.FromArgb(38, 38, 42);

            _statusItem = new ToolStripMenuItem("正在连接…");
            _statusItem.Enabled = false;
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新（双击图标同效）");
            refresh.Click += delegate { RefreshNow(); };
            menu.Items.Add(refresh);

            ToolStripMenuItem styleMenu = new ToolStripMenuItem("图标样式");
            _styleCapsule = new ToolStripMenuItem("胶囊电池", null, delegate { SetStyle(0); });
            _styleRing = new ToolStripMenuItem("环形电量", null, delegate { SetStyle(1); });
            _styleNumber = new ToolStripMenuItem("大号数字", null, delegate { SetStyle(2); });
            styleMenu.DropDownItems.Add(_styleCapsule);
            styleMenu.DropDownItems.Add(_styleRing);
            styleMenu.DropDownItems.Add(_styleNumber);
            menu.Items.Add(styleMenu);

            _intervalMenu = new ToolStripMenuItem("刷新间隔");
            int[] intervals = new int[] { 300000, 600000, 1800000, 3600000 };
            foreach (int interval in intervals)
            {
                int captured = interval;
                ToolStripMenuItem item = new ToolStripMenuItem((interval / 60000) + " 分钟", null,
                    delegate { SetInterval(captured); });
                item.Tag = interval;
                _intervalMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(_intervalMenu);

            _smoothItem = new ToolStripMenuItem("电量平滑（每 10 秒最多 1%）", null, delegate
            {
                _settings.Smooth = !_settings.Smooth;
                _settings.Save();
                _service.SmoothPercent = _settings.Smooth;
                SyncChecks();
            });
            menu.Items.Add(_smoothItem);

            _alertMenu = new ToolStripMenuItem("低电量提醒");
            _alertItem = new ToolStripMenuItem("启用提醒", null, delegate
            {
                _settings.LowBatteryAlert = !_settings.LowBatteryAlert;
                _settings.Save();
                SyncChecks();
            });
            _alertMenu.DropDownItems.Add(_alertItem);
            _alertMenu.DropDownItems.Add(new ToolStripSeparator());
            int[] thresholds = new int[] { 10, 15, 20, 30 };
            foreach (int threshold in thresholds)
            {
                int captured = threshold;
                ToolStripMenuItem item = new ToolStripMenuItem("阈值 " + threshold + "%", null, delegate
                {
                    _settings.LowBatteryThreshold = captured;
                    _settings.Save();
                    _alerted = false;
                    SyncChecks();
                });
                item.Tag = threshold;
                _alertMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(_alertMenu);

            _autostartItem = new ToolStripMenuItem("开机自启", null, delegate
            {
                _settings.Autostart = !_settings.Autostart;
                _settings.Save();
                Autostart.Apply(_settings.Autostart);
                SyncChecks();
            });
            menu.Items.Add(_autostartItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem copy = new ToolStripMenuItem("复制诊断信息", null, delegate { CopyDiagnostics(); });
            menu.Items.Add(copy);

            ToolStripMenuItem about = new ToolStripMenuItem("关于", null, delegate { ShowAbout(); });
            menu.Items.Add(about);

            ToolStripMenuItem exit = new ToolStripMenuItem("退出", null, delegate { ExitApp(); });
            menu.Items.Add(exit);

            SyncChecks();
            return menu;
        }

        private void SyncChecks()
        {
            _styleCapsule.Checked = _settings.IconStyle == 0;
            _styleRing.Checked = _settings.IconStyle == 1;
            _styleNumber.Checked = _settings.IconStyle == 2;
            _smoothItem.Checked = _settings.Smooth;
            _alertItem.Checked = _settings.LowBatteryAlert;
            _autostartItem.Checked = _settings.Autostart;
            if (_intervalMenu != null)
            {
                foreach (ToolStripItem item in _intervalMenu.DropDownItems)
                {
                    ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                    if (menuItem != null && menuItem.Tag != null)
                        menuItem.Checked = Convert.ToInt32(menuItem.Tag) == _settings.IntervalMs;
                }
            }
            if (_alertMenu != null)
            {
                foreach (ToolStripItem item in _alertMenu.DropDownItems)
                {
                    ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                    if (menuItem != null && menuItem.Tag != null)
                        menuItem.Checked = Convert.ToInt32(menuItem.Tag) == _settings.LowBatteryThreshold;
                }
            }
        }

        private void SetStyle(int style)
        {
            _settings.IconStyle = style;
            _settings.Save();
            SyncChecks();
            UpdateIcon(_state);
        }

        private void SetInterval(int intervalMs)
        {
            _settings.IntervalMs = intervalMs;
            _settings.Save();
            _service.BatteryIntervalMs = intervalMs;
            SyncChecks();
            RefreshNow();
        }

        private void RefreshNow()
        {
            Record("手动触发刷新");
            _service.RequestRefresh();
            _notifyIcon.Text = "SCYROX 鼠标电量 · 正在刷新…";
        }

        private void OnMouseClick(object sender, MouseEventArgs e)
        {
            // 左键单击不做任何事（不弹窗）；刷新请双击
        }

        private void OnMouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) RefreshNow();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            ScyroxState state = sender as ScyroxState;
            if (state == null) return;
            if (_marshal.InvokeRequired)
            {
                _marshal.BeginInvoke((MethodInvoker)delegate { ApplyState(state); });
                return;
            }
            ApplyState(state);
        }

        private void ApplyState(ScyroxState state)
        {
            bool newReading = state.HasReading &&
                (_state == null || state.LastBatteryAt != _state.LastBatteryAt);
            _state = state;
            if (newReading) _settings.SaveReading(state.DisplayPercent, state.FirmwarePercent,
                state.VoltagePercent, state.VoltageMillivolts, state.Charging, state.LastBatteryAt);
            if (_notifyIcon != null && _notifyIcon.Visible)
            {
                try
                {
                    UpdateIcon(state);
                }
                catch { }
            }
            UpdateStatusItem(state);
            CheckLowBattery(state);
        }

        private void UpdateStatusItem(ScyroxState state)
        {
            if (_statusItem == null) return;
            if (!state.HasReading)
                _statusItem.Text = "SCYROX 鼠标电量";
            else
                _statusItem.Text = string.Format("{0}  {1}%{2}", state.DeviceName, state.DisplayPercent,
                    state.Charging ? "  ⚡ 充电中" : "");
        }

        private void CheckLowBattery(ScyroxState state)
        {
            if (!_settings.LowBatteryAlert || state == null || !state.HasReading) return;
            if (!state.Online) return;                    // 只对实时读数告警，避免休眠/启动时误报
            if (state.Charging || state.DisplayPercent > _settings.LowBatteryThreshold)
            {
                if (state.DisplayPercent > _settings.LowBatteryThreshold + 5 || state.Charging) _alerted = false;
                return;
            }
            if (_alerted) return;
            _alerted = true;
            _notifyIcon.ShowBalloonTip(4000, "SCYROX 电量偏低",
                string.Format("当前电量 {0}%，建议充电。", state.DisplayPercent), ToolTipIcon.Warning);
        }

        /// <summary>
        /// 重建托盘图标与提示文本。
        ///
        /// 顺序很关键：必须「先装上新图标 → 再设文本 → 最后释放旧图标」。
        /// 旧实现是先 Dispose 再 return、由调用方赋值，于是存在一个窗口：
        /// 旧图标已释放、而 _notifyIcon.Icon 仍指向它。窗口期内任何触碰
        /// （设文本 / 双击刷新 / 切样式）都会让 NotifyIcon 内部读 Icon.Handle 时
        /// 抛 ObjectDisposedException，图标从此不再更新 —— 表现为"切样式没反应"。
        /// </summary>
        private void UpdateIcon(ScyroxState state)
        {
            if (_notifyIcon == null) return;

            bool hasReading = state != null && state.HasReading;
            bool fresh = state != null && state.Online;
            int percent = hasReading ? state.DisplayPercent : 0;
            bool charging = state != null && state.Charging && fresh;
            Icon icon = IconFactory.Create(_settings.IconStyle, percent, charging, !hasReading, hasReading);

            string tooltip;
            if (!hasReading) tooltip = "SCYROX 鼠标电量";
            else if (charging) tooltip = string.Format("SCYROX {0}% · 充电中", percent);
            else tooltip = string.Format("SCYROX {0}%", percent);
            if (tooltip.Length > 62) tooltip = tooltip.Substring(0, 62);

            Icon previous = _currentIcon;
            _currentIcon = icon;
            _notifyIcon.Icon = icon;        // ① 先换图标
            _notifyIcon.Text = tooltip;     // ② 再设文本（此时已指向新图标）
            if (previous != null && !ReferenceEquals(previous, icon))
                previous.Dispose();         // ③ 最后释放旧图标
        }

        private void CopyDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            ScyroxState s = _state;
            sb.AppendLine("ScyroxTray 诊断信息 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("设备: " + (s == null ? "-" : s.DeviceName) + " / " + (s == null ? "-" : s.ConnectionMode));
            sb.AppendLine("路径: " + (s == null ? "-" : s.DevicePath));
            sb.AppendLine("地址: " + (s == null ? "-" : s.Address));
            sb.AppendLine("电量: " + (s == null ? "-" : s.DisplayPercent.ToString()) + "% (固件 " +
                (s == null ? "-" : s.FirmwarePercent.ToString()) + "% / 电压估算 " +
                (s == null ? "-" : s.VoltagePercent.ToString()) + "%)");
            sb.AppendLine("电压: " + (s == null ? "-" : s.VoltageMillivolts.ToString()) + " mV  充电: " +
                (s == null ? "-" : s.Charging.ToString()));
            sb.AppendLine("在线: " + (s == null ? "-" : s.Online.ToString()) + "  报文: 在线 " +
                (s == null ? "0" : s.OnlinePackets.ToString()) + " / 电量 " +
                (s == null ? "0" : s.BatteryPackets.ToString()) + " / 通知 " +
                (s == null ? "0" : s.Notifications.ToString()));
            if (s != null && !string.IsNullOrEmpty(s.Error)) sb.AppendLine("错误: " + s.Error);
            sb.AppendLine();
            sb.AppendLine("--- 协议日志 ---");
            lock (_log)
            {
                foreach (string line in _log) sb.AppendLine(line);
            }
            try
            {
                Clipboard.SetText(sb.ToString());
                _notifyIcon.ShowBalloonTip(2500, "已复制", "诊断信息已复制到剪贴板。", ToolTipIcon.Info);
            }
            catch { }
        }

        private void ShowAbout()
        {
            string text = "ScyroxTray v1.1\n\n"
                + "SCYROX / Compx 8K 无线鼠标电量托盘工具。\n"
                + "思路参考 rapoo-tray（HID 集合枚举 + 厂商报文解析 + 托盘图标），\n"
                + "协议按 SCYROX 官方 S-Center 报文（Report ID 8）实现。\n\n"
                + "鼠标悬停图标：查看电量\n"
                + "双击图标：立即刷新\n"
                + "右键图标：控制面板";
            MessageBox.Show(text, "关于 ScyroxTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExitApp()
        {
            // 防误触：退出前确认一次（托盘图标一旦退出就只剩任务管理器能拉回来）
            DialogResult answer = MessageBox.Show(
                "确定退出 ScyroxTray？\n\n退出后任务栏不再显示鼠标电量。\n（已勾选“开机自启”，下次开机仍会自动启动）",
                "退出 ScyroxTray", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            try
            {
                _service.Stop();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch { }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _service.Dispose(); } catch { }
                if (_currentIcon != null) _currentIcon.Dispose();
                if (_taskbarWatcher != null) _taskbarWatcher.Dispose();
                _marshal.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
