using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VoicemeeterAgent;

/// <summary>
/// Entry point. Runs as a silent Windows tray app.
/// Right-click the tray icon to sync now or exit.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Headless check of the volume curves; used by CI and by "VoicemeeterAgent.exe --selftest".
        if (args.Contains("--selftest")) return VolumeCurve.SelfTest() ? 0 : 1;

        // ── Single-instance guard ────────────────────────────────────────────
        // Prevents a second tray icon when Windows launches the app at startup
        // while a previous instance is already running.
        using var mutex = new System.Threading.Mutex(
            initiallyOwned: true,
            name: "Global\\VoicemeeterAgent_SingleInstance",
            out bool isNewInstance);
        if (!isNewInstance) return 0;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Catch unhandled exceptions on UI thread and thread-pool threads so the app
        // doesn't silently vanish from the tray without any indication.
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "VoicemeeterAgent – Unexpected Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject?.ToString(), "VoicemeeterAgent – Fatal Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        // ── Migrate old startup registry key (one-time) ───────────────────────
        Startup.MigrateOldKey();

        // ── Resolve DLL ──────────────────────────────────────────────────────
        var dllPath = VoicemeeterApi.FindDllPath();
        if (dllPath == null)
        {
            MessageBox.Show(
                "Could not locate VoicemeeterRemote64.dll.\n\n" +
                "Please ensure Voicemeeter Banana is installed.",
                "VoicemeeterAgent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        VoicemeeterApi.PreloadDll(dllPath);

        // ── Wire up services ─────────────────────────────────────────────────
        // WindowsFormsSynchronizationContext must be created explicitly here.
        // SynchronizationContext.Current is null before Application.Run() installs
        // the WinForms message pump — capturing Current directly would give null and
        // crash the process when ConnectionStateChanged fires from the timer thread.
        var uiContext = new WindowsFormsSynchronizationContext();
        using var api = new VoicemeeterApi();
        using var watcher = new WindowsVolumeWatcher();
        var config = AppConfig.Load();
        using var sync = new SyncApp(api, watcher, config);

        // ── Tray icon ────────────────────────────────────────────────────────
        using var trayIcon = BuildTrayIcon(sync, uiContext);
        trayIcon.Visible = true;

        sync.Start();

        Application.Run();          // message loop; exits on "Exit" menu click
        return 0;
    }

    /// <summary>Status dot colours. A disabled menu item would grey these out, so the item stays enabled.</summary>
    private static readonly Color ConnectedColor = Color.FromArgb(0, 200, 83);
    private static readonly Color DisconnectedColor = Color.FromArgb(200, 60, 60);
    private static readonly Color ConnectingColor = Color.FromArgb(150, 150, 150);

    // ─── Tray icon construction ───────────────────────────────────────────────

    private static NotifyIcon BuildTrayIcon(SyncApp sync, SynchronizationContext uiContext)
    {
        var menu = new ContextMenuStrip();

        // ── Status indicator (non-clickable, informational) ───────────────────
        // Kept enabled with no Click handler: a disabled ToolStripMenuItem is drawn greyed out,
        // which swallows the ForeColor and leaves the dot the same colour in both states.
        var statusItem = new ToolStripMenuItem("⬤  Connecting…")
        {
            ForeColor = ConnectingColor
        };
        sync.ConnectionStateChanged += connected =>
            uiContext.Post(_ =>
            {
                statusItem.Text = connected ? "⬤  Connected" : "⬤  Disconnected";
                statusItem.ForeColor = connected ? ConnectedColor : DisconnectedColor;
            }, null);

        var statusSeparator = new ToolStripSeparator();

        var syncNowItem = new ToolStripMenuItem("Sync Now");
        syncNowItem.Click += (_, _) => sync.SyncNow();

        var volumeItem = BuildVolumeMenu(sync);

        var devicesItem = BuildDevicesMenu(sync);

        var watchdogItem = new ToolStripMenuItem("Auto-reconnect bus devices")
        {
            CheckOnClick = true,
            Checked = sync.Config.DeviceWatchdogEnabled,
            ToolTipText = "Detach a bus device while it is missing so Voicemeeter stops " +
                          "re-probing it, and re-attach it once it comes back."
        };
        watchdogItem.CheckedChanged += (_, _) =>
        {
            sync.Config.DeviceWatchdogEnabled = watchdogItem.Checked;
            sync.Config.Save();
        };

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = Startup.IsEnabled()
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            if (startupItem.Checked) Startup.Enable();
            else Startup.Disable();
        };

        var separator = new ToolStripSeparator();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Application.Exit();

        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem, statusSeparator,
            syncNowItem, volumeItem, devicesItem, watchdogItem, startupItem, separator, exitItem
        });

        return new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "VoicemeeterAgent",
            ContextMenuStrip = menu,
        };
    }

    // ─── Volume curve menu ────────────────────────────────────────────────────

    /// <summary>Selectable volume curves, in menu order.</summary>
    private static readonly (VolumeProfile Profile, string Label)[] VolumeProfiles =
    {
        (VolumeProfile.Knee,      "Knee — 50% = -20 dB"),
        (VolumeProfile.LinearDb,  "Linear dB — 50% = -30 dB (v1.0)"),
        (VolumeProfile.Gamma2,    "Squared — 50% = -12 dB"),
        (VolumeProfile.Amplitude, "Amplitude — 50% = -6 dB (v1.1)"),
    };

    /// <summary>Selectable gain floors, in menu order. A higher floor spreads less dB over the slider.</summary>
    private static readonly float[] GainFloors = { -40f, -50f, -60f };

    /// <summary>
    /// Builds the "Volume Curve" submenu: one entry per curve shape, plus the gain floor.
    /// Picking either re-syncs immediately so the change is audible without touching the slider.
    /// </summary>
    private static ToolStripMenuItem BuildVolumeMenu(SyncApp sync)
    {
        var root = new ToolStripMenuItem("Volume Curve");
        var profileItems = new List<(VolumeProfile Profile, ToolStripMenuItem Item)>();
        var floorItems = new List<(float Floor, ToolStripMenuItem Item)>();

        void Refresh()
        {
            foreach (var (profile, item) in profileItems)
                item.Checked = sync.Config.Profile == profile;
            foreach (var (floor, item) in floorItems)
                item.Checked = MathF.Abs(sync.Config.MinGainDb - floor) < 0.01f;
        }

        foreach (var (profile, label) in VolumeProfiles)
        {
            var captured = profile;
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) =>
            {
                sync.Config.Profile = captured;
                sync.Config.Save();
                Refresh();
                sync.SyncNow();
            };
            profileItems.Add((captured, item));
            root.DropDownItems.Add(item);
        }

        root.DropDownItems.Add(new ToolStripSeparator());

        var floorRoot = new ToolStripMenuItem("Floor (volume at 0%)");
        foreach (var floor in GainFloors)
        {
            var captured = floor;
            var item = new ToolStripMenuItem($"{captured:0} dB");
            item.Click += (_, _) =>
            {
                sync.Config.MinGainDb = captured;
                sync.Config.Save();
                Refresh();
                sync.SyncNow();
            };
            floorItems.Add((captured, item));
            floorRoot.DropDownItems.Add(item);
        }
        root.DropDownItems.Add(floorRoot);

        root.DropDownOpening += (_, _) => Refresh();
        Refresh();
        return root;
    }

    // ─── Bus device menu ──────────────────────────────────────────────────────

    /// <summary>Physical Voicemeeter Banana output buses, in menu order.</summary>
    private static readonly (int Bus, string Label)[] PhysicalBuses =
    {
        (0, "A1"), (1, "A2"), (2, "A3"),
    };

    /// <summary>
    /// Builds the "Bus Devices" submenu. Each bus lists the output devices Voicemeeter
    /// currently sees, plus "(none)". The list is rebuilt every time the submenu opens so
    /// devices that just came back appear without restarting the app.
    /// </summary>
    private static ToolStripMenuItem BuildDevicesMenu(SyncApp sync)
    {
        var root = new ToolStripMenuItem("Bus Devices");

        foreach (var (bus, label) in PhysicalBuses)
        {
            var busItem = new ToolStripMenuItem(label);
            int busIndex = bus;

            busItem.DropDownOpening += (_, _) =>
            {
                busItem.DropDownItems.Clear();
                var binding = sync.Config.BindingFor(busIndex);
                var devices = sync.ListOutputDevices();

                var none = new ToolStripMenuItem("(none)")
                {
                    Checked = string.IsNullOrEmpty(binding.Name)
                };
                none.Click += (_, _) =>
                {
                    binding.Name = "";
                    sync.Config.Save();
                    sync.ApplyBinding(binding);
                };
                busItem.DropDownItems.Add(none);

                if (devices.Count == 0)
                {
                    busItem.DropDownItems.Add(new ToolStripMenuItem("Voicemeeter not running")
                    {
                        Enabled = false
                    });
                    return;
                }

                busItem.DropDownItems.Add(new ToolStripSeparator());

                foreach (var device in devices)
                {
                    var captured = device;
                    var item = new ToolStripMenuItem(captured.Display)
                    {
                        Checked = binding.Type == captured.Type && binding.Name == captured.Name
                    };
                    item.Click += (_, _) =>
                    {
                        binding.Type = captured.Type;
                        binding.Name = captured.Name;
                        sync.Config.Save();
                        sync.ApplyBinding(binding);
                    };
                    busItem.DropDownItems.Add(item);
                }
            };

            // A submenu with no items never raises DropDownOpening, so seed a placeholder.
            busItem.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            root.DropDownItems.Add(busItem);
        }

        return root;
    }

    /// <summary>
    /// Generates a small amber speaker icon at runtime (no external .ico required).
    /// </summary>
    private static Icon BuildIcon()
    {
        const int Size = 16;
        using var bmp = new Bitmap(Size, Size);
        using var g = System.Drawing.Graphics.FromImage(bmp);

        g.Clear(Color.Transparent);

        // Dark background square
        g.FillRectangle(new SolidBrush(Color.FromArgb(255, 12, 13, 16)), 0, 0, Size, Size);

        // Three amber vertical bars (EQ style)
        var amber = new SolidBrush(Color.FromArgb(255, 255, 176, 0));
        // bar 1
        g.FillRectangle(amber, 2, 8, 3, 6);
        // bar 2
        g.FillRectangle(amber, 7, 4, 3, 10);
        // bar 3
        g.FillRectangle(amber, 12, 6, 3, 8);

        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>
/// Manages the HKCU Run registry key so the app auto-starts with Windows.
/// Uses the current process exe path, so it works both from the debug folder
/// and from any published/installed location.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "VoicemeeterAgent";
    private const string OldAppName = "AudioRouter.VoicemeeterSync";

    private static string ExePath =>
        System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;

    /// <summary>
    /// One-time migration: if the old "AudioRouter.VoicemeeterSync" Run key exists,
    /// copy it to "VoicemeeterAgent" and remove the old entry so the user doesn't
    /// lose their "Start with Windows" setting after the rename.
    /// </summary>
    public static void MigrateOldKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (key.GetValue(OldAppName) is string oldPath)
        {
            key.SetValue(AppName, oldPath);
            key.DeleteValue(OldAppName, throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(AppName) is string path &&
               string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(AppName, ExePath);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }
}
