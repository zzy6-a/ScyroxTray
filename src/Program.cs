// Program.cs - 入口：托盘模式 + 命令行模式（--once / --json / --watch / --debug）
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScyroxTray
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        private const int ATTACH_PARENT_PROCESS = -1;

        private static Mutex _singleInstance;
        private static DateTime _startedAt;

        [STAThread]
        private static int Main(string[] args)
        {
            _startedAt = DateTime.Now;
            bool console = args.Length > 0;
            bool verbose = HasFlag(args, "--debug") || HasFlag(args, "-d");

            if (console)
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            }

            // 任何未处理异常都写进日志，避免“图标突然消失”查不到原因
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogFatal(e.ExceptionObject as Exception);
            };
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                LogFatal(e.Exception);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
            {
                PrintHelp();
                return 0;
            }

            if (HasFlag(args, "--style"))
            {
                int style = ParseIntArg(args, "--style", 0);
                Settings settings = Settings.Load();
                settings.IconStyle = Math.Max(0, Math.Min(2, style));
                settings.Save();
                Console.WriteLine("图标样式已设为 " + settings.IconStyle);
                return 0;
            }

            if (HasFlag(args, "--interval"))
            {
                int interval = ParseIntArg(args, "--interval", 5000);
                Settings settings = Settings.Load();
                settings.IntervalMs = Math.Max(1000, interval);
                settings.Save();
                Console.WriteLine("刷新间隔已设为 " + settings.IntervalMs + " ms");
                return 0;
            }

            if (HasFlag(args, "--icon-preview"))
            {
                string path = ParseStringArg(args, "--icon-preview", "icons.png");
                return IconPreview(path);
            }

            if (HasFlag(args, "--once") || HasFlag(args, "-o") || HasFlag(args, "--json") || HasFlag(args, "-j"))
            {
                bool json = HasFlag(args, "--json") || HasFlag(args, "-j");
                int timeout = ParseIntArg(args, "--timeout", 6000);
                return QueryOnce(json, timeout, verbose);
            }

            if (HasFlag(args, "--watch") || HasFlag(args, "-w"))
            {
                int seconds = ParseIntArg(args, "--watch", 5);
                return Watch(seconds, verbose);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例：重复启动只会多出一个图标并抢占同一个 HID 句柄，这里静默退出
            // （静默是为了让“看门狗 / 计划任务”可以随时拉起它而不弹窗打扰）
            bool createdNew;
            _singleInstance = new Mutex(true, "ScyroxTray.SingleInstance.v1", out createdNew);
            if (!createdNew) return 0;

            // 只驻留托盘，不弹任何窗口
            string logPath = ParseStringArg(args, "--log", null);
            using (TrayContext context = new TrayContext(true, logPath, verbose))
            {
                Application.Run(context);
            }
            return 0;
        }

        /// <summary>
        /// 未处理异常写入 %LOCALAPPDATA%\ScyroxTray\error.log，并在崩溃时自动重启自身，
        /// 避免用户看到“图标莫名其妙消失了”。
        /// </summary>
        private static void LogFatal(Exception ex)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScyroxTray");
                System.IO.Directory.CreateDirectory(dir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  " + (ex == null ? "(未知异常)" : ex.ToString())
                    + Environment.NewLine;
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"), line, Encoding.UTF8);
            }
            catch { }

            // 崩溃自愈：只在本进程已经稳定运行过 30 秒时才重启，避免启动即崩导致无限重启
            try
            {
                if (_startedAt != null && (DateTime.Now - _startedAt).TotalSeconds > 30)
                {
                    System.Diagnostics.Process.Start(Application.ExecutablePath);
                }
            }
            catch { }
        }

        private static int QueryOnce(bool json, int timeoutMs, bool verbose)
        {
            Action<string> log = delegate(string message) { Console.WriteLine(message); };
            try
            {
                BatteryReading reading = ScyroxBatteryService.QueryOnce(timeoutMs, verbose, log);
                if (json)
                {
                    Console.WriteLine(ToJson(reading));
                }
                else
                {
                    Console.WriteLine("SCYROX 鼠标电量: " + reading.DisplayPercent + "%" +
                        (reading.Charging ? "  [充电中]" : ""));
                    Console.WriteLine("  固件上报 : " + reading.FirmwarePercent + "%");
                    Console.WriteLine("  电压     : " + reading.VoltageMillivolts + " mV (曲线估算 " +
                        reading.VoltagePercent + "%)");
                    Console.WriteLine("  原始报文 : " + reading.RawHex());
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (json)
                    Console.WriteLine("{\"connected\":false,\"error\":\"" + Escape(ex.Message) + "\"}");
                else
                    Console.WriteLine("读取失败: " + ex.Message);
                return 2;
            }
        }

        private static int Watch(int seconds, bool verbose)
        {
            Action<string> log = delegate(string message) { if (verbose) Console.WriteLine(message); };
            Console.WriteLine("持续监测中（Ctrl+C 退出），间隔 " + seconds + " 秒…");
            while (true)
            {
                try
                {
                    BatteryReading reading = ScyroxBatteryService.QueryOnce(6000, verbose, log);
                    Console.WriteLine(string.Format("{0}  {1,3}%  {2}  电压 {3} mV  固件 {4}%",
                        DateTime.Now.ToString("HH:mm:ss"), reading.DisplayPercent,
                        reading.Charging ? "充电中" : "放电中", reading.VoltageMillivolts, reading.FirmwarePercent));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + ex.Message);
                }
                Thread.Sleep(Math.Max(1000, seconds * 1000));
            }
        }


        /// <summary>导出图标预览：每种样式一行，上方 6 倍放大图，下方是该图标在任务栏里的真实大小。</summary>
        private static int IconPreview(string path)
        {
            int[] levels = new int[] { 100, 88, 60, 45, 30, 15, 5 };
            int zoom = 6;
            int cell = 16 * zoom;
            int gap = 10;
            int realSize = 16;
            int rowHeight = cell + realSize + 18;
            int width = gap + levels.Length * (cell + gap);
            int height = gap + 4 * rowHeight;

            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(width, height))
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.FromArgb(255, 28, 28, 32));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                for (int style = 0; style < 3; style++)
                {
                    int top = gap + style * rowHeight;
                    for (int i = 0; i < levels.Length; i++)
                    {
                        int left = gap + i * (cell + gap);
                        using (System.Drawing.Icon icon = IconFactory.Create(style, levels[i], false, false, true))
                        using (System.Drawing.Bitmap small = icon.ToBitmap())
                        {
                            g.DrawImage(small, new System.Drawing.Rectangle(left, top, cell, cell));
                            DrawRealSize(g, small, left, top + cell + 6, cell, realSize);
                        }
                    }
                }

                // 第四行：充电中（三种样式 + 不同电量）/ 无读数
                int extraTop = gap + 3 * rowHeight;
                System.Drawing.Icon[] extras = new System.Drawing.Icon[]
                {
                    IconFactory.Create(0, 82, true, false, true),
                    IconFactory.Create(1, 82, true, false, true),
                    IconFactory.Create(2, 82, true, false, true),
                    IconFactory.Create(2, 45, true, false, true),
                    IconFactory.Create(2, 100, true, false, true),
                    IconFactory.Create(0, 0, false, true, false),
                    IconFactory.Create(2, 0, false, true, false)
                };
                for (int i = 0; i < extras.Length; i++)
                {
                    int left = gap + i * (cell + gap);
                    using (System.Drawing.Bitmap small = extras[i].ToBitmap())
                    {
                        g.DrawImage(small, new System.Drawing.Rectangle(left, extraTop, cell, cell));
                        DrawRealSize(g, small, left, extraTop + cell + 6, cell, realSize);
                    }
                    extras[i].Dispose();
                }

                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("图标预览已保存: " + path);
            return 0;
        }

        private static void DrawRealSize(System.Drawing.Graphics g, System.Drawing.Bitmap icon,
            int left, int top, int cell, int realSize)
        {
            using (System.Drawing.SolidBrush strip = new System.Drawing.SolidBrush(
                       System.Drawing.Color.FromArgb(255, 34, 34, 38)))
            {
                g.FillRectangle(strip, left, top, cell, realSize + 8);
            }
            g.DrawImage(icon, new System.Drawing.Rectangle(
                left + (cell - realSize) / 2, top + 4, realSize, realSize));
        }

        private static string ToJson(BatteryReading reading)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"connected\":true,");
            sb.Append("\"level\":").Append(reading.DisplayPercent).Append(",");
            sb.Append("\"firmware_level\":").Append(reading.FirmwarePercent).Append(",");
            sb.Append("\"voltage_level\":").Append(reading.VoltagePercent).Append(",");
            sb.Append("\"voltage_mv\":").Append(reading.VoltageMillivolts).Append(",");
            sb.Append("\"charging\":").Append(reading.Charging ? "true" : "false").Append(",");
            sb.Append("\"sampled_at\":\"").Append(reading.SampledAt.ToString("s", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"raw\":\"").Append(reading.RawHex()).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

        private static string Escape(string text)
        {
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int ParseIntArg(string[] args, string flag, int fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        int value;
                        if (int.TryParse(args[i + 1], out value)) return value;
                    }
                    return fallback;
                }
            }
            return fallback;
        }

        private static string ParseStringArg(string[] args, string flag, string fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) return args[i + 1];
                    return fallback;
                }
            }
            return fallback;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("ScyroxTray - SCYROX / Compx 8K 无线鼠标电量托盘工具");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  ScyroxTray.exe                 启动托盘（默认，不弹窗）");
            Console.WriteLine("  ScyroxTray.exe --log 文件路径  托盘模式下把协议日志写入文件");
            Console.WriteLine("  ScyroxTray.exe --once          读取一次电量（文本）");
            Console.WriteLine("  ScyroxTray.exe --json          读取一次电量（JSON）");
            Console.WriteLine("  ScyroxTray.exe --watch [秒]    持续监测输出（默认 5 秒）");
            Console.WriteLine("  ScyroxTray.exe --debug         输出完整 HID 报文日志");
            Console.WriteLine("  ScyroxTray.exe --style 0|1|2   设置图标样式（胶囊/环形/数字）");
            Console.WriteLine("  ScyroxTray.exe --interval 毫秒 设置刷新间隔（默认 300000 = 5 分钟）");
            Console.WriteLine("  ScyroxTray.exe --timeout 毫秒  单次查询超时（默认 6000）");
        }
    }
}
