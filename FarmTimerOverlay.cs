using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using IOPath = System.IO.Path;

namespace FarmTimerOverlay
{
    public sealed class FarmOverlayApp : Application
    {
        private static Mutex singleInstanceMutex;
        private TimerController timer;
        private OverlaySettings settings;
        private FarmOverlayWindow overlay;
        private FarmControlWindow control;
        private WinForms.NotifyIcon trayIcon;
        private WinForms.ToolStripMenuItem customTrayItem;
        private WinForms.ToolStripMenuItem runTrayItem;
        private bool exiting;

        [STAThread]
        public static void Main()
        {
            bool createdNew;
            singleInstanceMutex = new Mutex(true, "Local\\FarmTimerOverlay.SingleInstance", out createdNew);
            if (!createdNew)
            {
                singleInstanceMutex.Dispose();
                return;
            }

            FarmOverlayApp app = new FarmOverlayApp();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Startup += app.OnAppStartup;
            app.Exit += app.OnAppExit;
            app.Run();

            GC.KeepAlive(singleInstanceMutex);
            singleInstanceMutex.Dispose();
        }

        private void OnAppStartup(object sender, StartupEventArgs e)
        {
            settings = OverlaySettings.Load();
            settings.Save();
            timer = new TimerController(settings.FarmSeconds);
            overlay = new FarmOverlayWindow(timer, settings);
            control = new FarmControlWindow(timer, settings, overlay, ExitApplication);

            overlay.CustomModeChanged += delegate
            {
                settings.CustomMode = overlay.CustomMode;
                settings.Save();
                control.SyncOverlaySettings();
                UpdateTrayState();
            };
            overlay.OpacityChangedByApp += delegate
            {
                settings.Opacity = overlay.Opacity;
                settings.Save();
                control.SyncOverlaySettings();
            };
            timer.Changed += delegate
            {
                UpdateTrayState();
            };

            overlay.Show();
            control.Show();
            CreateTrayIcon();
            timer.Start();
        }

        private void CreateTrayIcon()
        {
            trayIcon = new WinForms.NotifyIcon();
            trayIcon.Text = "Farm Timer Overlay";
            try
            {
                string processPath = Process.GetCurrentProcess().MainModule.FileName;
                trayIcon.Icon = Drawing.Icon.ExtractAssociatedIcon(processPath);
            }
            catch
            {
                trayIcon.Icon = Drawing.SystemIcons.Information;
            }
            trayIcon.Visible = true;

            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
            menu.Font = new Drawing.Font("Segoe UI", 9.0f);

            WinForms.ToolStripMenuItem openItem = new WinForms.ToolStripMenuItem("Open control panel");
            openItem.Click += delegate { Dispatcher.BeginInvoke(new Action(ShowControlWindow)); };
            menu.Items.Add(openItem);

            customTrayItem = new WinForms.ToolStripMenuItem("Custom overlay");
            customTrayItem.Click += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    overlay.SetCustomMode(!overlay.CustomMode);
                }));
            };
            menu.Items.Add(customTrayItem);

            runTrayItem = new WinForms.ToolStripMenuItem("Stop");
            runTrayItem.Click += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (timer.IsRunning)
                    {
                        timer.Stop();
                    }
                    else
                    {
                        timer.Start();
                    }
                }));
            };
            menu.Items.Add(runTrayItem);

            WinForms.ToolStripMenuItem resetItem = new WinForms.ToolStripMenuItem("Reset");
            resetItem.Click += delegate { Dispatcher.BeginInvoke(new Action(timer.Reset)); };
            menu.Items.Add(resetItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            WinForms.ToolStripMenuItem exitItem = new WinForms.ToolStripMenuItem("Exit");
            exitItem.Click += delegate { Dispatcher.BeginInvoke(new Action(ExitApplication)); };
            menu.Items.Add(exitItem);

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowControlWindow)); };
            UpdateTrayState();
        }

        private void ShowControlWindow()
        {
            if (!control.IsVisible)
            {
                control.Show();
            }
            if (control.WindowState == WindowState.Minimized)
            {
                control.WindowState = WindowState.Normal;
            }
            control.Activate();
            control.Topmost = true;
            control.Topmost = false;
        }

        private void UpdateTrayState()
        {
            if (customTrayItem != null)
            {
                customTrayItem.Checked = overlay.CustomMode;
                customTrayItem.Text = overlay.CustomMode ? "Custom overlay: ON" : "Custom overlay: OFF";
            }
            if (runTrayItem != null)
            {
                runTrayItem.Text = timer.IsRunning ? "Stop timer" : "Start timer";
            }
        }

        private void ExitApplication()
        {
            if (exiting)
            {
                return;
            }
            exiting = true;
            settings.Save();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (control != null)
            {
                control.ForceClose();
            }
            if (overlay != null)
            {
                overlay.Close();
            }
            Shutdown();
        }

        private void OnAppExit(object sender, ExitEventArgs e)
        {
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
        }
    }

    public sealed class TimerController
    {
        public const double CycleSeconds = 120.0;
        private readonly Stopwatch watch = new Stopwatch();
        private readonly DispatcherTimer uiTimer;
        private double elapsedBeforeRun;
        private double farmSeconds;

        public event EventHandler Changed;

        public TimerController(double initialFarmSeconds)
        {
            farmSeconds = ClampFarmSeconds(initialFarmSeconds);
            uiTimer = new DispatcherTimer(DispatcherPriority.Render);
            uiTimer.Interval = TimeSpan.FromMilliseconds(80);
            uiTimer.Tick += delegate { RaiseChanged(); };
            uiTimer.Start();
        }

        public bool IsRunning
        {
            get { return watch.IsRunning; }
        }

        public double FarmSeconds
        {
            get { return farmSeconds; }
        }

        public double LootSeconds
        {
            get { return CycleSeconds - farmSeconds; }
        }

        public double TotalElapsed
        {
            get { return elapsedBeforeRun + (watch.IsRunning ? watch.Elapsed.TotalSeconds : 0.0); }
        }

        public double CycleElapsed
        {
            get { return Math.Max(0.0, TotalElapsed) % CycleSeconds; }
        }

        public bool IsFarmPhase
        {
            get { return CycleElapsed < farmSeconds; }
        }

        public long CycleIndex
        {
            get { return (long)Math.Floor(Math.Max(0.0, TotalElapsed) / CycleSeconds) + 1; }
        }

        public void Start()
        {
            if (watch.IsRunning)
            {
                return;
            }
            watch.Restart();
            RaiseChanged();
        }

        public void Stop()
        {
            if (!watch.IsRunning)
            {
                return;
            }
            elapsedBeforeRun += watch.Elapsed.TotalSeconds;
            watch.Reset();
            RaiseChanged();
        }

        public void Reset()
        {
            elapsedBeforeRun = 0.0;
            watch.Reset();
            RaiseChanged();
        }

        public void SetFarmSeconds(double seconds)
        {
            double next = ClampFarmSeconds(seconds);
            if (Math.Abs(next - farmSeconds) < 0.001)
            {
                return;
            }
            farmSeconds = next;
            RaiseChanged();
        }

        private static double ClampFarmSeconds(double seconds)
        {
            double rounded = Math.Round(seconds / 5.0) * 5.0;
            return Math.Max(30.0, Math.Min(115.0, rounded));
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }

    public static class LootAlertPlayer
    {
        public static void PlayDoubleTing()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    using (MemoryStream stream = BuildDoubleTingWave())
                    using (SoundPlayer player = new SoundPlayer(stream))
                    {
                        player.PlaySync();
                    }
                }
                catch
                {
                }
            });
        }

        private static MemoryStream BuildDoubleTingWave()
        {
            const int sampleRate = 44100;
            const short channels = 1;
            const short bitsPerSample = 16;
            const double duration = 0.76;
            int sampleCount = (int)(sampleRate * duration);
            int dataLength = sampleCount * channels * (bitsPerSample / 8);

            MemoryStream stream = new MemoryStream(44 + dataLength);
            BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bitsPerSample / 8));
            writer.Write((short)(channels * (bitsPerSample / 8)));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = i / (double)sampleRate;
                double value =
                    BellStrike(t, 0.025, 1174.66) +
                    BellStrike(t, 0.355, 1567.98);
                value = Math.Max(-1.0, Math.Min(1.0, value));
                writer.Write((short)(value * short.MaxValue));
            }

            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        private static double BellStrike(double time, double start, double frequency)
        {
            double local = time - start;
            if (local < 0.0 || local > 0.28)
            {
                return 0.0;
            }

            double attack = Math.Min(1.0, local / 0.006);
            double decay = Math.Exp(-10.5 * local);
            double body =
                Math.Sin(2.0 * Math.PI * frequency * local) +
                0.34 * Math.Sin(2.0 * Math.PI * frequency * 2.01 * local) +
                0.12 * Math.Sin(2.0 * Math.PI * frequency * 3.94 * local);
            return body * attack * decay * 0.48;
        }
    }

    public sealed class OverlaySettings
    {
        private readonly string path;

        public double OverlayLeft = double.NaN;
        public double OverlayTop = double.NaN;
        public double Opacity = 0.92;
        public bool CustomMode;
        public double FarmSeconds = 105.0;
        public bool FlashLootAlert = true;
        public bool SoundLootAlert = true;

        private OverlaySettings(string settingsPath)
        {
            path = settingsPath;
        }

        public static OverlaySettings Load()
        {
            string settingsPath = IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FarmTimerOverlay",
                "settings.ini");
            OverlaySettings settings = new OverlaySettings(settingsPath);

            try
            {
                if (!File.Exists(settingsPath))
                {
                    return settings;
                }

                string[] lines = File.ReadAllLines(settingsPath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    double number;
                    bool flag;
                    if (key == "Left" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    {
                        settings.OverlayLeft = number;
                    }
                    else if (key == "Top" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    {
                        settings.OverlayTop = number;
                    }
                    else if (key == "Opacity" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    {
                        settings.Opacity = Math.Max(0.55, Math.Min(1.0, number));
                    }
                    else if (key == "Custom" && bool.TryParse(value, out flag))
                    {
                        settings.CustomMode = flag;
                    }
                    else if (key == "FarmSeconds" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    {
                        settings.FarmSeconds = Math.Max(30.0, Math.Min(115.0, Math.Round(number / 5.0) * 5.0));
                    }
                    else if (key == "FlashLootAlert" && bool.TryParse(value, out flag))
                    {
                        settings.FlashLootAlert = flag;
                    }
                    else if (key == "SoundLootAlert" && bool.TryParse(value, out flag))
                    {
                        settings.SoundLootAlert = flag;
                    }
                }
            }
            catch
            {
            }
            return settings;
        }

        public void Save()
        {
            try
            {
                string folder = IOPath.GetDirectoryName(path);
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                File.WriteAllLines(path, new string[]
                {
                    "Left=" + OverlayLeft.ToString("0.###", CultureInfo.InvariantCulture),
                    "Top=" + OverlayTop.ToString("0.###", CultureInfo.InvariantCulture),
                    "Opacity=" + Opacity.ToString("0.##", CultureInfo.InvariantCulture),
                    "Custom=" + CustomMode.ToString(CultureInfo.InvariantCulture),
                    "FarmSeconds=" + FarmSeconds.ToString("0", CultureInfo.InvariantCulture),
                    "FlashLootAlert=" + FlashLootAlert.ToString(CultureInfo.InvariantCulture),
                    "SoundLootAlert=" + SoundLootAlert.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch
            {
            }
        }
    }

    public enum ProgressVisualState
    {
        Farm,
        PreLoot,
        Loot,
        Paused
    }

    public sealed class ModernProgressBar : FrameworkElement
    {
        private double progress;
        private double farmFraction = 0.875;
        private double sheenPhase;
        private ProgressVisualState visualState = ProgressVisualState.Farm;

        public ModernProgressBar()
        {
            Height = 14;
            MinHeight = 14;
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
        }

        public void SetVisual(double value, double farmPart, ProgressVisualState state, double sheen)
        {
            value = Math.Max(0.0, Math.Min(1.0, value));
            farmPart = Math.Max(0.05, Math.Min(0.95, farmPart));
            sheen = sheen - Math.Floor(sheen);

            if (Math.Abs(progress - value) < 0.0001 &&
                Math.Abs(farmFraction - farmPart) < 0.0001 &&
                visualState == state &&
                Math.Abs(sheenPhase - sheen) < 0.002)
            {
                return;
            }

            progress = value;
            farmFraction = farmPart;
            visualState = state;
            sheenPhase = sheen;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 2.0 || height <= 2.0)
            {
                return;
            }

            const double railHeight = 8.0;
            double y = Math.Round((height - railHeight) / 2.0);
            Rect railRect = new Rect(0.5, y + 0.5, Math.Max(0.0, width - 1.0), railHeight - 1.0);
            double radius = railRect.Height / 2.0;

            LinearGradientBrush glassBase = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            glassBase.GradientStops.Add(new GradientStop(Color.FromArgb(28, 255, 255, 255), 0.0));
            glassBase.GradientStops.Add(new GradientStop(Color.FromArgb(10, 255, 255, 255), 1.0));
            dc.DrawRoundedRectangle(
                glassBase,
                new Pen(new SolidColorBrush(Color.FromArgb(44, 255, 255, 255)), 1.0),
                railRect,
                radius,
                radius);

            // Preview the complete cycle behind the active fill:
            // FARM is a cool blue glass segment, LOOT is a warm amber segment.
            // The clip keeps both segments inside one seamless capsule.
            double farmWidth = railRect.Width * farmFraction;
            double lootWidth = Math.Max(0.0, railRect.Width - farmWidth);
            dc.PushClip(new RectangleGeometry(railRect, radius, radius));

            LinearGradientBrush farmPreview = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            farmPreview.GradientStops.Add(new GradientStop(Color.FromArgb(46, 71, 139, 242), 0.0));
            farmPreview.GradientStops.Add(new GradientStop(Color.FromArgb(34, 93, 185, 255), 1.0));
            dc.DrawRectangle(
                farmPreview,
                null,
                new Rect(railRect.X, railRect.Y, farmWidth, railRect.Height));

            LinearGradientBrush lootPreview = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            lootPreview.GradientStops.Add(new GradientStop(Color.FromArgb(44, 255, 177, 60), 0.0));
            lootPreview.GradientStops.Add(new GradientStop(Color.FromArgb(34, 255, 221, 116), 1.0));
            dc.DrawRectangle(
                lootPreview,
                null,
                new Rect(railRect.X + farmWidth, railRect.Y, lootWidth, railRect.Height));

            // A very soft seam hints at the phase boundary without becoming a marker.
            if (farmWidth > 2.0 && lootWidth > 2.0)
            {
                dc.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
                    null,
                    new Rect(railRect.X + farmWidth - 0.5, railRect.Y + 1.0, 1.0, Math.Max(1.0, railRect.Height - 2.0)));
            }
            dc.Pop();

            double fillWidth = railRect.Width * progress;
            if (fillWidth <= 0.35)
            {
                return;
            }

            Color start;
            Color end;
            Color glow;
            switch (visualState)
            {
                case ProgressVisualState.PreLoot:
                    start = Color.FromRgb(255, 76, 91);
                    end = Color.FromRgb(255, 151, 82);
                    glow = Color.FromRgb(255, 83, 76);
                    break;
                case ProgressVisualState.Loot:
                    start = Color.FromRgb(255, 181, 65);
                    end = Color.FromRgb(255, 222, 122);
                    glow = Color.FromRgb(255, 194, 73);
                    break;
                case ProgressVisualState.Paused:
                    start = Color.FromRgb(124, 131, 145);
                    end = Color.FromRgb(157, 164, 178);
                    glow = Color.FromRgb(145, 151, 164);
                    break;
                default:
                    start = Color.FromRgb(88, 157, 255);
                    end = Color.FromRgb(114, 207, 255);
                    glow = Color.FromRgb(84, 171, 255);
                    break;
            }

            double coreWidth = Math.Max(1.5, fillWidth);
            Rect fillRect = new Rect(0.5, y + 0.5, coreWidth, railHeight - 1.0);
            double fillRadius = Math.Min(radius, coreWidth / 2.0);

            Rect glowRect = new Rect(
                fillRect.X - 1.0,
                fillRect.Y - 1.5,
                fillRect.Width + 2.0,
                fillRect.Height + 3.0);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(
                    visualState == ProgressVisualState.PreLoot ? (byte)54 : (byte)34,
                    glow.R, glow.G, glow.B)),
                null,
                glowRect,
                glowRect.Height / 2.0,
                glowRect.Height / 2.0);

            LinearGradientBrush fillBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            fillBrush.GradientStops.Add(new GradientStop(start, 0.0));
            fillBrush.GradientStops.Add(new GradientStop(end, 1.0));
            dc.DrawRoundedRectangle(fillBrush, null, fillRect, fillRadius, fillRadius);

            LinearGradientBrush topGloss = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            topGloss.GradientStops.Add(new GradientStop(Color.FromArgb(72, 255, 255, 255), 0.0));
            topGloss.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.7));
            Rect glossRect = new Rect(fillRect.X, fillRect.Y, fillRect.Width, fillRect.Height * 0.55);
            dc.PushClip(new RectangleGeometry(fillRect, fillRadius, fillRadius));
            dc.DrawRectangle(topGloss, null, glossRect);

            if (fillRect.Width > 30.0 && visualState != ProgressVisualState.Paused)
            {
                double sheenWidth = 34.0;
                double travel = fillRect.Width + sheenWidth * 1.5;
                double sheenX = fillRect.X - sheenWidth + travel * sheenPhase;
                LinearGradientBrush sheenBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5)
                };
                sheenBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0));
                sheenBrush.GradientStops.Add(new GradientStop(Color.FromArgb(54, 255, 255, 255), 0.5));
                sheenBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
                dc.DrawRectangle(
                    sheenBrush,
                    null,
                    new Rect(sheenX, fillRect.Y, sheenWidth, fillRect.Height));
            }
            dc.Pop();
        }
    }

    public sealed class FarmOverlayWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 0x4F31;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint VkF9 = 0x78;

        private readonly TimerController timer;
        private readonly OverlaySettings settings;
        private IntPtr hwnd;
        private HwndSource hwndSource;
        private Border card;
        private Border phasePill;
        private TextBlock phaseText;
        private TextBlock timeText;
        private TextBlock phaseCaptionText;
        private TextBlock phaseDetailText;
        private TextBlock cycleText;
        private TextBlock modeText;
        private ModernProgressBar progressBar;
        private Ellipse phaseDot;
        private bool allowPositionSave;
        private bool phaseStateInitialized;
        private bool previousFarmPhase;
        private bool flashActive;

        private readonly SolidColorBrush farmAccent = new SolidColorBrush(Color.FromRgb(105, 171, 255));
        private readonly SolidColorBrush lootAccent = new SolidColorBrush(Color.FromRgb(255, 199, 92));
        private readonly SolidColorBrush textPrimary = new SolidColorBrush(Color.FromRgb(248, 249, 252));
        private readonly SolidColorBrush textMuted = new SolidColorBrush(Color.FromRgb(169, 174, 186));

        public event EventHandler CustomModeChanged;
        public event EventHandler OpacityChangedByApp;

        public bool CustomMode { get; private set; }

        public FarmOverlayWindow(TimerController timerController, OverlaySettings appSettings)
        {
            timer = timerController;
            settings = appSettings;
            Width = 286;
            Height = 148;
            Title = "Farm Timer Overlay";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            Content = BuildUi();
            Left = settings.OverlayLeft;
            Top = settings.OverlayTop;
            Opacity = settings.Opacity;
            CustomMode = settings.CustomMode;

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            LocationChanged += OnLocationChanged;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            SizeChanged += delegate { NativeWindowShape.ApplyRoundedRegion(this, hwnd, 18); };
            timer.Changed += delegate { UpdateUi(); };
        }

        private UIElement BuildUi()
        {
            card = new Border
            {
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(62, 255, 255, 255)),
                Background = new LinearGradientBrush(
                    Color.FromArgb(250, 34, 35, 41),
                    Color.FromArgb(247, 19, 20, 24),
                    new Point(0, 0),
                    new Point(1, 1))
            };

            Grid root = new Grid { Margin = new Thickness(14, 10, 14, 9) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(73) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            card.Child = root;

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.Children.Add(header);

            StackPanel brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Border logo = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(Color.FromRgb(105, 171, 255), Color.FromRgb(155, 127, 255), 45),
                Margin = new Thickness(0, 0, 8, 0)
            };
            logo.Child = new TextBlock
            {
                Text = "\u23F1",
                Foreground = Brushes.White,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Symbol")
            };
            brand.Children.Add(logo);
            brand.Children.Add(new TextBlock
            {
                Text = "FARM TIMER",
                Foreground = textPrimary,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            });
            header.Children.Add(brand);

            phasePill = new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(9, 4, 9, 4),
                Background = new SolidColorBrush(Color.FromArgb(30, 105, 171, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            StackPanel phaseRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            phaseDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = farmAccent,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            phaseText = new TextBlock
            {
                Text = "FARM",
                Foreground = farmAccent,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            };
            phaseRow.Children.Add(phaseDot);
            phaseRow.Children.Add(phaseText);
            phasePill.Child = phaseRow;
            Grid.SetColumn(phasePill, 1);
            header.Children.Add(phasePill);

            Grid main = new Grid { Margin = new Thickness(0, 4, 0, 2) };
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            Grid.SetRow(main, 1);
            root.Children.Add(main);

            Grid clock = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            clock.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            clock.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            TextBlock remainingLabel = new TextBlock
            {
                Text = "C\u00D2N L\u1EA0I",
                Foreground = new SolidColorBrush(Color.FromRgb(137, 143, 155)),
                FontSize = 9,
                FontWeight = FontWeights.Medium,
                Width = 92,
                Margin = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                ClipToBounds = false,
                FontFamily = new FontFamily("Segoe UI")
            };
            TextOptions.SetTextFormattingMode(remainingLabel, TextFormattingMode.Display);
            Grid.SetRow(remainingLabel, 0);
            clock.Children.Add(remainingLabel);

            timeText = new TextBlock
            {
                Text = "02:00",
                Foreground = textPrimary,
                FontSize = 38,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Segoe UI"),
                LineHeight = 39
            };
            TextOptions.SetTextFormattingMode(timeText, TextFormattingMode.Display);
            Grid.SetRow(timeText, 1);
            clock.Children.Add(timeText);

            Border clockFrame = new Border
            {
                Width = 132,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)),
                Padding = new Thickness(5, 3, 5, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = clock
            };
            main.Children.Add(clockFrame);

            StackPanel details = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            phaseCaptionText = new TextBlock
            {
                Text = "FARM C\u00D2N",
                Foreground = new SolidColorBrush(Color.FromRgb(146, 152, 164)),
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            };
            phaseDetailText = new TextBlock
            {
                Text = "01:45",
                Foreground = textPrimary,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            };
            TextBlock splitText = new TextBlock
            {
                Text = "F 1:45  \u2022  L 0:15",
                Foreground = textMuted,
                FontSize = 7.8,
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("Segoe UI")
            };
            splitText.Name = "SplitInfo";
            details.Children.Add(phaseCaptionText);
            details.Children.Add(phaseDetailText);
            details.Children.Add(splitText);
            Border detailsFrame = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)),
                Padding = new Thickness(6, 6, 6, 6),
                Margin = new Thickness(6, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = details
            };
            Grid.SetColumn(detailsFrame, 1);
            main.Children.Add(detailsFrame);

            progressBar = new ModernProgressBar
            {
                Margin = new Thickness(0, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(progressBar, 2);
            root.Children.Add(progressBar);

            Grid footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cycleText = new TextBlock
            {
                Text = "V\u00D2NG 01",
                Foreground = textMuted,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            };
            modeText = new TextBlock
            {
                Text = "KH\u00D3A  \u2022  CTRL+ALT+F9",
                Foreground = new SolidColorBrush(Color.FromRgb(132, 137, 148)),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = new FontFamily("Segoe UI")
            };
            footer.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(cycleText);
            Grid.SetColumn(modeText, 1);
            footer.Children.Add(modeText);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            return card;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            hwndSource = HwndSource.FromHwnd(hwnd);
            if (hwndSource != null)
            {
                hwndSource.AddHook(WndProc);
            }
            RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt, VkF9);
            ApplyInteractionMode();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (double.IsNaN(Left) || double.IsNaN(Top) || Left == 0 && Top == 0)
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Right - Width - 22;
                Top = area.Top + 22;
            }
            EnsureVisible();
            NativeWindowShape.ApplyRoundedRegion(this, hwnd, 18);
            allowPositionSave = true;
            UpdateUi();
        }

        private void EnsureVisible()
        {
            Drawing.Rectangle bounds = WinForms.SystemInformation.VirtualScreen;
            if (Left + 40 < bounds.Left || Left > bounds.Right - 40 ||
                Top + 40 < bounds.Top || Top > bounds.Bottom - 40)
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Right - Width - 22;
                Top = area.Top + 22;
            }
        }

        private void UpdateUi()
        {
            double cycleElapsed = timer.CycleElapsed;
            bool isFarm = timer.IsFarmPhase;
            double totalRemaining = TimerController.CycleSeconds - cycleElapsed;
            int displayRemaining = Math.Max(1, (int)Math.Ceiling(totalRemaining));
            timeText.Text = FormatSeconds(displayRemaining);

            double phaseRemaining = isFarm
                ? timer.FarmSeconds - cycleElapsed
                : TimerController.CycleSeconds - cycleElapsed;
            int phaseDisplay = Math.Max(1, (int)Math.Ceiling(phaseRemaining));

            SolidColorBrush accent = isFarm ? farmAccent : lootAccent;
            string phaseName = isFarm ? "FARM" : "NH\u1EB6T TI\u1EC0N";
            phaseText.Text = timer.IsRunning ? phaseName : "D\u1EEANG";
            phaseText.Foreground = timer.IsRunning ? accent : textMuted;
            phaseDot.Fill = timer.IsRunning ? accent : textMuted;
            phasePill.Background = new SolidColorBrush(Color.FromArgb(
                31,
                timer.IsRunning ? accent.Color.R : textMuted.Color.R,
                timer.IsRunning ? accent.Color.G : textMuted.Color.G,
                timer.IsRunning ? accent.Color.B : textMuted.Color.B));
            phaseCaptionText.Text = phaseName + " C\u00D2N";
            phaseCaptionText.Foreground = timer.IsRunning ? accent : textMuted;
            phaseDetailText.Text = FormatSeconds(phaseDisplay);
            cycleText.Text = "V\u00D2NG " + Math.Min(timer.CycleIndex, 9999).ToString("00", CultureInfo.InvariantCulture);
            if (!flashActive)
            {
                timeText.Foreground = isFarm ? textPrimary : lootAccent;
            }
            bool isPreLoot = timer.IsRunning &&
                             isFarm &&
                             phaseRemaining > 0.0 &&
                             phaseRemaining <= 5.0;
            ProgressVisualState progressState = !timer.IsRunning
                ? ProgressVisualState.Paused
                : (isPreLoot
                    ? ProgressVisualState.PreLoot
                    : (isFarm ? ProgressVisualState.Farm : ProgressVisualState.Loot));
            double normalizedProgress = Math.Max(0.0, Math.Min(1.0, cycleElapsed / TimerController.CycleSeconds));
            double sheen = (timer.TotalElapsed * 0.34) % 1.0;
            progressBar.SetVisual(
                normalizedProgress,
                timer.FarmSeconds / TimerController.CycleSeconds,
                progressState,
                sheen);

            HandlePhaseAlerts(isFarm, phaseRemaining);

            TextBlock splitInfo = FindTextBlock(card, "SplitInfo");
            if (splitInfo != null)
            {
                splitInfo.Text = "F " + FormatShort(timer.FarmSeconds) +
                                 "  \u2022  L " + FormatShort(timer.LootSeconds);
            }
        }

        private void HandlePhaseAlerts(bool isFarm, double phaseRemaining)
        {
            if (!phaseStateInitialized)
            {
                previousFarmPhase = isFarm;
                phaseStateInitialized = true;
            }

            bool shouldFlash = timer.IsRunning &&
                               isFarm &&
                               settings.FlashLootAlert &&
                               phaseRemaining > 0.0 &&
                               phaseRemaining <= 5.0;
            if (shouldFlash && !flashActive)
            {
                StartPreLootFlash();
            }
            else if (!shouldFlash && flashActive)
            {
                StopPreLootFlash(isFarm);
            }

            if (timer.IsRunning && previousFarmPhase && !isFarm)
            {
                if (settings.SoundLootAlert)
                {
                    LootAlertPlayer.PlayDoubleTing();
                }
            }
            previousFarmPhase = isFarm;
        }

        private void StartPreLootFlash()
        {
            flashActive = true;
            SolidColorBrush flashBrush = new SolidColorBrush(textPrimary.Color);
            timeText.Foreground = flashBrush;

            ColorAnimation animation = new ColorAnimation
            {
                From = textPrimary.Color,
                To = Color.FromRgb(255, 67, 78),
                Duration = TimeSpan.FromMilliseconds(240),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            flashBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }

        private void StopPreLootFlash(bool isFarm)
        {
            flashActive = false;
            timeText.Foreground = isFarm ? textPrimary : lootAccent;
        }

        private static TextBlock FindTextBlock(DependencyObject parent, string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                TextBlock tb = child as TextBlock;
                if (tb != null && tb.Name == name)
                {
                    return tb;
                }
                TextBlock nested = FindTextBlock(child, name);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }

        public void SetCustomMode(bool enabled)
        {
            if (CustomMode == enabled && hwnd != IntPtr.Zero)
            {
                ApplyInteractionMode();
                return;
            }
            CustomMode = enabled;
            settings.CustomMode = enabled;
            settings.Save();
            ApplyInteractionMode();
            EventHandler handler = CustomModeChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        public void SetOverlayOpacity(double value)
        {
            Opacity = Math.Max(0.55, Math.Min(1.0, value));
            settings.Opacity = Opacity;
            settings.Save();
            EventHandler handler = OpacityChangedByApp;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void ApplyInteractionMode()
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            int style = GetWindowLong(hwnd, GwlExStyle);
            style |= WsExToolWindow;
            if (CustomMode)
            {
                style &= ~WsExTransparent;
                style &= ~WsExNoActivate;
                Focusable = true;
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(130, 105, 171, 255));
                modeText.Text = "CUSTOM  \u2022  K\u00C9O \u0110\u1EC2 DI CHUY\u1EC2N";
            }
            else
            {
                style |= WsExTransparent;
                style |= WsExNoActivate;
                Focusable = false;
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(62, 255, 255, 255));
                modeText.Text = "KH\u00D3A  \u2022  CTRL+ALT+F9";
            }
            SetWindowLong(hwnd, GwlExStyle, style);
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!CustomMode || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            try
            {
                DragMove();
            }
            catch
            {
            }
        }

        private void OnLocationChanged(object sender, EventArgs e)
        {
            if (!allowPositionSave || !CustomMode)
            {
                return;
            }
            settings.OverlayLeft = Left;
            settings.OverlayTop = Top;
            settings.Save();
        }

        private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                SetCustomMode(!CustomMode);
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, HotkeyId);
            }
            if (hwndSource != null)
            {
                hwndSource.RemoveHook(WndProc);
            }
            base.OnClosed(e);
        }

        private static string FormatSeconds(double value)
        {
            int total = Math.Max(0, (int)Math.Round(value));
            return (total / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                   (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static string FormatShort(double value)
        {
            int total = Math.Max(0, (int)Math.Round(value));
            return (total / 60).ToString(CultureInfo.InvariantCulture) + ":" +
                   (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    public sealed class FarmControlWindow : Window
    {
        private readonly TimerController timer;
        private readonly OverlaySettings settings;
        private readonly FarmOverlayWindow overlay;
        private readonly Action exitAction;
        private bool allowClose;
        private IntPtr hwnd;

        private TextBlock liveTimeText;
        private TextBlock livePhaseText;
        private TextBlock farmValueText;
        private TextBlock lootValueText;
        private TextBlock opacityValueText;
        private Border startButton;
        private Border stopButton;
        private Border resetButton;
        private Border customToggle;
        private Ellipse customThumb;
        private Border flashToggle;
        private Ellipse flashThumb;
        private Border soundToggle;
        private Ellipse soundThumb;
        private Grid splitSlider;
        private Border splitFarmFill;
        private Border splitThumb;
        private Slider opacitySlider;
        private bool splitDragging;

        private readonly SolidColorBrush farmAccent = new SolidColorBrush(Color.FromRgb(105, 171, 255));
        private readonly SolidColorBrush lootAccent = new SolidColorBrush(Color.FromRgb(255, 199, 92));
        private readonly SolidColorBrush textPrimary = new SolidColorBrush(Color.FromRgb(245, 247, 250));
        private readonly SolidColorBrush textMuted = new SolidColorBrush(Color.FromRgb(158, 164, 176));
        private readonly SolidColorBrush surface = new SolidColorBrush(Color.FromRgb(31, 32, 38));

        public FarmControlWindow(
            TimerController timerController,
            OverlaySettings appSettings,
            FarmOverlayWindow overlayWindow,
            Action exitApplication)
        {
            timer = timerController;
            settings = appSettings;
            overlay = overlayWindow;
            exitAction = exitApplication;

            Width = 448;
            Height = 590;
            Title = "Farm Timer Control";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            Content = BuildUi();
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
            };
            Loaded += delegate
            {
                NativeWindowShape.ApplyRoundedRegion(this, hwnd, 20);
                SyncOverlaySettings();
                UpdateUi();
            };
            SizeChanged += delegate { NativeWindowShape.ApplyRoundedRegion(this, hwnd, 20); };
            Closing += OnClosing;
            timer.Changed += delegate { UpdateUi(); };
        }

        private UIElement BuildUi()
        {
            Border frame = new Border
            {
                CornerRadius = new CornerRadius(20),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(66, 255, 255, 255)),
                Background = new LinearGradientBrush(
                    Color.FromRgb(35, 36, 43),
                    Color.FromRgb(22, 23, 28),
                    new Point(0, 0),
                    new Point(1, 1))
            };

            Grid root = new Grid { Margin = new Thickness(20, 16, 20, 17) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            frame.Child = root;

            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };
            root.Children.Add(header);

            StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            Border logo = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(10),
                Background = new LinearGradientBrush(Color.FromRgb(105, 171, 255), Color.FromRgb(155, 127, 255), 45),
                Margin = new Thickness(0, 0, 11, 0)
            };
            logo.Child = new TextBlock
            {
                Text = "\u23F1",
                Foreground = Brushes.White,
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Symbol")
            };
            titleRow.Children.Add(logo);
            StackPanel titleText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleText.Children.Add(new TextBlock
            {
                Text = "Farm Timer",
                Foreground = textPrimary,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            });
            titleText.Children.Add(new TextBlock
            {
                Text = "2:00 mỗi vòng \u2022 tự lặp",
                Foreground = textMuted,
                FontSize = 9.5,
                Margin = new Thickness(0, 2, 0, 0),
                FontFamily = new FontFamily("Segoe UI")
            });
            titleRow.Children.Add(titleText);
            header.Children.Add(titleRow);

            StackPanel windowButtons = new StackPanel { Orientation = Orientation.Horizontal };
            Border minimize = MakeHeaderButton("\u2014");
            minimize.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                WindowState = WindowState.Minimized;
            };
            Border close = MakeHeaderButton("\u00D7");
            close.Margin = new Thickness(6, 0, 0, 0);
            close.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                Hide();
            };
            windowButtons.Children.Add(minimize);
            windowButtons.Children.Add(close);
            Grid.SetColumn(windowButtons, 1);
            header.Children.Add(windowButtons);

            Border liveCard = MakeCard();
            liveCard.Padding = new Thickness(15, 12, 15, 12);
            Grid liveGrid = new Grid();
            liveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            liveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel liveLeft = new StackPanel();
            livePhaseText = new TextBlock
            {
                Text = "FARM",
                Foreground = farmAccent,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI")
            };
            liveLeft.Children.Add(livePhaseText);
            liveLeft.Children.Add(new TextBlock
            {
                Text = "Trạng thái overlay",
                Foreground = textMuted,
                FontSize = 9,
                Margin = new Thickness(0, 3, 0, 0),
                FontFamily = new FontFamily("Segoe UI")
            });
            liveGrid.Children.Add(liveLeft);
            liveTimeText = new TextBlock
            {
                Text = "02:00",
                Foreground = textPrimary,
                FontSize = 27,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(liveTimeText, 1);
            liveGrid.Children.Add(liveTimeText);
            liveCard.Child = liveGrid;
            Grid.SetRow(liveCard, 1);
            root.Children.Add(liveCard);

            Grid actionRow = new Grid { Margin = new Thickness(0, 10, 0, 14) };
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            startButton = MakeActionButton("\u25B6", "START", true, timer.Start);
            startButton.Margin = new Thickness(0, 0, 7, 0);
            actionRow.Children.Add(startButton);
            stopButton = MakeActionButton("\u25A0", "STOP", false, timer.Stop);
            stopButton.Margin = new Thickness(0, 0, 7, 0);
            Grid.SetColumn(stopButton, 1);
            actionRow.Children.Add(stopButton);
            resetButton = MakeActionButton("\u21BB", "RESET", false, timer.Reset);
            Grid.SetColumn(resetButton, 2);
            actionRow.Children.Add(resetButton);
            Grid.SetRow(actionRow, 2);
            root.Children.Add(actionRow);

            Border splitCard = MakeCard();
            splitCard.Padding = new Thickness(15, 13, 15, 13);
            Grid splitContent = new Grid();
            splitContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            splitContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            splitContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            splitContent.Children.Add(new TextBlock
            {
                Text = "CHIA TH\u1EDCI GIAN",
                Foreground = textMuted,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            });

            Grid splitLabels = new Grid { Margin = new Thickness(0, 9, 0, 8) };
            splitLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            splitLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel farmLabel = new StackPanel();
            farmLabel.Children.Add(new TextBlock
            {
                Text = "FARM",
                Foreground = farmAccent,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI")
            });
            farmValueText = new TextBlock
            {
                Text = "01:45",
                Foreground = textPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 0),
                FontFamily = new FontFamily("Segoe UI")
            };
            farmLabel.Children.Add(farmValueText);
            splitLabels.Children.Add(farmLabel);
            StackPanel lootLabel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            lootLabel.Children.Add(new TextBlock
            {
                Text = "NH\u1EB6T TI\u1EC0N",
                Foreground = lootAccent,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = new FontFamily("Segoe UI")
            });
            lootValueText = new TextBlock
            {
                Text = "00:15",
                Foreground = textPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = new FontFamily("Segoe UI")
            };
            lootLabel.Children.Add(lootValueText);
            Grid.SetColumn(lootLabel, 1);
            splitLabels.Children.Add(lootLabel);
            Grid.SetRow(splitLabels, 1);
            splitContent.Children.Add(splitLabels);

            splitSlider = new Grid
            {
                Height = 28,
                Cursor = Cursors.SizeWE,
                Background = Brushes.Transparent
            };
            Border lootTrack = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(117, 91, 39)),
                VerticalAlignment = VerticalAlignment.Center
            };
            splitFarmFill = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = farmAccent,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            splitThumb = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(105, 171, 255)),
                BorderThickness = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            splitSlider.Children.Add(lootTrack);
            splitSlider.Children.Add(splitFarmFill);
            splitSlider.Children.Add(splitThumb);
            splitSlider.SizeChanged += delegate { UpdateSplitSliderVisual(); };
            splitSlider.MouseLeftButtonDown += OnSplitMouseDown;
            splitSlider.MouseMove += OnSplitMouseMove;
            splitSlider.MouseLeftButtonUp += OnSplitMouseUp;
            Grid.SetRow(splitSlider, 2);
            splitContent.Children.Add(splitSlider);
            splitCard.Child = splitContent;
            Grid.SetRow(splitCard, 3);
            root.Children.Add(splitCard);

            Border overlayCard = MakeCard();
            overlayCard.Padding = new Thickness(15, 13, 15, 13);
            overlayCard.Margin = new Thickness(0, 10, 0, 0);
            Grid overlayGrid = new Grid();
            overlayGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            overlayGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid customRow = new Grid();
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel customText = new StackPanel();
            customText.Children.Add(new TextBlock
            {
                Text = "Custom overlay",
                Foreground = textPrimary,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            });
            customText.Children.Add(new TextBlock
            {
                Text = "B\u1EADt \u0111\u1EC3 k\u00E9o overlay \u2022 Ctrl+Alt+F9",
                Foreground = textMuted,
                FontSize = 9,
                Margin = new Thickness(0, 3, 0, 0),
                FontFamily = new FontFamily("Segoe UI")
            });
            customRow.Children.Add(customText);
            customToggle = new Border
            {
                Width = 46,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(65, 67, 76)),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            customThumb = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(3, 0, 0, 0)
            };
            customToggle.Child = customThumb;
            customToggle.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; };
            customToggle.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                overlay.SetCustomMode(!overlay.CustomMode);
                SyncOverlaySettings();
            };
            Grid.SetColumn(customToggle, 1);
            customRow.Children.Add(customToggle);
            overlayGrid.Children.Add(customRow);

            Grid opacityRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            opacityRow.Children.Add(new TextBlock
            {
                Text = "Opacity",
                Foreground = textMuted,
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            });
            opacitySlider = new Slider
            {
                Minimum = 55,
                Maximum = 100,
                TickFrequency = 1,
                IsSnapToTickEnabled = false,
                Value = settings.Opacity * 100.0,
                Height = 24,
                Margin = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            opacitySlider.ValueChanged += delegate
            {
                if (opacityValueText != null)
                {
                    int value = (int)Math.Round(opacitySlider.Value);
                    opacityValueText.Text = value.ToString(CultureInfo.InvariantCulture) + "%";
                    overlay.SetOverlayOpacity(value / 100.0);
                }
            };
            Grid.SetColumn(opacitySlider, 1);
            opacityRow.Children.Add(opacitySlider);
            opacityValueText = new TextBlock
            {
                Text = ((int)Math.Round(settings.Opacity * 100.0)).ToString(CultureInfo.InvariantCulture) + "%",
                Foreground = textPrimary,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            };
            Grid.SetColumn(opacityValueText, 2);
            opacityRow.Children.Add(opacityValueText);
            Grid.SetRow(opacityRow, 1);
            overlayGrid.Children.Add(opacityRow);

            overlayCard.Child = overlayGrid;
            Grid.SetRow(overlayCard, 4);
            root.Children.Add(overlayCard);

            Border alertCard = MakeCard();
            alertCard.Padding = new Thickness(15, 13, 15, 13);
            alertCard.Margin = new Thickness(0, 10, 0, 0);
            StackPanel alertStack = new StackPanel();
            alertStack.Children.Add(new TextBlock
            {
                Text = "C\u1EA2NH B\u00C1O LOOT",
                Foreground = textMuted,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 9),
                FontFamily = new FontFamily("Segoe UI")
            });

            Grid flashRow = MakeToggleSettingRow(
                "Nh\u00E1y \u0111\u1ECF tr\u01B0\u1EDBc LOOT 5 gi\u00E2y",
                "Nh\u00E1y trong 5 gi\u00E2y cu\u1ED1i c\u1EE7a giai \u0111o\u1EA1n FARM",
                settings.FlashLootAlert,
                delegate(bool enabled)
                {
                    settings.FlashLootAlert = enabled;
                    settings.Save();
                },
                out flashToggle,
                out flashThumb);
            alertStack.Children.Add(flashRow);

            Border divider = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                Margin = new Thickness(0, 10, 0, 10)
            };
            alertStack.Children.Add(divider);

            Grid soundRow = MakeToggleSettingRow(
                "Âm thanh ting ting",
                "2 ti\u1EBFng chime khi b\u1EAFt \u0111\u1EA7u giai \u0111o\u1EA1n LOOT",
                settings.SoundLootAlert,
                delegate(bool enabled)
                {
                    settings.SoundLootAlert = enabled;
                    settings.Save();
                    if (enabled)
                    {
                        LootAlertPlayer.PlayDoubleTing();
                    }
                },
                out soundToggle,
                out soundThumb);
            alertStack.Children.Add(soundRow);
            alertCard.Child = alertStack;
            Grid.SetRow(alertCard, 5);
            root.Children.Add(alertCard);

            Grid footer = new Grid { Margin = new Thickness(2, 12, 2, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(new TextBlock
            {
                Text = "Overlay lu\u00F4n n\u1EB1m tr\u00EAn c\u00F9ng",
                Foreground = new SolidColorBrush(Color.FromRgb(124, 129, 141)),
                FontSize = 8.5,
                FontFamily = new FontFamily("Segoe UI")
            });
            TextBlock exitText = new TextBlock
            {
                Text = "Tho\u00E1t h\u1EB3n",
                Foreground = new SolidColorBrush(Color.FromRgb(184, 188, 198)),
                FontSize = 8.5,
                Cursor = Cursors.Hand,
                FontFamily = new FontFamily("Segoe UI")
            };
            exitText.MouseLeftButtonUp += delegate { exitAction(); };
            Grid.SetColumn(exitText, 1);
            footer.Children.Add(exitText);
            Grid.SetRow(footer, 6);
            root.Children.Add(footer);

            return frame;
        }

        private Border MakeCard()
        {
            return new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255))
            };
        }

        private Border MakeHeaderButton(string text)
        {
            Border button = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                Cursor = Cursors.Hand
            };
            button.Child = new TextBlock
            {
                Text = text,
                Foreground = textMuted,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            };
            button.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; };
            button.MouseEnter += delegate { button.Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)); };
            button.MouseLeave += delegate { button.Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)); };
            return button;
        }

        private Grid MakeToggleSettingRow(
            string title,
            string subtitle,
            bool initialValue,
            Action<bool> changed,
            out Border toggle,
            out Ellipse thumb)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = textPrimary,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            });
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = textMuted,
                FontSize = 8.8,
                Margin = new Thickness(0, 3, 10, 0),
                FontFamily = new FontFamily("Segoe UI")
            });
            row.Children.Add(text);

            Ellipse localThumb = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            Border localToggle = new Border
            {
                Width = 46,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Child = localThumb
            };
            SetToggleVisual(localToggle, localThumb, initialValue);
            localToggle.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
            };
            localToggle.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                bool next = localThumb.HorizontalAlignment != HorizontalAlignment.Right;
                SetToggleVisual(localToggle, localThumb, next);
                changed(next);
            };
            Grid.SetColumn(localToggle, 1);
            row.Children.Add(localToggle);

            toggle = localToggle;
            thumb = localThumb;
            return row;
        }

        private static void SetToggleVisual(Border toggle, Ellipse thumb, bool enabled)
        {
            if (toggle == null || thumb == null)
            {
                return;
            }
            toggle.Background = enabled
                ? new SolidColorBrush(Color.FromRgb(69, 121, 184))
                : new SolidColorBrush(Color.FromRgb(65, 67, 76));
            thumb.HorizontalAlignment = enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            thumb.Margin = enabled ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
        }

        private Border MakeActionButton(string glyph, string text, bool primary, Action action)
        {
            Color normal = primary ? Color.FromArgb(72, 105, 171, 255) : Color.FromArgb(22, 255, 255, 255);
            Color hover = primary ? Color.FromArgb(100, 105, 171, 255) : Color.FromArgb(38, 255, 255, 255);
            Color border = primary ? Color.FromArgb(120, 105, 171, 255) : Color.FromArgb(40, 255, 255, 255);
            Border button = new Border
            {
                Height = 36,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(border),
                Background = new SolidColorBrush(normal),
                Cursor = Cursors.Hand
            };
            StackPanel row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new TextBlock
            {
                Text = glyph,
                Foreground = primary ? Brushes.White : textMuted,
                FontSize = 10,
                Margin = new Thickness(0, 0, 6, 0),
                FontFamily = new FontFamily("Segoe UI Symbol")
            });
            row.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = textPrimary,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            });
            button.Child = row;
            button.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; };
            button.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (button.Opacity > 0.65)
                {
                    action();
                }
            };
            button.MouseEnter += delegate
            {
                if (button.Opacity > 0.65)
                {
                    button.Background = new SolidColorBrush(hover);
                }
            };
            button.MouseLeave += delegate { button.Background = new SolidColorBrush(normal); };
            return button;
        }

        private void OnSplitMouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            splitDragging = true;
            splitSlider.CaptureMouse();
            SetSplitFromPoint(e.GetPosition(splitSlider).X);
        }

        private void OnSplitMouseMove(object sender, MouseEventArgs e)
        {
            if (!splitDragging || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            SetSplitFromPoint(e.GetPosition(splitSlider).X);
        }

        private void OnSplitMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!splitDragging)
            {
                return;
            }
            e.Handled = true;
            SetSplitFromPoint(e.GetPosition(splitSlider).X);
            splitDragging = false;
            splitSlider.ReleaseMouseCapture();
        }

        private void SetSplitFromPoint(double x)
        {
            if (splitSlider.ActualWidth <= 0)
            {
                return;
            }
            double fraction = Math.Max(0.0, Math.Min(1.0, x / splitSlider.ActualWidth));
            double farmSeconds = Math.Round((fraction * TimerController.CycleSeconds) / 5.0) * 5.0;
            farmSeconds = Math.Max(30.0, Math.Min(115.0, farmSeconds));
            timer.SetFarmSeconds(farmSeconds);
            settings.FarmSeconds = timer.FarmSeconds;
            settings.Save();
            UpdateSplitSliderVisual();
        }

        private void UpdateSplitSliderVisual()
        {
            if (splitSlider == null || splitSlider.ActualWidth <= 0)
            {
                return;
            }
            double position = splitSlider.ActualWidth * (timer.FarmSeconds / TimerController.CycleSeconds);
            splitFarmFill.Width = Math.Max(0.0, position);
            splitThumb.Margin = new Thickness(
                Math.Max(0.0, Math.Min(splitSlider.ActualWidth - splitThumb.Width, position - splitThumb.Width / 2.0)),
                0, 0, 0);
        }

        private void UpdateUi()
        {
            if (liveTimeText == null)
            {
                return;
            }
            double cycleElapsed = timer.CycleElapsed;
            int totalRemaining = Math.Max(1, (int)Math.Ceiling(TimerController.CycleSeconds - cycleElapsed));
            liveTimeText.Text = FormatSeconds(totalRemaining);
            livePhaseText.Text = timer.IsRunning
                ? (timer.IsFarmPhase ? "FARM" : "NH\u1EB6T TI\u1EC0N")
                : "D\u1EEANG";
            livePhaseText.Foreground = !timer.IsRunning
                ? textMuted
                : (timer.IsFarmPhase ? farmAccent : lootAccent);
            farmValueText.Text = FormatSeconds(timer.FarmSeconds);
            lootValueText.Text = FormatSeconds(timer.LootSeconds);
            startButton.Opacity = timer.IsRunning ? 0.46 : 1.0;
            stopButton.Opacity = timer.IsRunning ? 1.0 : 0.46;
            UpdateSplitSliderVisual();
        }

        public void SyncOverlaySettings()
        {
            if (customToggle == null)
            {
                return;
            }
            bool enabled = overlay.CustomMode;
            customToggle.Background = enabled
                ? new SolidColorBrush(Color.FromRgb(69, 121, 184))
                : new SolidColorBrush(Color.FromRgb(65, 67, 76));
            customThumb.HorizontalAlignment = enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            customThumb.Margin = enabled ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
            opacitySlider.Value = Math.Round(overlay.Opacity * 100.0);
            opacityValueText.Text = ((int)Math.Round(overlay.Opacity * 100.0)).ToString(CultureInfo.InvariantCulture) + "%";
            SetToggleVisual(flashToggle, flashThumb, settings.FlashLootAlert);
            SetToggleVisual(soundToggle, soundThumb, settings.SoundLootAlert);
        }

        public void ForceClose()
        {
            allowClose = true;
            Close();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private static string FormatSeconds(double value)
        {
            int total = Math.Max(0, (int)Math.Round(value));
            return (total / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                   (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }
    }

    public static class NativeWindowShape
    {
        public static void ApplyRoundedRegion(Window window, IntPtr hwnd, double radiusDip)
        {
            if (hwnd == IntPtr.Zero || window.ActualWidth <= 0 || window.ActualHeight <= 0)
            {
                return;
            }

            PresentationSource source = PresentationSource.FromVisual(window);
            double scaleX = 1.0;
            double scaleY = 1.0;
            if (source != null && source.CompositionTarget != null)
            {
                Matrix matrix = source.CompositionTarget.TransformToDevice;
                scaleX = matrix.M11;
                scaleY = matrix.M22;
            }

            int pixelWidth = Math.Max(1, (int)Math.Round(window.ActualWidth * scaleX));
            int pixelHeight = Math.Max(1, (int)Math.Round(window.ActualHeight * scaleY));
            int diameter = Math.Max(12, (int)Math.Round(radiusDip * 2.0 * Math.Min(scaleX, scaleY)));
            IntPtr region = CreateRoundRectRgn(0, 0, pixelWidth + 1, pixelHeight + 1, diameter, diameter);
            if (region != IntPtr.Zero)
            {
                if (SetWindowRgn(hwnd, region, true) == 0)
                {
                    DeleteObject(region);
                }
            }
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
            int nWidthEllipse, int nHeightEllipse);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
