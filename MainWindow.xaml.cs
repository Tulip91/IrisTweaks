
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SystemTweaker;

public partial class MainWindow : Window
{
    private const double BaseBodyTextSize = 13;
    private const double BasePageTitleTextSize = 30;
    private const double BasePageSubtitleTextSize = 14;
    private const double BaseControlTextSize = 13;
    private const double BaseCardTitleTextSize = 15;
    private const double BaseCardCategoryTextSize = 11;
    private const double BaseSectionTitleTextSize = 21;

    private sealed record OperationResult(bool Success, string Message);

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
    {
        public static CommandResult Failed(string error) => new(-1, string.Empty, error, false);
        public static CommandResult Timeout() => new(-1, string.Empty, "Command timed out.", true);
    }

    private sealed record ToggleCardSpec(
        string Id,
        string Category,
        string Title,
        string Description,
        string Keywords,
        string? WarningTooltip = null,
        bool ShowCategory = true);

    private sealed record ActionCardSpec(
        string Id,
        string Category,
        string Title,
        string Description,
        string Keywords,
        string ButtonText = "Apply",
        bool Danger = false,
        string? WarningTooltip = null);

    private readonly Dictionary<string, Func<bool, Task<OperationResult>>> _toggleTweaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<Task<OperationResult>>> _actionTweaks = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Button> _navButtons = new();
    private readonly List<Grid> _pages = new();
    private readonly List<Button> _performanceTabButtons = new();
    private readonly List<Grid> _performanceTabPages = new();
    private readonly List<Button> _advancedTabButtons = new();
    private readonly List<Grid> _advancedTabPages = new();

    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly object _logLock = new();

    private CancellationTokenSource? _bannerCts;
    private Grid? _activePage;
    private bool _suppressToggleEvents;
    private bool _settingsReady;
    private bool _searchWatermarkActive;
    private double _startupProgress;

    private readonly string _appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tulip");
    private readonly string _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tulip", "Logs");
    private readonly string _backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tulip", "Backups");
    private readonly string _profileFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tulip", "profile.name");
    private readonly string _logFilePath;
    private static readonly string[] StartupSoundFileNames = ["startup.wav", "startup-calm.wav"];
    private static SoundPlayer? _startupSoundPlayer;
    private string _displayName = "User";

    public MainWindow()
    {
        _logFilePath = Path.Combine(_logDirectory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        InitializeComponent();
        EnsureOutputDirectories();

        _navButtons.AddRange(new[]
        {
            DashboardButton,
            PerformanceButton,
            AdvancedButton,
            NetworkButton,
            CleanupButton,
            PrivacyButton,
            AppsButton,
            ServicesButton,
            SettingsButton
        });

        _pages.AddRange(new[]
        {
            DashboardPage,
            PerformancePage,
            AdvancedPage,
            NetworkPage,
            CleanupPage,
            PrivacyPage,
            AppsPage,
            ServicesPage,
            SettingsPage
        });

        _performanceTabButtons.AddRange(new[]
        {
            GpuTabButton,
            CpuTabButton,
            RamTabButton,
            PeripheralsTabButton,
            StorageTabButton
        });

        _performanceTabPages.AddRange(new[]
        {
            GpuTabContent,
            CpuTabContent,
            RamTabContent,
            PeripheralsTabContent,
            StorageTabContent
        });

        _advancedTabButtons.AddRange(new[]
        {
            AdvancedGeneralTabButton,
            AdvancedMsiTabButton
        });

        _advancedTabPages.AddRange(new[]
        {
            AdvancedGeneralTabContent,
            AdvancedMsiTabContent
        });

        InitializeDefinitions();
        InitializeCards();

        AccentColorCombo.SelectedIndex = 0;
        DensityCombo.SelectedIndex = 0;
        ThemeCombo.SelectedIndex = 0;
        TextScaleSlider.Value = 1;
        ApplyTextScale(1);
        ShowPerformanceTab(GpuTabContent, GpuTabButton);
        ShowAdvancedTab(AdvancedGeneralTabContent, AdvancedGeneralTabButton);
        ShowPage(DashboardPage, DashboardButton);
        InitializeSearchWatermark();
        _settingsReady = true;
    }

    private void EnsureOutputDirectories()
    {
        Directory.CreateDirectory(_appDataDirectory);
        Directory.CreateDirectory(_logDirectory);
        Directory.CreateDirectory(_backupDirectory);
    }

    private static void PlayStartupCompleteSound()
    {
        try
        {
            var soundPath = ResolveStartupSoundPath();
            if (!string.IsNullOrWhiteSpace(soundPath))
            {
                _startupSoundPlayer = new SoundPlayer(soundPath);
                _startupSoundPlayer.Load();
                _startupSoundPlayer.Play();
                return;
            }

            SystemSounds.Asterisk.Play();
        }
        catch
        {
        }
    }

    private static string? ResolveStartupSoundPath()
    {
        foreach (var fileName in StartupSoundFileNames)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private void LoadOrCreateUserProfile()
    {
        var storedName = string.Empty;
        try
        {
            if (File.Exists(_profileFilePath))
            {
                storedName = File.ReadAllText(_profileFilePath).Trim();
            }
        }
        catch
        {
            storedName = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(storedName))
        {
            var suggestedName = string.IsNullOrWhiteSpace(Environment.UserName) ? "User" : Environment.UserName;
            var enteredName = PromptForName(suggestedName);
            storedName = string.IsNullOrWhiteSpace(enteredName) ? suggestedName : enteredName.Trim();
            try
            {
                File.WriteAllText(_profileFilePath, storedName);
            }
            catch
            {
            }
        }

        _displayName = storedName;
        ApplyUserNameToUi();
    }

    private void ApplyUserNameToUi()
    {
        SidebarUserNameText.Text = _displayName;
        StartupWelcomeText.Text = "Welcome to Tulip Tweaks";
        SidebarUserInitialText.Text = char.ToUpperInvariant(_displayName[0]).ToString();
    }

    private string? PromptForName(string defaultName)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Please enter your name:",
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 13
        });

        var inputBox = new TextBox
        {
            Text = defaultName,
            MinWidth = 260
        };
        panel.Children.Add(inputBox);

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 82,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var okButton = new Button
        {
            Content = "Save",
            MinWidth = 82,
            IsDefault = true
        };

        buttonBar.Children.Add(cancelButton);
        buttonBar.Children.Add(okButton);
        panel.Children.Add(buttonBar);

        var dialog = new Window
        {
            Title = "Welcome to Tulip",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = this
        };

        cancelButton.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };

        okButton.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };

        inputBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                dialog.DialogResult = true;
                dialog.Close();
            }
        };

        inputBox.Focus();
        inputBox.SelectAll();
        var result = dialog.ShowDialog();
        return result == true ? inputBox.Text : null;
    }

    private void InitializeSearchWatermark()
    {
        _searchWatermarkActive = true;
        SearchBox.Text = "Search...";
        SearchBox.Foreground = (Brush)Resources["TextMutedBrush"];
    }

    private void UiControlClickSound(object sender, MouseButtonEventArgs e)
    {
        // Custom click/toggle sounds were intentionally removed.
    }

    private void InitializeCards()
    {
        AddToggleCards(NetworkCardsPanel, new[]
        {
            new ToggleCardSpec("network.disableNagle", "Network", "Disable Nagle's Algorithm", "Cuts packet delay for faster response.", "network nagle tcp ack"),
            new ToggleCardSpec("network.optimizeTcpWindowScaling", "Network", "Optimize TCP Window Scaling", "Improves speed on modern connections.", "network tcp window scaling"),
            new ToggleCardSpec("network.reduceThrottlingIndex", "Network", "Reduce Network Throttling Index", "Lowers system packet limits.", "network throttling index"),
            new ToggleCardSpec("network.disableMultimediaThrottling", "Network", "Disable Multimedia Network Throttling", "Removes background network limits.", "network multimedia throttling", "May affect video streaming smoothness."),
            new ToggleCardSpec("network.optimizeDnsCacheSize", "Network", "Optimize DNS Cache Size", "Speeds up repeat DNS lookups.", "network dns cache"),
            new ToggleCardSpec("network.disableQosReservation", "Network", "Disable QoS Bandwidth Reservation", "Reclaims reserved bandwidth.", "network qos reservation"),
            new ToggleCardSpec("network.disableTeredo", "Network", "Disable Teredo", "Turns off Teredo tunneling.", "network teredo", "Can break Xbox network features."),
            new ToggleCardSpec("network.disableNetworkPowerSaving", "Network", "Disable Network Power Saving", "Keeps adapter in high performance mode.", "network power saving", "Uses more battery on laptops.")
        });

        AddActionCards(CleanupCardsPanel, new[]
        {
            new ActionCardSpec("cleanup.clearCache", "Cleanup", "Clear Cache", "Deletes temp files safely.", "cleanup temp cache", "Run"),
            new ActionCardSpec("cleanup.clearChromeTemp", "Cleanup", "Clear Chrome Temp Files", "Removes Chrome cache files.", "cleanup chrome temp", "Run"),
            new ActionCardSpec("cleanup.clearGameTemp", "Cleanup", "Clear Game Temp Files", "Clears DirectX and GPU cache.", "cleanup game temp d3d nvidia", "Run"),
            new ActionCardSpec("cleanup.clearPrefetch", "Cleanup", "Clear Prefetch", "Clears prefetch data.", "cleanup prefetch", "Run", WarningTooltip: "Load times may be slower for a short time."),
            new ActionCardSpec("cleanup.clearRecycleBin", "Cleanup", "Clear Recycle Bin", "Empties recycle bin on all drives.", "cleanup recycle bin", "Run")
        });

        AddToggleCards(PrivacyCardsPanel, new[]
        {
            new ToggleCardSpec("privacy.disableActivityFeed", "Privacy", "Disable Activity Feed", "Stops activity history tracking.", "privacy activity feed"),
            new ToggleCardSpec("privacy.disableAdvertisingId", "Privacy", "Disable Advertising ID", "Blocks ad ID tracking.", "privacy advertising id"),
            new ToggleCardSpec("privacy.disableCeip", "Privacy", "Disable CEIP", "Stops CEIP reports.", "privacy ceip"),
            new ToggleCardSpec("privacy.disableCompatibilityTelemetry", "Privacy", "Disable Compatibility Telemetry", "Reduces compatibility telemetry.", "privacy compatibility telemetry", "Can reduce troubleshooting details."),
            new ToggleCardSpec("privacy.disableDiagnosticCollection", "Privacy", "Disable Diagnostic Data Collection", "Minimizes diagnostics data.", "privacy diagnostics telemetry", "May limit support diagnostics."),
            new ToggleCardSpec("privacy.disableErrorReporting", "Privacy", "Disable Error Reporting", "Stops crash report uploads.", "privacy error reporting"),
            new ToggleCardSpec("privacy.disableFeedbackHub", "Privacy", "Disable Feedback Hub", "Stops feedback prompts.", "privacy feedback hub"),
            new ToggleCardSpec("privacy.disableLocationTracking", "Privacy", "Disable Location Tracking", "Stops location access.", "privacy location tracking"),
            new ToggleCardSpec("privacy.disableTimelineTracking", "Privacy", "Disable Timeline Tracking", "Disables timeline sync.", "privacy timeline"),
            new ToggleCardSpec("privacy.disableRemoteAssistance", "Privacy", "Disable Remote Assistance", "Turns off remote help.", "privacy remote assistance", "Remote help sessions will not work.")
        });

        AddActionCards(AppsCardsPanel, new[]
        {
            new ActionCardSpec("apps.disableOneDrive", "Apps", "Disable OneDrive", "Removes OneDrive and blocks it.", "apps onedrive disable"),
            new ActionCardSpec("apps.removeXboxApps", "Apps", "Remove Xbox Apps", "Removes Xbox apps.", "apps xbox remove"),
            new ActionCardSpec("apps.disableCortana", "Apps", "Disable Cortana", "Turns off Cortana.", "apps cortana disable"),
            new ActionCardSpec("apps.removeMixedReality", "Apps", "Remove Mixed Reality Portal", "Removes Mixed Reality Portal.", "apps mixed reality portal remove"),
            new ActionCardSpec("apps.disableTeamsAutostart", "Apps", "Disable Teams Auto-start", "Stops Teams from auto launch.", "apps teams startup disable"),
            new ActionCardSpec("apps.removeConsumerBloatware", "Apps", "Remove Consumer Bloatware", "Removes common preinstalled extras.", "apps consumer bloatware")
        });

        AddToggleCards(ServicesCardsPanel, new[]
        {
            new ToggleCardSpec("services.disableXbox", "Services", "Disable Xbox Services", "Turns off Xbox background services.", "services xbox"),
            new ToggleCardSpec("services.disablePrintSpooler", "Services", "Disable Print Spooler", "Stops print spooler.", "services print spooler", "Printing will not work."),
            new ToggleCardSpec("services.disableRemoteRegistry", "Services", "Disable Remote Registry", "Turns off remote registry.", "services remote registry", "Remote registry tools will fail."),
            new ToggleCardSpec("services.disableSearch", "Services", "Disable Windows Search Indexing", "Stops search indexing service.", "services search indexing"),
            new ToggleCardSpec("services.disableSysMain", "Services", "Disable SysMain", "Stops SysMain.", "services sysmain"),
            new ToggleCardSpec("services.disableDiagnostic", "Services", "Disable Diagnostic Services", "Stops diagnostic services.", "services diagnostic", "Troubleshooting data will be limited.")
        });

        AddToggleCards(AdvancedGeneralCardsPanel, new[]
        {
            new ToggleCardSpec("network.enableCommunicationPorts", "Advanced", "Enable Communication Ports", "Enables serial and communication class services.", "advanced communication ports serial"),
            new ToggleCardSpec("network.enableApic", "Advanced", "Enable APIC", "Enables x2APIC policy for interrupt handling.", "advanced apic interrupt", "Requires reboot to fully apply."),
            new ToggleCardSpec("network.enableHpet", "Advanced", "Enable High Precision Event Timer", "Uses platform clock timing for stable polling.", "advanced hpet timer", "May alter timer behavior in some games."),
            new ToggleCardSpec("network.enableRss", "Advanced", "Enable Receive Side Scaling", "Distributes network interrupts across CPU cores.", "advanced rss scaling network"),
            new ToggleCardSpec("network.disableSerialPorts", "Advanced", "Disable Serial Ports", "Disables legacy serial port driver services.", "advanced serial ports disable", "Can break software that relies on COM ports.")
        });

        AddToggleCards(AdvancedMsiCardsPanel, new[]
        {
            new ToggleCardSpec("network.enableMsiMode", "Network Adapter", "Enable MSI Mode", "Turns on Message Signaled Interrupt mode for compatible devices.", "advanced msi mode network adapter"),
            new ToggleCardSpec("network.enableMsiOperationType", "Network Adapter", "Enable MSI Operation Type", "Sets MSI interrupt operation policy for device compatibility.", "advanced msi operation type network")
        });

        AddActionCards(AdvancedMsiCardsPanel, new[]
        {
            new ActionCardSpec("perf.msi.enableForDevices", "All Components", "Enable MSI Mode For Compatible Devices", "Bulk-enables MSI mode on compatible PCI/USB device entries.", "advanced msi mode all components devices", "Apply", WarningTooltip: "May require reboot and can affect unsupported devices."),
            new ActionCardSpec("perf.usb.optimizeController", "Controller", "Optimize USB Ports & Drivers for Controller", "Applies USB and interrupt tweaks aimed at stable controller latency.", "advanced msi controller usb optimize low latency", "Apply"),
            new ActionCardSpec("perf.usb.optimizeKeyboardMouse", "Keyboard & Mouse", "Optimize USB Ports & Drivers for Keyboard & Mouse", "Applies USB and input tweaks aimed at lowest keyboard/mouse latency.", "advanced msi keyboard mouse usb optimize low latency", "Apply")
        });

        AddToggleCards(GpuCardsPanel, new[]
        {
            new ToggleCardSpec("perf.gpu.hags", "GPU", "Enable Hardware-Accelerated GPU Scheduling", "Can lower render latency.", "performance gpu hags", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.disableFullscreenOpt", "GPU", "Disable Fullscreen Optimizations", "Prefers true fullscreen mode.", "performance gpu fullscreen", "Can reduce overlay compatibility.", false),
            new ToggleCardSpec("perf.gpu.maxPerfPolicy", "GPU", "Prefer Maximum GPU Throughput", "Keeps GPU in performance mode.", "performance gpu power", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.disableMpo", "GPU", "Disable Multi-Plane Overlay (MPO)", "Helps with flicker issues.", "performance gpu mpo", "May affect HDR and power usage.", false),
            new ToggleCardSpec("perf.gpu.disableDriverUpdates", "GPU", "Disable GeForce Driver Update", "Stops NVIDIA scheduled update checks.", "nvidia driver update disable", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.disableHdcp", "GPU", "Disable HDCP", "Disables HDCP policy for lower display handshake overhead.", "nvidia hdcp drm disable", "DRM playback may stop working.", false),
            new ToggleCardSpec("perf.gpu.disableDriverLogging", "GPU", "Disable NVIDIA Driver Logging", "Reduces NVIDIA event and telemetry log writes.", "nvidia logging telemetry disable", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.disableDmaRemapping", "GPU", "Disable NVIDIA DMA Remapping", "Applies low-latency DMA remap policy overrides.", "nvidia dma remapping disable", "May affect compatibility on some systems.", false),
            new ToggleCardSpec("perf.gpu.disableUvm", "GPU", "Disable NVIDIA UVM", "Disables NVIDIA UVM policy path to reduce overhead.", "nvidia uvm disable", "Some CUDA workloads may fail.", false),
            new ToggleCardSpec("perf.gpu.forceContiguousMemory", "GPU", "Force Contiguous Memory Allocation", "Prioritizes contiguous allocation policy for GPU memory usage.", "nvidia contiguous memory allocation", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.optimizeIdleThresholds", "GPU", "Optimize GPU Idle Thresholds", "Keeps GPU performance states active for longer.", "nvidia gpu idle thresholds", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.optimizeMemoryLatency", "GPU", "Optimize Memory Latency Settings", "Applies aggressive GPU memory latency policy values.", "nvidia memory latency settings", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.optimizeDirectFlipVrr", "GPU", "Optimize NVIDIA Direct Flip & VRR", "Tunes Direct Flip/VRR-related game configuration flags.", "nvidia direct flip vrr optimize", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.optimizeFrameScheduling", "GPU", "Optimize NVIDIA Frame Scheduling", "Adjusts driver scheduling behavior for frame consistency.", "nvidia frame scheduling optimize", ShowCategory: false),
            new ToggleCardSpec("perf.gpu.optimizeGeForceExperience", "GPU", "Optimize NVIDIA GeForce Experience", "Disables unneeded telemetry/update components.", "nvidia geforce experience optimize", "May disable some NVIDIA app features.", false)
        });
        AddActionCards(GpuCardsPanel, new[]
        {
            new ActionCardSpec("profile.nvidiaLaptop", "NVIDIA", "EXM Laptop Nvidia Profile", "Applies NVIDIA and Windows tuning for low-latency laptop operation.", "nvidia profile laptop low latency", "Apply"),
            new ActionCardSpec("profile.nvidiaDesktop", "NVIDIA", "EXM Desktop Nvidia Profile", "Applies aggressive desktop-focused NVIDIA latency/performance tuning.", "nvidia profile desktop max performance", "Apply")
        });

        AddToggleCards(CpuCardsPanel, new[]
        {
            new ToggleCardSpec("perf.cpu.disableCoreParking", "CPU", "Disable Core Parking", "Keeps more cores ready.", "performance cpu core parking", ShowCategory: false),
            new ToggleCardSpec("perf.cpu.disablePowerThrottling", "CPU", "Disable CPU Power Throttling", "Reduces clock throttling.", "performance cpu throttling", "Can raise temps and power draw.", false),
            new ToggleCardSpec("perf.cpu.ultimatePlan", "CPU", "Use Ultimate Performance Plan", "Switches to max performance plan.", "performance cpu ultimate plan", ShowCategory: false),
            new ToggleCardSpec("perf.cpu.vendorAwareScheduler", "CPU", "Apply Vendor-Aware Scheduler Optimization", "Auto tunes for AMD or Intel.", "performance cpu vendor amd intel", ShowCategory: false),
            new ToggleCardSpec("perf.cpu.disableBasicCStates", "CPU", "Disable Basic C-states", "Keeps cores in active states to reduce wake latency.", "cpu c states disable", "Can increase heat and power draw.", false),
            new ToggleCardSpec("perf.cpu.disableCoalescableTimer", "CPU", "Disable Coalescable Timer", "Disables dynamic timer coalescing behavior.", "cpu coalescable timer disable", "Requires reboot to fully apply.", false),
            new ToggleCardSpec("perf.cpu.disableModernStandby", "CPU", "Disable Modern Standby", "Disables Modern Standby low-power connected mode.", "cpu modern standby disable", "Sleep behavior may change after reboot.", false),
            new ToggleCardSpec("perf.cpu.setEnergyPerfPreference", "CPU", "Set Energy Performance Preference", "Prioritizes performance over energy saving.", "cpu energy performance preference", ShowCategory: false),
            new ToggleCardSpec("perf.cpu.setMinMaxProcessorState", "CPU", "Set Minimum and Maximum Processor State", "Locks processor state to 100%/100%.", "cpu min max processor state", "Can increase idle power usage.", false)
        });
        AddToggleCards(RamCardsPanel, new[]
        {
            new ToggleCardSpec("perf.ram.disableCompression", "RAM", "Disable Memory Compression", "Disables memory compression.", "performance ram compression", ShowCategory: false),
            new ToggleCardSpec("perf.ram.clearPagefileOnShutdown", "RAM", "Clear Pagefile On Shutdown", "Clears pagefile on shutdown.", "performance ram pagefile", "Can make shutdown slower.", false),
            new ToggleCardSpec("perf.ram.disablePrefetch", "RAM", "Disable Prefetch", "Turns off prefetch.", "performance ram prefetch", ShowCategory: false),
            new ToggleCardSpec("perf.ram.optimizeSvchostSplit", "RAM", "Optimize Svchost Grouping", "Optimizes service grouping.", "performance ram svchost", ShowCategory: false),
            new ToggleCardSpec("perf.ram.disableRamDiagnostics", "RAM", "Disable RAM Diagnostics", "Disables scheduled memory diagnostics tasks.", "ram diagnostics disable", ShowCategory: false),
            new ToggleCardSpec("perf.ram.enableSuperfetch", "RAM", "Enable Superfetch", "Turns SysMain on for app preload behavior.", "ram superfetch sysmain enable", ShowCategory: false)
        });

        AddToggleCards(PeripheralsCardsPanel, new[]
        {
            new ToggleCardSpec("perf.peripherals.disableIdleSleepStates", "Peripherals", "Disable Idle and Sleep States", "Prevents low-power sleep timeouts from triggering.", "keyboard mouse idle sleep states", ShowCategory: false),
            new ToggleCardSpec("perf.peripherals.disableMouseAccel", "Peripherals", "Disable Mouse Acceleration", "Sets 1:1 mouse movement.", "performance mouse acceleration", ShowCategory: false),
            new ToggleCardSpec("perf.peripherals.enablePixelPerfectMouse", "Peripherals", "Enable 1:1 Pixel Mouse Movement", "Applies raw 1:1 cursor movement values.", "pixel perfect mouse movement 1:1", ShowCategory: false),
            new ToggleCardSpec("perf.peripherals.disableUsbSelectiveSuspend", "Peripherals", "Disable USB Selective Suspend", "Keeps USB devices active.", "performance usb selective suspend", "Uses more battery.", false),
            new ToggleCardSpec("perf.peripherals.disableStickyShortcut", "Peripherals", "Disable Sticky/Toggle Key Shortcuts", "Stops sticky key popups.", "performance sticky keys", ShowCategory: false),
            new ToggleCardSpec("perf.peripherals.disableStickyKeys", "Peripherals", "Disable Sticky Keys", "Disables Sticky Keys shortcut behavior.", "keyboard sticky keys disable", ShowCategory: false),
            new ToggleCardSpec("perf.peripherals.disableToggleKeys", "Peripherals", "Disable Toggle Keys", "Disables Toggle Keys beep shortcuts.", "keyboard toggle keys disable", ShowCategory: false),
            new ToggleCardSpec("perf.peripherals.reduceKeyboardRepeatDelay", "Peripherals", "Reduce Keyboard Repeat Delay", "Makes held key repeat respond faster.", "keyboard repeat delay speed", ShowCategory: false),
            new ToggleCardSpec("perf.peripherals.setDebugPollInterval", "Peripherals", "Set Debug Poll Interval", "Sets kernel debug polling interval to 1000 ms.", "debug poll interval kernel", ShowCategory: false),
            new ToggleCardSpec("perf.peripherals.disableGameBar", "Peripherals", "Disable Game Bar Background Capture", "Turns off Game DVR capture.", "performance game bar dvr", "Built-in capture will stop.", false)
        });

        AddToggleCards(StorageCardsPanel, new[]
        {
            new ToggleCardSpec("perf.storage.disableLastAccess", "Storage", "Disable NTFS Last Access Updates", "Reduces extra disk writes.", "performance storage last access", ShowCategory: false),
            new ToggleCardSpec("perf.storage.disableHibernation", "Storage", "Disable Hibernation", "Removes hiberfile and hibernate.", "performance storage hibernation", "Disables Fast Startup.", false),
            new ToggleCardSpec("perf.storage.enableTrim", "Storage", "Enable TRIM", "Keeps SSD cleanup enabled.", "performance storage trim", ShowCategory: false),
            new ToggleCardSpec("perf.storage.disableStorageSense", "Storage", "Disable Storage Sense Automation", "Stops automatic storage cleanup.", "performance storage sense", ShowCategory: false),
            new ToggleCardSpec("perf.storage.disableDipmParking", "Storage", "Disable DIPM Parking", "Disables Device-Initiated Link Power Management parking.", "storage dipm parking disable", ShowCategory: false),
            new ToggleCardSpec("perf.storage.disableHddParking", "Storage", "Disable HDD Parking", "Prevents aggressive disk park timeout behavior.", "storage hdd parking disable", ShowCategory: false),
            new ToggleCardSpec("perf.storage.disableHipmParking", "Storage", "Disable HIPM Parking", "Disables Host-Initiated Link Power Management parking.", "storage hipm parking disable", ShowCategory: false),
            new ToggleCardSpec("perf.storage.disableSsdPowersaving", "Storage", "Disable SSD Powersaving", "Disables SSD low-power modes where supported.", "storage ssd powersaving disable", ShowCategory: false),
            new ToggleCardSpec("perf.storage.disableWriteCacheFlush", "Storage", "Disable Write Cache Buffer Flushing", "Disables OS flush-buffer behavior for storage writes.", "storage write cache flush disable", "Can risk data integrity on power loss.", false),
            new ToggleCardSpec("perf.storage.optimizeSsdSleep", "Storage", "Optimize SSD Sleep", "Tunes SSD and disk idle settings for faster wake-up.", "storage ssd sleep optimize", ShowCategory: false)
        });
    }

    private void AddToggleCards(Panel panel, IEnumerable<ToggleCardSpec> specs)
    {
        foreach (var spec in specs)
        {
            panel.Children.Add(CreateToggleCard(spec));
        }
    }

    private void AddActionCards(Panel panel, IEnumerable<ActionCardSpec> specs)
    {
        foreach (var spec in specs)
        {
            panel.Children.Add(CreateActionCard(spec));
        }
    }

    private Border CreateToggleCard(ToggleCardSpec spec)
    {
        var isPerformanceCard = spec.Id.StartsWith("perf.", StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Style = (Style)Resources[isPerformanceCard ? "PerformanceTweakCardStyle" : "TweakCardStyle"],
            Tag = spec.Keywords
        };

        var root = new Grid();
        var row = 0;
        if (spec.ShowCategory)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var categoryText = new TextBlock
            {
                Text = spec.Category,
                Foreground = (Brush)Resources["AccentBrush"],
                FontWeight = FontWeights.SemiBold
            };
            categoryText.SetResourceReference(TextBlock.FontSizeProperty, "CardCategoryTextSize");
            Grid.SetRow(categoryText, row++);
            root.Children.Add(categoryText);
        }

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleText = new TextBlock
        {
            Text = spec.Title,
            Foreground = (Brush)Resources["TextPrimaryBrush"],
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 20,
            MaxHeight = isPerformanceCard ? 40 : 60
        };
        titleText.SetResourceReference(TextBlock.FontSizeProperty, "CardTitleTextSize");
        titleText.Margin = spec.ShowCategory ? new Thickness(0, 8, 0, 0) : new Thickness(0, 0, 0, 0);
        Grid.SetRow(titleText, row++);
        root.Children.Add(titleText);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var descriptionText = new TextBlock
        {
            Text = spec.Description,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (Brush)Resources["TextSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.WordEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 18,
            MaxHeight = isPerformanceCard ? 36 : 72
        };
        descriptionText.SetResourceReference(TextBlock.FontSizeProperty, "BodyTextSize");
        Grid.SetRow(descriptionText, row++);
        root.Children.Add(descriptionText);

        if (!string.IsNullOrWhiteSpace(spec.WarningTooltip))
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var warningChip = CreateWarningChip(spec.WarningTooltip!, isPerformanceCard);
            warningChip.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(warningChip, row++);
            root.Children.Add(warningChip);
        }

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        row++;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toggle = new ToggleButton
        {
            Style = (Style)Resources["SwitchToggleStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Tag = spec.Id,
            CommandParameter = spec.Title
        };
        toggle.Checked += TweakToggle_Checked;
        toggle.Unchecked += TweakToggle_Unchecked;
        Grid.SetRow(toggle, row);
        root.Children.Add(toggle);

        card.Child = root;
        return card;
    }

    private Border CreateActionCard(ActionCardSpec spec)
    {
        var card = new Border
        {
            Style = (Style)Resources["TweakCardStyle"],
            Tag = spec.Keywords
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var categoryText = new TextBlock
        {
            Text = spec.Category,
            Foreground = (Brush)Resources["AccentBrush"],
            FontWeight = FontWeights.SemiBold
        };
        categoryText.SetResourceReference(TextBlock.FontSizeProperty, "CardCategoryTextSize");
        Grid.SetRow(categoryText, 0);
        root.Children.Add(categoryText);

        var titleRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        var titleText = new TextBlock
        {
            Text = spec.Title,
            Foreground = (Brush)Resources["TextPrimaryBrush"],
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        titleText.SetResourceReference(TextBlock.FontSizeProperty, "CardTitleTextSize");
        titleRow.Children.Add(titleText);

        if (!string.IsNullOrWhiteSpace(spec.WarningTooltip))
        {
            titleRow.Children.Add(CreateWarningChip(spec.WarningTooltip!));
        }

        Grid.SetRow(titleRow, 1);
        root.Children.Add(titleRow);

        var descriptionText = new TextBlock
        {
            Text = spec.Description,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (Brush)Resources["TextSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        descriptionText.SetResourceReference(TextBlock.FontSizeProperty, "BodyTextSize");
        Grid.SetRow(descriptionText, 2);
        root.Children.Add(descriptionText);

        var button = new Button
        {
            Content = spec.ButtonText,
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 0),
            Style = (Style)Resources["SecondaryButtonStyle"],
            Tag = spec.Id,
            CommandParameter = spec.Title
        };

        if (spec.Danger)
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B1626"));
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6A203E"));
        }

        button.Click += ActionTweak_Click;
        Grid.SetRow(button, 3);
        root.Children.Add(button);

        card.Child = root;
        return card;
    }

    private Border CreateWarningChip(string tooltip, bool compact = false)
    {
        var fontSize = compact ? 10d : 11d;
        var chip = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B2A08")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A6D18")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = compact ? new Thickness(7, 2, 7, 2) : new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Click to view warning details.",
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Tag = tooltip
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new TextBlock { Text = "\u26A0", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8C54F")), FontSize = fontSize, Margin = new Thickness(0, 0, 4, 0) });
        stack.Children.Add(new TextBlock { Text = "Warning", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8C54F")), FontSize = fontSize, FontWeight = FontWeights.SemiBold });
        chip.Child = stack;
        chip.MouseEnter += WarningChip_MouseEnter;
        chip.MouseLeave += WarningChip_MouseLeave;
        chip.MouseLeftButtonDown += WarningChip_MouseLeftButtonDown;
        return chip;
    }

    private static void AnimateWarningChipScale(Border chip, double scale, int durationMs)
    {
        if (chip.RenderTransform is not ScaleTransform transform)
        {
            return;
        }

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = easing });
        transform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = easing });
    }

    private void WarningChip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border chip)
        {
            AnimateWarningChipScale(chip, 1.02, 110);
        }
    }

    private void WarningChip_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border chip)
        {
            AnimateWarningChipScale(chip, 1.0, 130);
        }
    }

    private void WarningChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border chip || chip.Tag is not string warningText)
        {
            return;
        }

        ShowWarningInfoOverlay(warningText);
    }

    private void InitializeDefinitions()
    {
        _toggleTweaks["network.disableNagle"] = ApplyNagleAlgorithmAsync;
        _toggleTweaks["network.optimizeTcpWindowScaling"] = ApplyTcpWindowScalingAsync;
        _toggleTweaks["network.reduceThrottlingIndex"] = ApplyNetworkThrottlingAsync;
        _toggleTweaks["network.disableMultimediaThrottling"] = ApplyMultimediaThrottlingAsync;
        _toggleTweaks["network.optimizeDnsCacheSize"] = ApplyDnsCacheAsync;
        _toggleTweaks["network.disableQosReservation"] = ApplyQosReservationAsync;
        _toggleTweaks["network.disableTeredo"] = ApplyTeredoAsync;
        _toggleTweaks["network.disableNetworkPowerSaving"] = ApplyNetworkPowerSavingAsync;
        _toggleTweaks["network.enableCommunicationPorts"] = ApplyCommunicationPortsAsync;
        _toggleTweaks["network.enableApic"] = ApplyApicAsync;
        _toggleTweaks["network.enableHpet"] = ApplyHpetAsync;
        _toggleTweaks["network.enableMsiMode"] = ApplyMsiModeAsync;
        _toggleTweaks["network.enableMsiOperationType"] = ApplyMsiOperationTypeAsync;
        _toggleTweaks["network.enableRss"] = ApplyRssAsync;
        _toggleTweaks["network.disableSerialPorts"] = ApplySerialPortsAsync;

        _toggleTweaks["privacy.disableActivityFeed"] = ApplyActivityFeedAsync;
        _toggleTweaks["privacy.disableAdvertisingId"] = ApplyAdvertisingIdAsync;
        _toggleTweaks["privacy.disableCeip"] = ApplyCeipAsync;
        _toggleTweaks["privacy.disableCompatibilityTelemetry"] = ApplyCompatibilityTelemetryAsync;
        _toggleTweaks["privacy.disableDiagnosticCollection"] = ApplyDiagnosticCollectionAsync;
        _toggleTweaks["privacy.disableErrorReporting"] = ApplyErrorReportingAsync;
        _toggleTweaks["privacy.disableFeedbackHub"] = ApplyFeedbackHubAsync;
        _toggleTweaks["privacy.disableLocationTracking"] = ApplyLocationTrackingAsync;
        _toggleTweaks["privacy.disableTimelineTracking"] = ApplyTimelineTrackingAsync;
        _toggleTweaks["privacy.disableRemoteAssistance"] = ApplyRemoteAssistanceAsync;

        _toggleTweaks["services.disableXbox"] = ApplyXboxServicesAsync;
        _toggleTweaks["services.disablePrintSpooler"] = enabled => SetServiceStateAsync("Spooler", enabled, "auto");
        _toggleTweaks["services.disableRemoteRegistry"] = enabled => SetServiceStateAsync("RemoteRegistry", enabled, "demand");
        _toggleTweaks["services.disableSearch"] = enabled => SetServiceStateAsync("WSearch", enabled, "auto");
        _toggleTweaks["services.disableSysMain"] = enabled => SetServiceStateAsync("SysMain", enabled, "auto");
        _toggleTweaks["services.disableDiagnostic"] = ApplyDiagnosticServicesAsync;

        _toggleTweaks["perf.gpu.hags"] = ApplyHagsAsync;
        _toggleTweaks["perf.gpu.disableFullscreenOpt"] = ApplyFullscreenOptimizationsAsync;
        _toggleTweaks["perf.gpu.maxPerfPolicy"] = ApplyGpuPowerPolicyAsync;
        _toggleTweaks["perf.gpu.disableMpo"] = ApplyMpoAsync;
        _toggleTweaks["perf.gpu.disableDriverUpdates"] = ApplyDisableGeForceDriverUpdatesAsync;
        _toggleTweaks["perf.gpu.disableHdcp"] = ApplyDisableHdcpAsync;
        _toggleTweaks["perf.gpu.disableDriverLogging"] = ApplyDisableNvidiaLoggingAsync;
        _toggleTweaks["perf.gpu.disableDmaRemapping"] = ApplyDisableNvidiaDmaRemappingAsync;
        _toggleTweaks["perf.gpu.disableUvm"] = ApplyDisableNvidiaUvmAsync;
        _toggleTweaks["perf.gpu.forceContiguousMemory"] = ApplyForceContiguousMemoryAllocationAsync;
        _toggleTweaks["perf.gpu.optimizeIdleThresholds"] = ApplyOptimizeGpuIdleThresholdsAsync;
        _toggleTweaks["perf.gpu.optimizeMemoryLatency"] = ApplyOptimizeNvidiaMemoryLatencyAsync;
        _toggleTweaks["perf.gpu.optimizeDirectFlipVrr"] = ApplyOptimizeDirectFlipVrrAsync;
        _toggleTweaks["perf.gpu.optimizeFrameScheduling"] = ApplyOptimizeFrameSchedulingAsync;
        _toggleTweaks["perf.gpu.optimizeGeForceExperience"] = ApplyOptimizeGeForceExperienceAsync;

        _toggleTweaks["perf.cpu.disableCoreParking"] = ApplyCoreParkingAsync;
        _toggleTweaks["perf.cpu.disablePowerThrottling"] = ApplyCpuPowerThrottlingAsync;
        _toggleTweaks["perf.cpu.ultimatePlan"] = ApplyUltimatePlanToggleAsync;
        _toggleTweaks["perf.cpu.vendorAwareScheduler"] = ApplyVendorAwareSchedulerAsync;
        _toggleTweaks["perf.cpu.disableBasicCStates"] = ApplyBasicCStatesAsync;
        _toggleTweaks["perf.cpu.disableCoalescableTimer"] = ApplyCoalescableTimerAsync;
        _toggleTweaks["perf.cpu.disableModernStandby"] = ApplyModernStandbyAsync;
        _toggleTweaks["perf.cpu.setEnergyPerfPreference"] = ApplyEnergyPerformancePreferenceAsync;
        _toggleTweaks["perf.cpu.setMinMaxProcessorState"] = ApplyMinMaxProcessorStateAsync;

        _toggleTweaks["perf.ram.disableCompression"] = ApplyMemoryCompressionAsync;
        _toggleTweaks["perf.ram.clearPagefileOnShutdown"] = ApplyClearPagefileAsync;
        _toggleTweaks["perf.ram.disablePrefetch"] = ApplyPrefetchAsync;
        _toggleTweaks["perf.ram.optimizeSvchostSplit"] = ApplySvchostSplitAsync;
        _toggleTweaks["perf.ram.disableRamDiagnostics"] = ApplyRamDiagnosticsAsync;
        _toggleTweaks["perf.ram.enableSuperfetch"] = ApplySuperfetchAsync;

        _toggleTweaks["perf.peripherals.disableIdleSleepStates"] = ApplyIdleSleepStatesAsync;
        _toggleTweaks["perf.peripherals.disableMouseAccel"] = ApplyMouseAccelerationAsync;
        _toggleTweaks["perf.peripherals.enablePixelPerfectMouse"] = ApplyPixelPerfectMouseAsync;
        _toggleTweaks["perf.peripherals.disableUsbSelectiveSuspend"] = ApplyUsbSelectiveSuspendAsync;
        _toggleTweaks["perf.peripherals.disableStickyShortcut"] = ApplyStickyShortcutAsync;
        _toggleTweaks["perf.peripherals.disableStickyKeys"] = ApplyStickyKeysAsync;
        _toggleTweaks["perf.peripherals.disableToggleKeys"] = ApplyToggleKeysAsync;
        _toggleTweaks["perf.peripherals.reduceKeyboardRepeatDelay"] = ApplyKeyboardRepeatDelayAsync;
        _toggleTweaks["perf.peripherals.setDebugPollInterval"] = ApplyDebugPollIntervalAsync;
        _toggleTweaks["perf.peripherals.disableGameBar"] = ApplyGameBarAsync;

        _toggleTweaks["perf.storage.disableLastAccess"] = ApplyLastAccessAsync;
        _toggleTweaks["perf.storage.disableHibernation"] = ApplyHibernationAsync;
        _toggleTweaks["perf.storage.enableTrim"] = ApplyTrimAsync;
        _toggleTweaks["perf.storage.disableStorageSense"] = ApplyStorageSenseAsync;
        _toggleTweaks["perf.storage.disableDipmParking"] = ApplyDipmParkingAsync;
        _toggleTweaks["perf.storage.disableHddParking"] = ApplyHddParkingAsync;
        _toggleTweaks["perf.storage.disableHipmParking"] = ApplyHipmParkingAsync;
        _toggleTweaks["perf.storage.disableSsdPowersaving"] = ApplySsdPowersavingAsync;
        _toggleTweaks["perf.storage.disableWriteCacheFlush"] = ApplyWriteCacheFlushAsync;
        _toggleTweaks["perf.storage.optimizeSsdSleep"] = ApplySsdSleepAsync;

        _actionTweaks["cleanup.clearCache"] = ClearTempCacheAsync;
        _actionTweaks["cleanup.clearChromeTemp"] = ClearChromeTempAsync;
        _actionTweaks["cleanup.clearGameTemp"] = ClearGameTempAsync;
        _actionTweaks["cleanup.clearPrefetch"] = ClearPrefetchAsync;
        _actionTweaks["cleanup.clearRecycleBin"] = ClearRecycleBinAsync;
        _actionTweaks["cleanup.startCleaning"] = StartCleaningAsync;

        _actionTweaks["quick.quickScan"] = RunQuickScanAsync;
        _actionTweaks["quick.restoreDefaults"] = RestoreDefaultsAsync;
        _actionTweaks["quick.createRestorePoint"] = CreateRestorePointAsync;
        _actionTweaks["quick.exportBackup"] = ExportBackupAsync;
        _actionTweaks["quick.openBackupFolder"] = OpenBackupFolderAsync;
        _actionTweaks["quick.openLogs"] = OpenLogsFolderAsync;
        _actionTweaks["quick.flushDns"] = FlushDnsAsync;
        _actionTweaks["quick.restartExplorer"] = RestartExplorerAsync;

        _actionTweaks["apps.disableOneDrive"] = DisableOneDriveAsync;
        _actionTweaks["apps.removeXboxApps"] = RemoveXboxAppsAsync;
        _actionTweaks["apps.disableCortana"] = DisableCortanaAsync;
        _actionTweaks["apps.removeMixedReality"] = RemoveMixedRealityAsync;
        _actionTweaks["apps.disableTeamsAutostart"] = DisableTeamsAutostartAsync;
        _actionTweaks["apps.removeConsumerBloatware"] = RemoveConsumerBloatwareAsync;

        _actionTweaks["profile.lowLatency"] = ApplyLowLatencyProfileAsync;
        _actionTweaks["profile.balanced"] = ApplyBalancedProfileAsync;
        _actionTweaks["profile.highPerformance"] = ApplyHighPerformanceProfileAsync;
        _actionTweaks["profile.nvidiaLaptop"] = ApplyNvidiaLaptopProfileAsync;
        _actionTweaks["profile.nvidiaDesktop"] = ApplyNvidiaDesktopProfileAsync;
        _actionTweaks["perf.usb.optimizeController"] = OptimizeUsbControllerAsync;
        _actionTweaks["perf.usb.optimizeKeyboardMouse"] = OptimizeUsbKeyboardMouseAsync;
        _actionTweaks["perf.msi.enableForDevices"] = EnableMsiForCompatibleDevicesAsync;
    }
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunStartupPipelineAsync();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string pageName || FindName(pageName) is not Grid page)
        {
            return;
        }

        ShowPage(page, button);
    }

    private void PerformanceTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string pageName || FindName(pageName) is not Grid page)
        {
            return;
        }

        ShowPerformanceTab(page, button);
    }

    private void AdvancedTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string pageName || FindName(pageName) is not Grid page)
        {
            return;
        }

        ShowAdvancedTab(page, button);
    }

    private async void TweakToggle_Checked(object sender, RoutedEventArgs e)
    {
        await HandleToggleChangedAsync(sender, true);
    }

    private async void TweakToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        await HandleToggleChangedAsync(sender, false);
    }

    private async Task HandleToggleChangedAsync(object sender, bool enabled)
    {
        if (_suppressToggleEvents || sender is not ToggleButton toggle || toggle.Tag is not string id)
        {
            return;
        }

        if (!_toggleTweaks.TryGetValue(id, out var operation))
        {
            await ShowBannerAsync("Unknown tweak requested.", false);
            return;
        }

        var operationName = toggle.CommandParameter?.ToString() ?? id;
        toggle.IsEnabled = false;

        try
        {
            var result = await ExecuteActionAsync(operationName, () => operation(enabled));
            if (!result.Success)
            {
                _suppressToggleEvents = true;
                toggle.IsChecked = !enabled;
                _suppressToggleEvents = false;
            }
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    private async void ActionTweak_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        if (!_actionTweaks.TryGetValue(id, out var operation))
        {
            await ShowBannerAsync("Unknown action requested.", false);
            return;
        }

        var operationName = button.CommandParameter?.ToString() ?? id;
        await ExecuteActionAsync(operationName, operation);
    }

    private async void ProfileApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id || !_actionTweaks.TryGetValue(id, out var operation))
        {
            await ShowBannerAsync("Profile action unavailable.", false);
            return;
        }

        var operationName = button.CommandParameter?.ToString() ?? "Profile";
        await ExecuteActionAsync(operationName, operation);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchWatermarkActive)
        {
            return;
        }

        ApplySearchFilter(SearchBox.Text);
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!_searchWatermarkActive)
        {
            return;
        }

        _searchWatermarkActive = false;
        SearchBox.Text = string.Empty;
        SearchBox.Foreground = (Brush)Resources["TextPrimaryBrush"];
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            return;
        }

        _searchWatermarkActive = true;
        SearchBox.Text = "Search...";
        SearchBox.Foreground = (Brush)Resources["TextMutedBrush"];
    }

    private void AccentColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        var selected = (AccentColorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Violet";
        ApplyAccent(selected);
    }

    private void TextScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsReady)
        {
            return;
        }

        ApplyTextScale(TextScaleSlider.Value);
    }

    private void DensityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        var selected = (DensityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced";
        ApplyDensity(selected);
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        var selected = (ThemeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Midnight";
        ApplyTheme(selected);
    }

    private async Task RunStartupPipelineAsync()
    {
        LoadOrCreateUserProfile();
        _startupProgress = 0;
        StartupProgressBar.Value = 0;

        await RunStartupStepAsync("Authorizing user...", 15, async () =>
        {
            var isAdmin = IsAdministrator();
            SidebarAdminStateText.Text = isAdmin ? "Yes" : "No";
            AdminValueText.Text = isAdmin ? "Administrator" : "Standard User";
            await Task.Delay(180);
        });

        await RunStartupStepAsync("Loading modules...", 42, async () =>
        {
            await Task.Delay(200);
        });

        await RunStartupStepAsync("Scanning system snapshot...", 76, LoadSystemSnapshotAsync);

        await RunStartupStepAsync("Preparing interface...", 100, async () =>
        {
            ApplyTheme((ThemeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Midnight");
            ApplyAccent((AccentColorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Violet");
            ApplyDensity((DensityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced");
            ApplySearchFilter(SearchBox.Text);
            await Task.Delay(180);
        });

        StartupStatusText.Text = "Loading complete.";
        MainShell.Visibility = Visibility.Visible;
        AnimateFadeIn(MainShell, 360);
        await FadeOutElementAsync(StartupOverlay, 320);
        SidebarSessionStateText.Text = "Free User";
        PlayStartupCompleteSound();
        await ShowBannerAsync("Tulip ready.", true);
    }

    private async Task RunStartupStepAsync(string statusText, double progress, Func<Task> step)
    {
        StartupStatusText.Text = statusText;
        await AnimateStartupProgressAsync(progress, 420);
        await step();
    }

    private async Task AnimateStartupProgressAsync(double target, int durationMs)
    {
        var start = _startupProgress;
        var delta = target - start;
        if (Math.Abs(delta) < 0.1)
        {
            StartupProgressBar.Value = target;
            _startupProgress = target;
            return;
        }

        const int frames = 30;
        for (var i = 1; i <= frames; i++)
        {
            var t = i / (double)frames;
            var eased = 1 - Math.Pow(1 - t, 3);
            var value = start + (delta * eased);
            StartupProgressBar.Value = value;
            _startupProgress = value;
            await Task.Delay(Math.Max(8, durationMs / frames));
        }

        StartupProgressBar.Value = target;
        _startupProgress = target;
    }

    private async Task FadeOutElementAsync(UIElement element, int durationMs)
    {
        var completion = new TaskCompletionSource<bool>();
        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        animation.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
            element.Opacity = 1;
            completion.TrySetResult(true);
        };

        element.BeginAnimation(UIElement.OpacityProperty, animation);
        await completion.Task;
    }

    private void ShowPage(Grid targetPage, Button navButton)
    {
        foreach (var page in _pages)
        {
            if (page == targetPage)
            {
                page.Visibility = Visibility.Visible;
                AnimateFadeIn(page, 220);
            }
            else
            {
                page.Visibility = Visibility.Collapsed;
            }
        }

        _activePage = targetPage;
        UpdateNavigationState(navButton);
        ApplySearchFilter(SearchBox.Text);
    }

    private void UpdateNavigationState(Button activeButton)
    {
        foreach (var button in _navButtons)
        {
            var isActive = button == activeButton;
            button.Background = isActive ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2B1C47")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#19122C"));
            button.BorderBrush = isActive ? (Brush)Resources["AccentBrush"] : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A1C46"));
            button.Foreground = isActive ? (Brush)Resources["TextPrimaryBrush"] : (Brush)Resources["TextSecondaryBrush"];
        }
    }

    private void ShowPerformanceTab(Grid targetTab, Button tabButton)
    {
        foreach (var tab in _performanceTabPages)
        {
            if (tab == targetTab)
            {
                tab.Visibility = Visibility.Visible;
                AnimateFadeIn(tab, 180);
            }
            else
            {
                tab.Visibility = Visibility.Collapsed;
            }
        }

        foreach (var button in _performanceTabButtons)
        {
            var isActive = button == tabButton;
            button.Background = isActive ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#352357")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A132D"));
            button.BorderBrush = isActive ? (Brush)Resources["AccentBrush"] : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#32234F"));
            button.Foreground = isActive ? (Brush)Resources["TextPrimaryBrush"] : (Brush)Resources["TextSecondaryBrush"];
            button.Opacity = isActive ? 1 : 0.88;
        }

        ApplySearchFilter(SearchBox.Text);
    }

    private void ShowAdvancedTab(Grid targetTab, Button tabButton)
    {
        foreach (var tab in _advancedTabPages)
        {
            if (tab == targetTab)
            {
                tab.Visibility = Visibility.Visible;
                AnimateFadeIn(tab, 180);
            }
            else
            {
                tab.Visibility = Visibility.Collapsed;
            }
        }

        foreach (var button in _advancedTabButtons)
        {
            var isActive = button == tabButton;
            button.Background = isActive ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#352357")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A132D"));
            button.BorderBrush = isActive ? (Brush)Resources["AccentBrush"] : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#32234F"));
            button.Foreground = isActive ? (Brush)Resources["TextPrimaryBrush"] : (Brush)Resources["TextSecondaryBrush"];
            button.Opacity = isActive ? 1 : 0.88;
        }

        ApplySearchFilter(SearchBox.Text);
    }

    private static void AnimateFadeIn(UIElement element, double durationMs)
    {
        element.Opacity = 0;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void ApplySearchFilter(string query)
    {
        if (_pages.Count == 0)
        {
            return;
        }

        if (_searchWatermarkActive)
        {
            query = string.Empty;
        }

        var normalized = (query ?? string.Empty).Trim();
        var cards = _pages
            .SelectMany(page => FindVisualChildren<Border>(page))
            .Where(border => border.Tag is string)
            .ToList();

        if (normalized.Length == 0)
        {
            foreach (var card in cards)
            {
                card.Visibility = Visibility.Visible;
            }

            return;
        }

        foreach (var card in cards)
        {
            var keywords = card.Tag?.ToString() ?? string.Empty;
            card.Visibility = keywords.Contains(normalized, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    public static readonly DependencyProperty VerticalOffsetProxyProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffsetProxy",
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnVerticalOffsetProxyChanged));

    private static void OnVerticalOffsetProxyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        e.Handled = true;
        var currentOffset = scrollViewer.VerticalOffset;
        var targetOffset = Math.Clamp(currentOffset - (e.Delta * 0.38), 0, scrollViewer.ScrollableHeight);

        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = targetOffset,
            Duration = TimeSpan.FromMilliseconds(170),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        scrollViewer.BeginAnimation(VerticalOffsetProxyProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyAccent(string accentName)
    {
        var accent = accentName switch
        {
            "Rose" => (Color)ColorConverter.ConvertFromString("#F472B6"),
            "Cyan" => (Color)ColorConverter.ConvertFromString("#22D3EE"),
            "Emerald" => (Color)ColorConverter.ConvertFromString("#34D399"),
            _ => (Color)ColorConverter.ConvertFromString("#A855F7")
        };

        var accentDark = accentName switch
        {
            "Rose" => (Color)ColorConverter.ConvertFromString("#DB2777"),
            "Cyan" => (Color)ColorConverter.ConvertFromString("#0891B2"),
            "Emerald" => (Color)ColorConverter.ConvertFromString("#059669"),
            _ => (Color)ColorConverter.ConvertFromString("#7E22CE")
        };

        Resources["AccentBrush"] = new SolidColorBrush(accent);
        Resources["AccentBrushDark"] = new SolidColorBrush(accentDark);

        if (_activePage is not null)
        {
            UpdateNavigationState(_navButtons.First(button => button.Tag?.ToString() == _activePage.Name));
        }
    }

    private void ApplyDensity(string density)
    {
        switch (density)
        {
            case "Compact":
                Resources["CardPadding"] = new Thickness(12);
                Resources["CardMargin"] = new Thickness(0, 0, 10, 10);
                break;
            case "Comfortable":
                Resources["CardPadding"] = new Thickness(20);
                Resources["CardMargin"] = new Thickness(0, 0, 16, 16);
                break;
            default:
                Resources["CardPadding"] = new Thickness(16);
                Resources["CardMargin"] = new Thickness(0, 0, 14, 14);
                break;
        }
    }

    private void ApplyTheme(string theme)
    {
        if (theme == "Obsidian")
        {
            Resources["WindowBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#07060D"));
            Resources["SidebarBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F0A1A"));
            Resources["PanelBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#151124"));
            Resources["CardBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1530"));
            Resources["BorderBrushColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2444"));
        }
        else if (theme == "Deep Violet")
        {
            Resources["WindowBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D0818"));
            Resources["SidebarBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#160D29"));
            Resources["PanelBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1433"));
            Resources["CardBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#24183E"));
            Resources["BorderBrushColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#412B61"));
        }
        else
        {
            Resources["WindowBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B0714"));
            Resources["SidebarBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#120A22"));
            Resources["PanelBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#181126"));
            Resources["CardBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1631"));
            Resources["BorderBrushColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#352352"));
        }
    }

    private void ApplyTextScale(double scale)
    {
        var clamped = Math.Clamp(scale, 0.85, 1.25);
        Resources["BodyTextSize"] = BaseBodyTextSize * clamped;
        Resources["PageTitleTextSize"] = BasePageTitleTextSize * clamped;
        Resources["PageSubtitleTextSize"] = BasePageSubtitleTextSize * clamped;
        Resources["ControlTextSize"] = BaseControlTextSize * clamped;
        Resources["CardTitleTextSize"] = BaseCardTitleTextSize * clamped;
        Resources["CardCategoryTextSize"] = BaseCardCategoryTextSize * clamped;
        Resources["SectionTitleTextSize"] = BaseSectionTitleTextSize * clamped;
        TextScaleValueText.Text = $"{Math.Round(clamped * 100)}%";
    }

    private async Task<OperationResult> ExecuteActionAsync(string actionName, Func<Task<OperationResult>> operation)
    {
        await _actionLock.WaitAsync();
        try
        {
            ShowActionOverlay($"Applying {actionName}...");
            var result = await operation();

            SessionStatusText.Text = result.Message;
            LastActionText.Text = $"{DateTime.Now:HH:mm:ss} - {actionName}";
            await ShowBannerAsync(result.Message, result.Success);
            AppendLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {actionName} | {(result.Success ? "OK" : "FAIL")} | {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            var message = $"{actionName} failed: {ex.Message}";
            SessionStatusText.Text = message;
            await ShowBannerAsync(message, false);
            AppendLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {actionName} | EXCEPTION | {ex}");
            return new OperationResult(false, message);
        }
        finally
        {
            HideActionOverlay();
            _actionLock.Release();
        }
    }

    private void ShowActionOverlay(string text)
    {
        ActionOverlayText.Text = text;
        ActionOverlay.Opacity = 0;
        ActionOverlay.Visibility = Visibility.Visible;
        AnimateFadeIn(ActionOverlay, 120);
    }

    private void HideActionOverlay()
    {
        var animation = new DoubleAnimation
        {
            From = ActionOverlay.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(130),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) => ActionOverlay.Visibility = Visibility.Collapsed;
        ActionOverlay.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void ShowWarningInfoOverlay(string warningText)
    {
        WarningInfoText.Text =
            "This tweak is marked as a warning because it can change expected Windows behavior.\n\n" +
            $"What it can break:\n{warningText}";
        WarningInfoOverlay.Opacity = 0;
        WarningInfoOverlay.Visibility = Visibility.Visible;
        AnimateFadeIn(WarningInfoOverlay, 130);
    }

    private void HideWarningInfoOverlay()
    {
        var animation = new DoubleAnimation
        {
            From = WarningInfoOverlay.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(130),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) => WarningInfoOverlay.Visibility = Visibility.Collapsed;
        WarningInfoOverlay.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void CloseWarningInfoOverlay_Click(object sender, RoutedEventArgs e) => HideWarningInfoOverlay();

    private void WarningInfoOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => HideWarningInfoOverlay();

    private void WarningInfoCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private async Task ShowBannerAsync(string text, bool success)
    {
        _bannerCts?.Cancel();
        _bannerCts = new CancellationTokenSource();

        BannerText.Text = text;
        BannerHost.Background = success ? (Brush)Resources["SuccessBrush"] : (Brush)Resources["ErrorBrush"];
        BannerHost.Opacity = 0;
        BannerHost.Visibility = Visibility.Visible;
        AnimateFadeIn(BannerHost, 150);

        try
        {
            await Task.Delay(2800, _bannerCts.Token);
            var fadeOut = new DoubleAnimation
            {
                From = BannerHost.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (_, _) => BannerHost.Visibility = Visibility.Collapsed;
            BannerHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
        }
    }
    private async Task<OperationResult> RunQuickScanAsync()
    {
        await LoadSystemSnapshotAsync();
        return new OperationResult(true, "System snapshot refreshed.");
    }

    private async Task LoadSystemSnapshotAsync()
    {
        CpuValueText.Text = GetCpuName();
        GpuValueText.Text = await GetGpuNameAsync();
        RamValueText.Text = GetInstalledMemoryText();
        StorageValueText.Text = GetStorageText();
        OsValueText.Text = GetOsText();
        PowerPlanValueText.Text = await GetActivePowerPlanAsync();
        AdminValueText.Text = IsAdministrator() ? "Administrator" : "Standard User";
        SidebarAdminStateText.Text = IsAdministrator() ? "Yes" : "No";
    }

    private static string GetCpuName()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cpuKey = baseKey.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return cpuKey?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown CPU";
    }

    private async Task<string> GetGpuNameAsync()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var winsat = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WinSAT");
        var directValue = winsat?.GetValue("PrimaryAdapterString")?.ToString();
        if (!string.IsNullOrWhiteSpace(directValue))
        {
            return directValue;
        }

        var result = await RunPowerShellAsync("(Get-CimInstance Win32_VideoController | Select-Object -First 1 -ExpandProperty Name)");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim()
            : "Unknown GPU";
    }

    private static string GetInstalledMemoryText()
    {
        return GetPhysicallyInstalledSystemMemory(out var totalKb)
            ? $"{totalKb / 1024d / 1024d:0.0} GB"
            : "Unavailable";
    }

    private static string GetStorageText()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(root))
        {
            return "Unavailable";
        }

        var drive = new DriveInfo(root);
        var free = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
        var total = drive.TotalSize / 1024d / 1024d / 1024d;
        return $"{drive.Name} {free:0} GB free / {total:0} GB";
    }

    private static string GetOsText()
    {
        var version = Environment.OSVersion.Version;
        var build = version.Build;

        if (build >= 26100)
        {
            return "Windows 11 24H2";
        }

        if (build >= 22631)
        {
            return "Windows 11 23H2";
        }

        if (build >= 22621)
        {
            return "Windows 11 22H2";
        }

        if (build >= 22000)
        {
            return "Windows 11 21H2";
        }

        if (build >= 19045)
        {
            return "Windows 10 22H2";
        }

        if (build >= 19044)
        {
            return "Windows 10 21H2";
        }

        return RuntimeInformation.OSDescription.Trim();
    }

    private async Task<string> GetActivePowerPlanAsync()
    {
        var result = await RunProcessAsync("powercfg.exe", "/getactivescheme");
        if (result.ExitCode != 0)
        {
            return "Unknown";
        }

        var line = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(text => text.Contains("Power Scheme GUID", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(line))
        {
            return "Unknown";
        }

        var start = line.IndexOf('(');
        var end = line.IndexOf(')');
        return start >= 0 && end > start ? line.Substring(start + 1, end - start - 1).Trim() : line.Trim();
    }

    private async Task<OperationResult> RestoreDefaultsAsync()
    {
        var results = new List<OperationResult>
        {
            await ApplyNetworkThrottlingAsync(false),
            await ApplyMultimediaThrottlingAsync(false),
            await ApplyCpuPowerThrottlingAsync(false),
            await ApplyUltimatePlanToggleAsync(false),
            await ApplyStorageSenseAsync(false)
        };

        return results.Any(result => !result.Success)
            ? new OperationResult(false, "Restore defaults completed with warnings.")
            : new OperationResult(true, "Default profile restored.");
    }

    private async Task<OperationResult> ApplyLowLatencyProfileAsync()
    {
        var results = new List<OperationResult>
        {
            await ApplyNetworkThrottlingAsync(true),
            await ApplyMultimediaThrottlingAsync(true),
            await ApplyCpuPowerThrottlingAsync(true),
            await ApplyUltimatePlanToggleAsync(true)
        };

        return results.Any(result => !result.Success)
            ? new OperationResult(false, "Low Latency profile applied with warnings.")
            : new OperationResult(true, "Low Latency profile applied.");
    }

    private async Task<OperationResult> ApplyBalancedProfileAsync()
    {
        var results = new List<OperationResult>
        {
            await ApplyUltimatePlanToggleAsync(false),
            await ApplyCpuPowerThrottlingAsync(false),
            await ApplyNetworkThrottlingAsync(false)
        };

        return results.Any(result => !result.Success)
            ? new OperationResult(false, "Balanced profile applied with warnings.")
            : new OperationResult(true, "Balanced profile applied.");
    }

    private async Task<OperationResult> ApplyHighPerformanceProfileAsync()
    {
        var results = new List<OperationResult>
        {
            await ApplyUltimatePlanToggleAsync(true),
            await ApplyCoreParkingAsync(true),
            await ApplyCpuPowerThrottlingAsync(true),
            await ApplyGpuPowerPolicyAsync(true)
        };

        return results.Any(result => !result.Success)
            ? new OperationResult(false, "High Performance profile applied with warnings.")
            : new OperationResult(true, "High Performance profile applied.");
    }

    private async Task<OperationResult> ApplyNvidiaLaptopProfileAsync()
    {
        var results = new List<OperationResult>
        {
            await ApplyDisableGeForceDriverUpdatesAsync(true),
            await ApplyDisableNvidiaLoggingAsync(true),
            await ApplyOptimizeGpuIdleThresholdsAsync(true),
            await ApplyOptimizeDirectFlipVrrAsync(true),
            await ApplyOptimizeFrameSchedulingAsync(true),
            await ApplyGpuPowerPolicyAsync(true)
        };

        return results.Any(result => !result.Success)
            ? new OperationResult(false, "Laptop NVIDIA profile applied with warnings.")
            : new OperationResult(true, "Laptop NVIDIA profile applied.");
    }

    private async Task<OperationResult> ApplyNvidiaDesktopProfileAsync()
    {
        var results = new List<OperationResult>
        {
            await ApplyDisableGeForceDriverUpdatesAsync(true),
            await ApplyDisableNvidiaLoggingAsync(true),
            await ApplyDisableHdcpAsync(true),
            await ApplyDisableNvidiaDmaRemappingAsync(true),
            await ApplyDisableNvidiaUvmAsync(true),
            await ApplyForceContiguousMemoryAllocationAsync(true),
            await ApplyOptimizeGpuIdleThresholdsAsync(true),
            await ApplyOptimizeNvidiaMemoryLatencyAsync(true),
            await ApplyOptimizeDirectFlipVrrAsync(true),
            await ApplyOptimizeFrameSchedulingAsync(true),
            await ApplyOptimizeGeForceExperienceAsync(true),
            await ApplyGpuPowerPolicyAsync(true)
        };

        return results.Any(result => !result.Success)
            ? new OperationResult(false, "Desktop NVIDIA profile applied with warnings.")
            : new OperationResult(true, "Desktop NVIDIA profile applied.");
    }

    private async Task<OperationResult> OptimizeUsbControllerAsync()
    {
        var results = new List<OperationResult>
        {
            await ApplyUsbSelectiveSuspendAsync(true),
            await ApplyDebugPollIntervalAsync(true),
            await ApplyIdleSleepStatesAsync(true),
            await SetUsbEnhancedPowerManagementAsync(true),
            await ApplyMsiModeAsync(true)
        };

        return results.Any(result => !result.Success)
            ? new OperationResult(false, "Controller USB optimization completed with warnings.")
            : new OperationResult(true, "Controller USB optimization applied.");
    }

    private async Task<OperationResult> OptimizeUsbKeyboardMouseAsync()
    {
        var results = new List<OperationResult>
        {
            await ApplyUsbSelectiveSuspendAsync(true),
            await ApplyDebugPollIntervalAsync(true),
            await ApplyIdleSleepStatesAsync(true),
            await SetUsbEnhancedPowerManagementAsync(true),
            await ApplyPixelPerfectMouseAsync(true),
            await ApplyKeyboardRepeatDelayAsync(true),
            await ApplyMsiModeAsync(true)
        };

        return results.Any(result => !result.Success)
            ? new OperationResult(false, "Keyboard/mouse USB optimization completed with warnings.")
            : new OperationResult(true, "Keyboard/mouse USB optimization applied.");
    }

    private Task<OperationResult> EnableMsiForCompatibleDevicesAsync()
    {
        return SetMsiForCompatibleDevicesAsync(true);
    }

    private async Task<OperationResult> CreateRestorePointAsync()
    {
        var script = "Checkpoint-Computer -Description 'TulipTweaks' -RestorePointType MODIFY_SETTINGS";
        var result = await RunPowerShellAsync(script, 90000);
        return ToOperationResult(result, "Restore point created.", "Failed to create restore point");
    }

    private async Task<OperationResult> ExportBackupAsync()
    {
        var folder = Path.Combine(_backupDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);

        var exports = new[]
        {
            (Root: "HKLM", Path: @"SOFTWARE\Policies\Microsoft\Windows", File: "policies-windows.reg"),
            (Root: "HKLM", Path: @"SYSTEM\CurrentControlSet\Control\Power", File: "power.reg"),
            (Root: "HKCU", Path: @"Software\Microsoft\Windows\CurrentVersion", File: "current-user.reg")
        };

        var failures = 0;
        foreach (var export in exports)
        {
            var target = Path.Combine(folder, export.File);
            var cmd = await RunProcessAsync("reg.exe", $"export \"{export.Root}\\{export.Path}\" \"{target}\" /y");
            if (cmd.ExitCode != 0)
            {
                failures++;
            }
        }

        return failures == 0
            ? new OperationResult(true, $"Backup exported to {folder}")
            : new OperationResult(false, $"Backup exported with {failures} warning(s) to {folder}");
    }

    private Task<OperationResult> OpenBackupFolderAsync() => OpenFolderAsync(_backupDirectory, "Backup folder opened.");

    private Task<OperationResult> OpenLogsFolderAsync() => OpenFolderAsync(_logDirectory, "Log folder opened.");

    private async Task<OperationResult> FlushDnsAsync()
    {
        return ToOperationResult(await RunProcessAsync("ipconfig.exe", "/flushdns"), "DNS cache flushed.", "Failed to flush DNS cache");
    }

    private async Task<OperationResult> RestartExplorerAsync()
    {
        _ = await RunProcessAsync("taskkill.exe", "/F /IM explorer.exe");
        await Task.Delay(500);
        var start = await RunProcessAsync("cmd.exe", "/c start explorer.exe");
        return ToOperationResult(start, "Explorer restarted.", "Failed to restart Explorer");
    }

    private async Task<OperationResult> OpenFolderAsync(string path, string successMessage)
    {
        Directory.CreateDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            await Task.CompletedTask;
            return new OperationResult(true, successMessage);
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Unable to open folder: {ex.Message}");
        }
    }

    private async Task<OperationResult> StartCleaningAsync()
    {
        var steps = new[]
        {
            await ClearTempCacheAsync(),
            await ClearChromeTempAsync(),
            await ClearGameTempAsync(),
            await ClearPrefetchAsync(),
            await ClearRecycleBinAsync()
        };

        var failures = steps.Count(step => !step.Success);
        return failures == 0
            ? new OperationResult(true, "Cleaning completed successfully.")
            : new OperationResult(false, $"Cleaning completed with {failures} warning(s).");
    }

    private Task<OperationResult> ClearTempCacheAsync() => ClearDirectoriesAsync(new[]
    {
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
    }, "Cache cleared.");

    private Task<OperationResult> ClearChromeTempAsync() => ClearDirectoriesAsync(new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Cache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "ShaderCache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Code Cache")
    }, "Chrome temp files cleared.");

    private Task<OperationResult> ClearGameTempAsync() => ClearDirectoriesAsync(new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "GLCache")
    }, "Game temp files cleared.");

    private Task<OperationResult> ClearPrefetchAsync() => ClearDirectoriesAsync(new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch")
    }, "Prefetch cleared.");

    private async Task<OperationResult> ClearRecycleBinAsync()
    {
        var result = await RunPowerShellAsync("Clear-RecycleBin -Force", 60000);
        return ToOperationResult(result, "Recycle Bin cleared.", "Failed to clear Recycle Bin");
    }

    private async Task<OperationResult> ClearDirectoriesAsync(IEnumerable<string> directories, string successMessage)
    {
        var removed = 0;
        var failed = 0;

        await Task.Run(() =>
        {
            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                List<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
                }
                catch
                {
                    failed++;
                    continue;
                }

                foreach (var file in files)
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        failed++;
                    }
                }

                List<string> folders;
                try
                {
                    folders = Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length).ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var folder in folders)
                {
                    try
                    {
                        Directory.Delete(folder, true);
                    }
                    catch
                    {
                    }
                }
            }
        });

        return failed == 0
            ? new OperationResult(true, $"{successMessage} Removed {removed} file(s).")
            : new OperationResult(false, $"{successMessage} Removed {removed} file(s), failed {failed}.");
    }

    private async Task<OperationResult> DisableOneDriveAsync()
    {
        _ = await RunProcessAsync("taskkill.exe", "/F /IM OneDrive.exe");
        var policy = SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\OneDrive",
            "DisableFileSyncNGSC",
            1,
            "OneDrive sync policy updated.");

        var localInstaller = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDriveSetup.exe");
        var systemInstaller = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "OneDriveSetup.exe");
        var installer = File.Exists(localInstaller) ? localInstaller : systemInstaller;

        if (File.Exists(installer))
        {
            var uninstall = await RunProcessAsync(installer, "/uninstall", 120000);
            if (uninstall.ExitCode != 0 && uninstall.ExitCode != 2)
            {
                return new OperationResult(false, "OneDrive policy set, but uninstall reported warnings.");
            }
        }

        return policy.Success ? new OperationResult(true, "OneDrive disabled.") : policy;
    }

    private async Task<OperationResult> RemoveXboxAppsAsync()
    {
        const string script = "Get-AppxPackage *Xbox* | Remove-AppxPackage -ErrorAction SilentlyContinue";
        return ToOperationResult(await RunPowerShellAsync(script, 120000), "Xbox apps removed.", "Failed to remove Xbox apps");
    }

    private Task<OperationResult> DisableCortanaAsync()
    {
        var first = SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Windows Search",
            "AllowCortana",
            0,
            "Cortana policy updated.");
        var second = SetRegistryDword(
            RegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
            "CortanaConsent",
            0,
            "Cortana consent updated.");
        return Task.FromResult(first.Success && second.Success
            ? new OperationResult(true, "Cortana disabled.")
            : new OperationResult(false, "Cortana configuration applied with warnings."));
    }

    private async Task<OperationResult> RemoveMixedRealityAsync()
    {
        const string script = "Get-AppxPackage Microsoft.MixedReality.Portal | Remove-AppxPackage -ErrorAction SilentlyContinue";
        return ToOperationResult(await RunPowerShellAsync(script, 120000), "Mixed Reality Portal removed.", "Failed to remove Mixed Reality Portal");
    }
    private Task<OperationResult> DisableTeamsAutostartAsync()
    {
        var one = DeleteRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "com.squirrel.Teams.Teams");
        var two = DeleteRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Teams");
        return Task.FromResult(one.Success || two.Success
            ? new OperationResult(true, "Teams auto-start disabled.")
            : new OperationResult(false, "Teams auto-start keys were not found or could not be changed."));
    }

    private async Task<OperationResult> RemoveConsumerBloatwareAsync()
    {
        _ = SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\CloudContent",
            "DisableWindowsConsumerFeatures",
            1,
            "Consumer content policy updated.");
        const string script = "@('Microsoft.ZuneMusic','Microsoft.ZuneVideo','Microsoft.BingNews','Microsoft.GetHelp') | ForEach-Object { Get-AppxPackage $_ | Remove-AppxPackage -ErrorAction SilentlyContinue }";
        return ToOperationResult(await RunPowerShellAsync(script, 120000), "Consumer bloatware removed where available.", "Failed to remove some consumer packages");
    }

    private Task<OperationResult> ApplyNagleAlgorithmAsync(bool enabled)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var interfaces = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", writable: true);

        if (interfaces is null)
        {
            return Task.FromResult(new OperationResult(false, "TCP interface registry path not found."));
        }

        var touched = 0;
        foreach (var name in interfaces.GetSubKeyNames())
        {
            using var adapter = interfaces.OpenSubKey(name, writable: true);
            if (adapter is null)
            {
                continue;
            }

            if (enabled)
            {
                adapter.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                adapter.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
            }
            else
            {
                adapter.DeleteValue("TcpAckFrequency", false);
                adapter.DeleteValue("TCPNoDelay", false);
            }

            touched++;
        }

        return Task.FromResult(touched > 0
            ? new OperationResult(true, "Nagle algorithm setting applied.")
            : new OperationResult(false, "No network interfaces were updated."));
    }

    private Task<OperationResult> ApplyTcpWindowScalingAsync(bool enabled) => RunCommandOperationAsync(
        "netsh.exe",
        $"int tcp set global autotuninglevel={(enabled ? "normal" : "restricted")}",
        enabled ? "TCP window scaling optimized." : "TCP window scaling reverted.");

    private Task<OperationResult> ApplyNetworkThrottlingAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
        "NetworkThrottlingIndex",
        enabled ? unchecked((int)0xFFFFFFFF) : 10,
        enabled ? "Network throttling reduced." : "Network throttling restored."));

    private Task<OperationResult> ApplyMultimediaThrottlingAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
        "SystemResponsiveness",
        enabled ? 0 : 20,
        enabled ? "Multimedia network throttling disabled." : "Multimedia throttling restored."));

    private Task<OperationResult> ApplyDnsCacheAsync(bool enabled)
    {
        var ttl = SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxCacheTtl", enabled ? 86400 : 5400, "DNS cache updated.");
        var negative = SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxNegativeCacheTtl", enabled ? 300 : 0, "DNS cache updated.");
        return Task.FromResult(ttl.Success && negative.Success
            ? new OperationResult(true, "DNS cache size optimized.")
            : new OperationResult(false, "DNS cache update completed with warnings."));
    }

    private Task<OperationResult> ApplyQosReservationAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SOFTWARE\Policies\Microsoft\Windows\Psched",
        "NonBestEffortLimit",
        enabled ? 0 : 20,
        enabled ? "QoS reservation disabled." : "QoS reservation restored."));

    private Task<OperationResult> ApplyTeredoAsync(bool enabled) => RunCommandOperationAsync(
        "netsh.exe",
        enabled ? "interface teredo set state disabled" : "interface teredo set state default",
        enabled ? "Teredo disabled." : "Teredo enabled.");

    private async Task<OperationResult> ApplyNetworkPowerSavingAsync(bool enabled)
    {
        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a {(enabled ? "0" : "2")}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a {(enabled ? "0" : "2")}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "Network power saving disabled." : "Network power saving restored.")
            : new OperationResult(false, "Failed to update network power policy.");
    }

    private async Task<OperationResult> ApplyCommunicationPortsAsync(bool enabled)
    {
        var serial = await SetServiceStateIfExistsAsync("Serial", !enabled, "demand");
        var serenum = await SetServiceStateIfExistsAsync("Serenum", !enabled, "demand");
        var parport = await SetServiceStateIfExistsAsync("Parport", !enabled, "demand");
        var failures = new[] { serial, serenum, parport }.Count(result => !result.Success);
        return failures == 0
            ? new OperationResult(true, enabled ? "Communication ports enabled." : "Communication ports restored.")
            : new OperationResult(false, "Communication ports updated with warnings.");
    }

    private async Task<OperationResult> ApplyApicAsync(bool enabled)
    {
        var command = enabled ? "/set x2apicpolicy enable" : "/deletevalue x2apicpolicy";
        var result = await RunProcessAsync("bcdedit.exe", command);
        if (!enabled && result.ExitCode != 0)
        {
            result = await RunProcessAsync("bcdedit.exe", "/set x2apicpolicy default");
        }

        return ToOperationResult(
            result,
            enabled ? "APIC policy enabled (reboot recommended)." : "APIC policy restored.",
            "Failed to update APIC policy");
    }

    private async Task<OperationResult> ApplyHpetAsync(bool enabled)
    {
        var result = await RunProcessAsync("bcdedit.exe", enabled ? "/set useplatformclock true" : "/deletevalue useplatformclock");
        return ToOperationResult(
            result,
            enabled ? "HPET platform clock enabled (reboot recommended)." : "HPET platform clock restored.",
            "Failed to update HPET");
    }

    private Task<OperationResult> ApplyMsiModeAsync(bool enabled)
    {
        return SetMsiForCompatibleDevicesAsync(enabled);
    }

    private async Task<OperationResult> ApplyMsiOperationTypeAsync(bool enabled)
    {
        var script = $@"
$value = {(enabled ? 3 : 0)}
$count = 0
$root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
if (Test-Path $root) {{
    Get-ChildItem -Path $root -Recurse -ErrorAction SilentlyContinue | ForEach-Object {{
        $affinity = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\Affinity Policy'
        if (Test-Path $affinity) {{
            New-ItemProperty -Path $affinity -Name DevicePolicy -PropertyType DWord -Value $value -Force -ErrorAction SilentlyContinue | Out-Null
            $count++
        }}
    }}
}}
Write-Output $count";

        var result = await RunPowerShellAsync(script, 120000);
        if (result.ExitCode != 0)
        {
            return ToOperationResult(result, string.Empty, "Failed to update MSI operation policy");
        }

        return new OperationResult(true, enabled ? "MSI operation policy enabled." : "MSI operation policy restored.");
    }

    private async Task<OperationResult> ApplyRssAsync(bool enabled)
    {
        var result = await RunProcessAsync("netsh.exe", $"int tcp set global rss={(enabled ? "enabled" : "disabled")}");
        return ToOperationResult(result, enabled ? "Receive Side Scaling enabled." : "Receive Side Scaling disabled.", "Failed to update RSS");
    }

    private async Task<OperationResult> ApplySerialPortsAsync(bool enabled)
    {
        var serial = await SetServiceStateIfExistsAsync("Serial", enabled, "demand");
        var serenum = await SetServiceStateIfExistsAsync("Serenum", enabled, "demand");
        var failures = new[] { serial, serenum }.Count(result => !result.Success);
        return failures == 0
            ? new OperationResult(true, enabled ? "Serial ports disabled." : "Serial ports restored.")
            : new OperationResult(false, "Serial port service update had warnings.");
    }

    private Task<OperationResult> ApplyActivityFeedAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", enabled ? 0 : 1, "Activity Feed setting applied."));
    private Task<OperationResult> ApplyAdvertisingIdAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", enabled ? 0 : 1, "Advertising ID setting applied."));
    private Task<OperationResult> ApplyCeipAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", enabled ? 0 : 1, "CEIP setting applied."));
    private Task<OperationResult> ApplyCompatibilityTelemetryAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DisableTelemetry", enabled ? 1 : 0, "Compatibility telemetry setting applied."));
    private Task<OperationResult> ApplyDiagnosticCollectionAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", enabled ? 0 : 3, "Diagnostic data collection setting applied."));
    private Task<OperationResult> ApplyErrorReportingAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", enabled ? 1 : 0, "Error reporting setting applied."));
    private Task<OperationResult> ApplyLocationTrackingAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", enabled ? 1 : 0, "Location tracking setting applied."));
    private Task<OperationResult> ApplyRemoteAssistanceAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", enabled ? 0 : 1, "Remote Assistance setting applied."));

    private Task<OperationResult> ApplyFeedbackHubAsync(bool enabled)
    {
        var one = SetRegistryDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", enabled ? 0 : 4, "Feedback Hub setting applied.");
        var two = SetRegistryDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Siuf\Rules", "PeriodInNanoSeconds", enabled ? 0 : 604800000, "Feedback Hub setting applied.");
        return Task.FromResult(one.Success && two.Success
            ? new OperationResult(true, "Feedback Hub setting applied.")
            : new OperationResult(false, "Feedback Hub setting applied with warnings."));
    }

    private Task<OperationResult> ApplyTimelineTrackingAsync(bool enabled)
    {
        var a = SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", enabled ? 0 : 1, "Timeline setting applied.");
        var b = SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", enabled ? 0 : 1, "Timeline setting applied.");
        var c = SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", enabled ? 0 : 1, "Timeline setting applied.");
        return Task.FromResult(a.Success && b.Success && c.Success
            ? new OperationResult(true, "Timeline tracking setting applied.")
            : new OperationResult(false, "Timeline tracking applied with warnings."));
    }

    private async Task<OperationResult> ApplyXboxServicesAsync(bool enabled)
    {
        var names = new[] { "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc" };
        var failures = 0;

        foreach (var name in names)
        {
            var result = await SetServiceStateAsync(name, enabled, "demand");
            if (!result.Success)
            {
                failures++;
            }
        }

        return failures == 0
            ? new OperationResult(true, enabled ? "Xbox services disabled." : "Xbox services restored.")
            : new OperationResult(false, "Xbox services operation completed with warnings.");
    }

    private async Task<OperationResult> ApplyDiagnosticServicesAsync(bool enabled)
    {
        var a = await SetServiceStateAsync("DiagTrack", enabled, "demand");
        var b = await SetServiceStateAsync("dmwappushservice", enabled, "demand");
        return a.Success && b.Success
            ? new OperationResult(true, enabled ? "Diagnostic services disabled." : "Diagnostic services restored.")
            : new OperationResult(false, "Diagnostic services updated with warnings.");
    }

    private Task<OperationResult> ApplyHagsAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", enabled ? 2 : 1, "GPU scheduling setting applied."));

    private Task<OperationResult> ApplyFullscreenOptimizationsAsync(bool enabled)
    {
        var one = SetRegistryDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", enabled ? 2 : 0, "Fullscreen optimization setting applied.");
        var two = SetRegistryDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", enabled ? 1 : 0, "Fullscreen optimization setting applied.");
        return Task.FromResult(one.Success && two.Success
            ? new OperationResult(true, enabled ? "Fullscreen optimizations disabled." : "Fullscreen optimizations restored.")
            : new OperationResult(false, "Fullscreen optimization update had warnings."));
    }

    private async Task<OperationResult> ApplyGpuPowerPolicyAsync(bool enabled)
    {
        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_VIDEO VIDEOIDLE {(enabled ? "0" : "300")}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_VIDEO VIDEOIDLE {(enabled ? "0" : "300")}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "GPU throughput policy enabled." : "GPU throughput policy restored.")
            : new OperationResult(false, "Failed to update GPU power policy.");
    }

    private Task<OperationResult> ApplyMpoAsync(bool enabled)
    {
        if (enabled)
        {
            return Task.FromResult(SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, "MPO setting applied."));
        }

        return Task.FromResult(DeleteRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", "MPO setting restored."));
    }

    private async Task<OperationResult> ApplyDisableGeForceDriverUpdatesAsync(bool enabled)
    {
        var script = $@"
$pattern = 'NvDriverUpdate|NvProfileUpdater|NVIDIA.*Update|GeForce.*Update|NvNodeLauncher'
$count = 0
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {{ $_.TaskName -match $pattern -or $_.TaskPath -match $pattern }} | ForEach-Object {{
    {(enabled ? "Disable-ScheduledTask" : "Enable-ScheduledTask")} -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue | Out-Null
    $count++
}}
Write-Output $count";

        var taskResult = await RunPowerShellAsync(script, 90000);
        var policy = SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\NVIDIA Corporation\GeForce Experience",
            "DisableDriverUpdateChecks",
            enabled ? 1 : 0,
            "NVIDIA driver update policy updated.");

        if (taskResult.ExitCode != 0 && !policy.Success)
        {
            return new OperationResult(false, "Failed to update NVIDIA driver update policy.");
        }

        return new OperationResult(true, enabled ? "GeForce driver updates disabled." : "GeForce driver updates restored.");
    }

    private Task<OperationResult> ApplyDisableHdcpAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SOFTWARE\NVIDIA Corporation\Global\HDCP",
        "Enabled",
        enabled ? 0 : 1,
        enabled ? "NVIDIA HDCP policy disabled." : "NVIDIA HDCP policy restored."));

    private async Task<OperationResult> ApplyDisableNvidiaLoggingAsync(bool enabled)
    {
        var policy = SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\Logging",
            "EnableLogging",
            enabled ? 0 : 1,
            "NVIDIA logging policy updated.");

        var script = $@"
$logs = wevtutil el | Select-String -Pattern 'NVIDIA'
$count = 0
foreach ($entry in $logs) {{
    $name = $entry.ToString().Trim()
    if ($name) {{
        wevtutil sl ""$name"" /e:{(enabled ? "false" : "true")} 2>$null
        $count++
    }}
}}
Write-Output $count";

        var logs = await RunPowerShellAsync(script, 90000);
        if (!policy.Success && logs.ExitCode != 0)
        {
            return new OperationResult(false, "Failed to update NVIDIA logging.");
        }

        return new OperationResult(true, enabled ? "NVIDIA logging disabled." : "NVIDIA logging restored.");
    }

    private Task<OperationResult> ApplyDisableNvidiaDmaRemappingAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
        "DisableDmaRemapping",
        enabled ? 1 : 0,
        enabled ? "NVIDIA DMA remapping policy disabled." : "NVIDIA DMA remapping policy restored."));

    private Task<OperationResult> ApplyDisableNvidiaUvmAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Services\nvlddmkm",
        "EnableUvm",
        enabled ? 0 : 1,
        enabled ? "NVIDIA UVM policy disabled." : "NVIDIA UVM policy restored."));

    private Task<OperationResult> ApplyForceContiguousMemoryAllocationAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
        "ContiguousMemoryPolicy",
        enabled ? 1 : 0,
        enabled ? "Contiguous memory allocation policy enabled." : "Contiguous memory allocation policy restored."));

    private async Task<OperationResult> ApplyOptimizeGpuIdleThresholdsAsync(bool enabled)
    {
        var video = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_VIDEO VIDEOIDLE {(enabled ? "0" : "300")}");
        var pcieAc = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_PCIEXPRESS ASPM {(enabled ? "0" : "2")}");
        var pcieDc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_PCIEXPRESS ASPM {(enabled ? "0" : "2")}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");

        return video.ExitCode == 0 && pcieAc.ExitCode == 0 && pcieDc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "GPU idle thresholds optimized." : "GPU idle thresholds restored.")
            : new OperationResult(false, "Failed to update GPU idle threshold settings.");
    }

    private Task<OperationResult> ApplyOptimizeNvidiaMemoryLatencyAsync(bool enabled)
    {
        var one = SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "GpuLatencyBoost", enabled ? 1 : 0, "GPU memory latency policy updated.");
        var two = SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "LowLatencyMode", enabled ? 1 : 0, "GPU memory latency policy updated.");
        return Task.FromResult(one.Success && two.Success
            ? new OperationResult(true, enabled ? "NVIDIA memory latency settings optimized." : "NVIDIA memory latency settings restored.")
            : new OperationResult(false, "Failed to update NVIDIA memory latency settings."));
    }

    private Task<OperationResult> ApplyOptimizeDirectFlipVrrAsync(bool enabled)
    {
        var one = SetRegistryDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", enabled ? 2 : 0, "Direct Flip/VRR setting applied.");
        var two = SetRegistryDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", enabled ? 1 : 0, "Direct Flip/VRR setting applied.");
        var three = SetRegistryDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", enabled ? 1 : 0, "Direct Flip/VRR setting applied.");
        return Task.FromResult(one.Success && two.Success && three.Success
            ? new OperationResult(true, enabled ? "NVIDIA Direct Flip & VRR optimized." : "NVIDIA Direct Flip & VRR restored.")
            : new OperationResult(false, "Failed to update Direct Flip/VRR settings."));
    }

    private Task<OperationResult> ApplyOptimizeFrameSchedulingAsync(bool enabled)
    {
        var one = SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", enabled ? 2 : 1, "Frame scheduling setting applied.");
        var two = SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "SchedulerBoostMode", enabled ? 2 : 0, "Frame scheduling setting applied.");
        return Task.FromResult(one.Success && two.Success
            ? new OperationResult(true, enabled ? "NVIDIA frame scheduling optimized." : "NVIDIA frame scheduling restored.")
            : new OperationResult(false, "Failed to update frame scheduling settings."));
    }

    private async Task<OperationResult> ApplyOptimizeGeForceExperienceAsync(bool enabled)
    {
        var service = await SetServiceStateIfExistsAsync("NvTelemetryContainer", enabled, "auto");
        var policy = SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\NVIDIA Corporation\NvTelemetry",
            "NvTelemetryEnabled",
            enabled ? 0 : 1,
            "NVIDIA telemetry policy updated.");

        var script = $@"
$pattern = 'NvTm|NVIDIA.*Telemetry|NvProfileUpdater'
$count = 0
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {{ $_.TaskName -match $pattern -or $_.TaskPath -match $pattern }} | ForEach-Object {{
    {(enabled ? "Disable-ScheduledTask" : "Enable-ScheduledTask")} -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue | Out-Null
    $count++
}}
Write-Output $count";

        var tasks = await RunPowerShellAsync(script, 90000);
        if (!service.Success && !policy.Success && tasks.ExitCode != 0)
        {
            return new OperationResult(false, "Failed to optimize NVIDIA GeForce Experience.");
        }

        return new OperationResult(true, enabled ? "NVIDIA GeForce Experience optimized." : "NVIDIA GeForce Experience settings restored.");
    }

    private async Task<OperationResult> ApplyCoreParkingAsync(bool enabled)
    {
        var minCores = enabled ? "100" : "10";
        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_PROCESSOR CPMINCORES {minCores}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_PROCESSOR CPMINCORES {minCores}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "Core parking disabled." : "Core parking restored.")
            : new OperationResult(false, "Failed to update core parking.");
    }

    private Task<OperationResult> ApplyCpuPowerThrottlingAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
        "PowerThrottlingOff",
        enabled ? 1 : 0,
        enabled ? "CPU power throttling disabled." : "CPU power throttling restored."));

    private async Task<OperationResult> ApplyUltimatePlanToggleAsync(bool enabled)
    {
        if (!enabled)
        {
            return ToOperationResult(await RunProcessAsync("powercfg.exe", "/setactive SCHEME_BALANCED"), "Balanced plan restored.", "Failed to set balanced plan");
        }

        _ = await RunProcessAsync("powercfg.exe", "-duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61");
        var setUltimate = await RunProcessAsync("powercfg.exe", "/setactive e9a42b02-d5df-448d-aa00-03f14749eb61");

        if (setUltimate.ExitCode != 0)
        {
            var fallback = await RunProcessAsync("powercfg.exe", "/setactive SCHEME_MIN");
            return ToOperationResult(fallback, "High Performance plan activated.", "Failed to set high performance plan");
        }

        return new OperationResult(true, "Ultimate Performance plan activated.");
    }

    private async Task<OperationResult> ApplyVendorAwareSchedulerAsync(bool enabled)
    {
        var cpu = CpuValueText.Text;
        var value = 3;

        if (enabled)
        {
            value = cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }

        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_PROCESSOR PERFBOOSTMODE {value}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_PROCESSOR PERFBOOSTMODE {value}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");

        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "Vendor-aware scheduler optimization applied." : "Vendor scheduler optimization restored.")
            : new OperationResult(false, "Failed to apply vendor-aware scheduler optimization.");
    }

    private async Task<OperationResult> ApplyBasicCStatesAsync(bool enabled)
    {
        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_PROCESSOR IDLEDISABLE {(enabled ? "1" : "0")}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_PROCESSOR IDLEDISABLE {(enabled ? "1" : "0")}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "Basic CPU C-states disabled." : "Basic CPU C-states restored.")
            : new OperationResult(false, "Failed to update CPU C-state settings.");
    }

    private async Task<OperationResult> ApplyCoalescableTimerAsync(bool enabled)
    {
        var bcd = await RunProcessAsync("bcdedit.exe", enabled ? "/set disabledynamictick yes" : "/deletevalue disabledynamictick");
        var registry = SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Kernel",
            "GlobalTimerResolutionRequests",
            enabled ? 1 : 0,
            "Timer coalescing policy updated.");

        if (bcd.ExitCode != 0 && !registry.Success)
        {
            return new OperationResult(false, "Failed to update coalescable timer settings.");
        }

        return new OperationResult(true, enabled ? "Coalescable timer disabled (reboot recommended)." : "Coalescable timer settings restored.");
    }

    private Task<OperationResult> ApplyModernStandbyAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\Power",
        "CsEnabled",
        enabled ? 0 : 1,
        enabled ? "Modern Standby disabled (reboot required)." : "Modern Standby setting restored."));

    private async Task<OperationResult> ApplyEnergyPerformancePreferenceAsync(bool enabled)
    {
        var value = enabled ? "0" : "50";
        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_PROCESSOR PERFEPP {value}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_PROCESSOR PERFEPP {value}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "Energy Performance Preference set to performance." : "Energy Performance Preference restored.")
            : new OperationResult(false, "Failed to update Energy Performance Preference.");
    }

    private async Task<OperationResult> ApplyMinMaxProcessorStateAsync(bool enabled)
    {
        var minValue = enabled ? "100" : "5";
        var maxValue = "100";
        var minAc = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_PROCESSOR PROCTHROTTLEMIN {minValue}");
        var minDc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_PROCESSOR PROCTHROTTLEMIN {minValue}");
        var maxAc = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_PROCESSOR PROCTHROTTLEMAX {maxValue}");
        var maxDc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_PROCESSOR PROCTHROTTLEMAX {maxValue}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return minAc.ExitCode == 0 && minDc.ExitCode == 0 && maxAc.ExitCode == 0 && maxDc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "Minimum/maximum processor state forced to 100%." : "Processor state settings restored.")
            : new OperationResult(false, "Failed to update processor state limits.");
    }

    private async Task<OperationResult> ApplyMemoryCompressionAsync(bool enabled)
    {
        var command = enabled ? "Disable-MMAgent -MemoryCompression" : "Enable-MMAgent -MemoryCompression";
        var result = await RunPowerShellAsync(command, 60000);
        return ToOperationResult(result, enabled ? "Memory compression disabled." : "Memory compression enabled.", "Failed to update memory compression");
    }

    private Task<OperationResult> ApplyClearPagefileAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
        "ClearPageFileAtShutdown",
        enabled ? 1 : 0,
        enabled ? "Pagefile clear-on-shutdown enabled." : "Pagefile clear-on-shutdown disabled."));

    private Task<OperationResult> ApplyPrefetchAsync(bool enabled)
    {
        var prefetch = SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher", enabled ? 0 : 3, "Prefetch setting applied.");
        var superfetch = SetRegistryDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnableSuperfetch", enabled ? 0 : 3, "Prefetch setting applied.");
        return Task.FromResult(prefetch.Success && superfetch.Success
            ? new OperationResult(true, enabled ? "Prefetch disabled." : "Prefetch restored.")
            : new OperationResult(false, "Prefetch update had warnings."));
    }

    private async Task<OperationResult> ApplyRamDiagnosticsAsync(bool enabled)
    {
        var script = $@"
$tasks = @('\Microsoft\Windows\MemoryDiagnostic\ProcessMemoryDiagnosticEvents','\Microsoft\Windows\MemoryDiagnostic\RunFullMemoryDiagnostic')
$count = 0
foreach ($task in $tasks) {{
    try {{
        schtasks.exe /Change /TN $task /{(enabled ? "Disable" : "Enable")} | Out-Null
        $count++
    }}
    catch {{}}
}}
Write-Output $count";

        var result = await RunPowerShellAsync(script, 60000);
        return result.ExitCode == 0
            ? new OperationResult(true, enabled ? "RAM diagnostics tasks disabled." : "RAM diagnostics tasks restored.")
            : new OperationResult(false, "Failed to update RAM diagnostics tasks.");
    }

    private async Task<OperationResult> ApplySuperfetchAsync(bool enabled)
    {
        if (enabled)
        {
            var config = await RunProcessAsync("sc.exe", "config \"SysMain\" start= auto");
            var start = await RunProcessAsync("sc.exe", "start \"SysMain\"");
            return config.ExitCode == 0 && (start.ExitCode == 0 || start.ExitCode == 1056)
                ? new OperationResult(true, "Superfetch (SysMain) enabled.")
                : new OperationResult(false, "Failed to enable Superfetch.");
        }

        var stop = await RunProcessAsync("sc.exe", "stop \"SysMain\"");
        var disable = await RunProcessAsync("sc.exe", "config \"SysMain\" start= disabled");
        return disable.ExitCode == 0 && (stop.ExitCode == 0 || stop.ExitCode == 1062)
            ? new OperationResult(true, "Superfetch (SysMain) disabled.")
            : new OperationResult(false, "Failed to disable Superfetch.");
    }

    private Task<OperationResult> ApplySvchostSplitAsync(bool enabled) => Task.FromResult(SetRegistryQword(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control",
        "SvcHostSplitThresholdInKB",
        enabled ? 4_194_304L : 3_670_016L,
        "Svchost split threshold updated."));

    private async Task<OperationResult> ApplyIdleSleepStatesAsync(bool enabled)
    {
        var standbyAc = await RunProcessAsync("powercfg.exe", $"/change standby-timeout-ac {(enabled ? "0" : "30")}");
        var standbyDc = await RunProcessAsync("powercfg.exe", $"/change standby-timeout-dc {(enabled ? "0" : "15")}");
        var hiberAc = await RunProcessAsync("powercfg.exe", $"/change hibernate-timeout-ac {(enabled ? "0" : "180")}");
        var hiberDc = await RunProcessAsync("powercfg.exe", $"/change hibernate-timeout-dc {(enabled ? "0" : "60")}");
        return standbyAc.ExitCode == 0 && standbyDc.ExitCode == 0 && hiberAc.ExitCode == 0 && hiberDc.ExitCode == 0
            ? new OperationResult(true, enabled ? "Idle and sleep states disabled." : "Idle and sleep states restored.")
            : new OperationResult(false, "Failed to update idle/sleep state settings.");
    }

    private Task<OperationResult> ApplyMouseAccelerationAsync(bool enabled)
    {
        var speed = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", enabled ? "0" : "1", "Mouse acceleration setting applied.");
        var t1 = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", enabled ? "0" : "6", "Mouse acceleration setting applied.");
        var t2 = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", enabled ? "0" : "10", "Mouse acceleration setting applied.");
        return Task.FromResult(speed.Success && t1.Success && t2.Success
            ? new OperationResult(true, enabled ? "Mouse acceleration disabled." : "Mouse acceleration restored.")
            : new OperationResult(false, "Mouse acceleration update had warnings."));
    }

    private Task<OperationResult> ApplyPixelPerfectMouseAsync(bool enabled)
    {
        return ApplyMouseAccelerationAsync(enabled);
    }

    private async Task<OperationResult> ApplyUsbSelectiveSuspendAsync(bool enabled)
    {
        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_USB USBSELECTIVE {(enabled ? "0" : "1")}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_USB USBSELECTIVE {(enabled ? "0" : "1")}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "USB selective suspend disabled." : "USB selective suspend restored.")
            : new OperationResult(false, "Failed to apply USB selective suspend setting.");
    }

    private Task<OperationResult> ApplyStickyShortcutAsync(bool enabled)
    {
        var sticky = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", enabled ? "506" : "510", "Sticky key shortcut setting applied.");
        var toggle = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags", enabled ? "58" : "62", "Toggle key shortcut setting applied.");
        var keyboard = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", enabled ? "122" : "126", "Keyboard response shortcut setting applied.");
        return Task.FromResult(sticky.Success && toggle.Success && keyboard.Success
            ? new OperationResult(true, enabled ? "Sticky/Toggle shortcuts disabled." : "Sticky/Toggle shortcuts restored.")
            : new OperationResult(false, "Sticky/Toggle shortcut update had warnings."));
    }

    private Task<OperationResult> ApplyStickyKeysAsync(bool enabled)
    {
        var sticky = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", enabled ? "506" : "510", "Sticky Keys setting applied.");
        return Task.FromResult(sticky.Success
            ? new OperationResult(true, enabled ? "Sticky Keys disabled." : "Sticky Keys restored.")
            : new OperationResult(false, "Failed to update Sticky Keys."));
    }

    private Task<OperationResult> ApplyToggleKeysAsync(bool enabled)
    {
        var toggle = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags", enabled ? "58" : "62", "Toggle Keys setting applied.");
        return Task.FromResult(toggle.Success
            ? new OperationResult(true, enabled ? "Toggle Keys disabled." : "Toggle Keys restored.")
            : new OperationResult(false, "Failed to update Toggle Keys."));
    }

    private Task<OperationResult> ApplyKeyboardRepeatDelayAsync(bool enabled)
    {
        var delay = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardDelay", enabled ? "0" : "1", "Keyboard repeat delay setting applied.");
        var speed = SetRegistryString(RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardSpeed", enabled ? "31" : "20", "Keyboard repeat speed setting applied.");
        return Task.FromResult(delay.Success && speed.Success
            ? new OperationResult(true, enabled ? "Keyboard repeat delay reduced." : "Keyboard repeat delay restored.")
            : new OperationResult(false, "Failed to update keyboard repeat settings."));
    }

    private Task<OperationResult> ApplyDebugPollIntervalAsync(bool enabled)
    {
        return Task.FromResult(SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Kernel",
            "DebugPollInterval",
            enabled ? 1000 : 0,
            enabled ? "Debug poll interval set to 1000 ms." : "Debug poll interval restored."));
    }

    private Task<OperationResult> ApplyGameBarAsync(bool enabled)
    {
        var one = SetRegistryDword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", enabled ? 0 : 1, "Game Bar setting applied.");
        var two = SetRegistryDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", enabled ? 0 : 1, "Game Bar setting applied.");
        var three = SetRegistryDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", enabled ? 0 : 1, "Game Bar setting applied.");
        return Task.FromResult(one.Success && two.Success && three.Success
            ? new OperationResult(true, enabled ? "Game Bar background capture disabled." : "Game Bar background capture restored.")
            : new OperationResult(false, "Game Bar update had warnings."));
    }

    private Task<OperationResult> ApplyLastAccessAsync(bool enabled) => RunCommandOperationAsync("fsutil.exe", $"behavior set disablelastaccess {(enabled ? "1" : "0")}", enabled ? "NTFS last access updates disabled." : "NTFS last access updates restored.");
    private Task<OperationResult> ApplyHibernationAsync(bool enabled) => RunCommandOperationAsync("powercfg.exe", enabled ? "/h off" : "/h on", enabled ? "Hibernation disabled." : "Hibernation enabled.");
    private Task<OperationResult> ApplyTrimAsync(bool enabled) => RunCommandOperationAsync("fsutil.exe", $"behavior set DisableDeleteNotify {(enabled ? "0" : "1")}", enabled ? "TRIM enabled." : "TRIM disabled.");
    private Task<OperationResult> ApplyStorageSenseAsync(bool enabled) => Task.FromResult(SetRegistryDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", enabled ? 0 : 1, enabled ? "Storage Sense automation disabled." : "Storage Sense automation restored."));

    private async Task<OperationResult> ApplyDipmParkingAsync(bool enabled)
    {
        var value = enabled ? "1" : "3";
        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_DISK AHCIHIPM {value}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_DISK AHCIHIPM {value}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "DIPM parking disabled." : "DIPM parking restored.")
            : new OperationResult(false, "Failed to update DIPM parking.");
    }

    private async Task<OperationResult> ApplyHddParkingAsync(bool enabled)
    {
        var ac = await RunProcessAsync("powercfg.exe", $"/change disk-timeout-ac {(enabled ? "0" : "20")}");
        var dc = await RunProcessAsync("powercfg.exe", $"/change disk-timeout-dc {(enabled ? "0" : "10")}");
        return ac.ExitCode == 0 && dc.ExitCode == 0
            ? new OperationResult(true, enabled ? "HDD parking disabled." : "HDD parking restored.")
            : new OperationResult(false, "Failed to update HDD parking.");
    }

    private async Task<OperationResult> ApplyHipmParkingAsync(bool enabled)
    {
        var value = enabled ? "2" : "3";
        var ac = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_DISK AHCIHIPM {value}");
        var dc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_DISK AHCIHIPM {value}");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");
        return ac.ExitCode == 0 && dc.ExitCode == 0 && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "HIPM parking disabled." : "HIPM parking restored.")
            : new OperationResult(false, "Failed to update HIPM parking.");
    }

    private Task<OperationResult> ApplySsdPowersavingAsync(bool enabled) => Task.FromResult(SetRegistryDword(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device",
        "IdlePowerMode",
        enabled ? 0 : 1,
        enabled ? "SSD powersaving disabled." : "SSD powersaving restored."));

    private Task<OperationResult> ApplyWriteCacheFlushAsync(bool enabled) => RunCommandOperationAsync(
        "fsutil.exe",
        $"behavior set DisableFlushBuffers {(enabled ? "1" : "0")}",
        enabled ? "Write cache buffer flushing disabled." : "Write cache buffer flushing restored.");

    private async Task<OperationResult> ApplySsdSleepAsync(bool enabled)
    {
        var diskAc = await RunProcessAsync("powercfg.exe", $"/setacvalueindex scheme_current SUB_DISK DISKIDLE {(enabled ? "0" : "1200")}");
        var diskDc = await RunProcessAsync("powercfg.exe", $"/setdcvalueindex scheme_current SUB_DISK DISKIDLE {(enabled ? "0" : "600")}");
        var nvme = SetRegistryDword(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device",
            "TransitionLatencyTolerantMs",
            enabled ? 0 : 100,
            "SSD sleep policy updated.");
        var active = await RunProcessAsync("powercfg.exe", "/setactive scheme_current");

        return diskAc.ExitCode == 0 && diskDc.ExitCode == 0 && nvme.Success && active.ExitCode == 0
            ? new OperationResult(true, enabled ? "SSD sleep optimized." : "SSD sleep settings restored.")
            : new OperationResult(false, "Failed to update SSD sleep settings.");
    }

    private async Task<OperationResult> RunCommandOperationAsync(string fileName, string arguments, string successMessage)
    {
        var result = await RunProcessAsync(fileName, arguments);
        return ToOperationResult(result, successMessage, $"Failed running {fileName}");
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static OperationResult SetRegistryDword(RegistryHive hive, string path, string name, int value, string successMessage)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).CreateSubKey(path, writable: true);
            if (key is null)
            {
                return new OperationResult(false, $"Registry path unavailable: {path}");
            }

            key.SetValue(name, value, RegistryValueKind.DWord);
            return new OperationResult(true, successMessage);
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Registry update failed: {name} ({ex.Message})");
        }
    }

    private static OperationResult SetRegistryQword(RegistryHive hive, string path, string name, long value, string successMessage)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).CreateSubKey(path, writable: true);
            if (key is null)
            {
                return new OperationResult(false, $"Registry path unavailable: {path}");
            }

            key.SetValue(name, value, RegistryValueKind.QWord);
            return new OperationResult(true, successMessage);
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Registry update failed: {name} ({ex.Message})");
        }
    }

    private static OperationResult SetRegistryString(RegistryHive hive, string path, string name, string value, string successMessage)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).CreateSubKey(path, writable: true);
            if (key is null)
            {
                return new OperationResult(false, $"Registry path unavailable: {path}");
            }

            key.SetValue(name, value, RegistryValueKind.String);
            return new OperationResult(true, successMessage);
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Registry update failed: {name} ({ex.Message})");
        }
    }

    private static OperationResult DeleteRegistryValue(RegistryHive hive, string path, string name, string successMessage = "Registry value removed.")
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(path, writable: true);
            if (key is null)
            {
                return new OperationResult(false, $"Registry path not found: {path}");
            }

            key.DeleteValue(name, false);
            return new OperationResult(true, successMessage);
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Registry delete failed: {name} ({ex.Message})");
        }
    }

    private async Task<OperationResult> SetServiceStateIfExistsAsync(string serviceName, bool disable, string enableStartType)
    {
        var query = await RunProcessAsync("sc.exe", $"query \"{serviceName}\"");
        if (query.ExitCode != 0)
        {
            return new OperationResult(true, $"Service {serviceName} not present.");
        }

        return await SetServiceStateAsync(serviceName, disable, enableStartType);
    }

    private async Task<OperationResult> SetMsiForCompatibleDevicesAsync(bool enabled)
    {
        var script = $@"
$value = {(enabled ? 1 : 0)}
$count = 0
$roots = @('HKLM:\SYSTEM\CurrentControlSet\Enum\PCI','HKLM:\SYSTEM\CurrentControlSet\Enum\USB')
foreach ($root in $roots) {{
    if (Test-Path $root) {{
        Get-ChildItem -Path $root -Recurse -ErrorAction SilentlyContinue | ForEach-Object {{
            $msi = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
            if (Test-Path $msi) {{
                New-ItemProperty -Path $msi -Name MSISupported -PropertyType DWord -Value $value -Force -ErrorAction SilentlyContinue | Out-Null
                $count++
            }}
        }}
    }}
}}
Write-Output $count";

        var result = await RunPowerShellAsync(script, 180000);
        if (result.ExitCode != 0)
        {
            return ToOperationResult(result, string.Empty, "Failed to update MSI mode");
        }

        var countText = result.StandardOutput.Trim();
        return new OperationResult(true, enabled
            ? $"MSI mode enabled for compatible devices. Entries touched: {countText}."
            : $"MSI mode restored for compatible devices. Entries touched: {countText}.");
    }

    private async Task<OperationResult> SetUsbEnhancedPowerManagementAsync(bool disable)
    {
        var script = $@"
$value = {(disable ? 0 : 1)}
$count = 0
$roots = @('HKLM:\SYSTEM\CurrentControlSet\Enum\USB','HKLM:\SYSTEM\CurrentControlSet\Enum\HID')
foreach ($root in $roots) {{
    if (Test-Path $root) {{
        Get-ChildItem -Path $root -Recurse -ErrorAction SilentlyContinue | ForEach-Object {{
            $path = Join-Path $_.PSPath 'Device Parameters'
            if (Test-Path $path) {{
                New-ItemProperty -Path $path -Name EnhancedPowerManagementEnabled -PropertyType DWord -Value $value -Force -ErrorAction SilentlyContinue | Out-Null
                $count++
            }}
        }}
    }}
}}
Write-Output $count";

        var result = await RunPowerShellAsync(script, 180000);
        if (result.ExitCode != 0)
        {
            return ToOperationResult(result, string.Empty, "Failed to update USB enhanced power management");
        }

        return new OperationResult(true, disable
            ? "USB enhanced power management disabled for compatible devices."
            : "USB enhanced power management restored for compatible devices.");
    }

    private async Task<OperationResult> SetServiceStateAsync(string serviceName, bool disable, string enableStartType)
    {
        var startType = disable ? "disabled" : enableStartType;
        var config = await RunProcessAsync("sc.exe", $"config \"{serviceName}\" start= {startType}");
        if (config.ExitCode != 0)
        {
            return new OperationResult(false, $"Failed to configure service {serviceName}.");
        }

        var control = await RunProcessAsync("sc.exe", disable ? $"stop \"{serviceName}\"" : $"start \"{serviceName}\"");
        if (control.ExitCode != 0 && !disable)
        {
            return new OperationResult(false, $"Configured {serviceName}, but start command failed.");
        }

        return new OperationResult(true, disable ? $"Service {serviceName} disabled." : $"Service {serviceName} restored.");
    }

    private async Task<CommandResult> RunPowerShellAsync(string script, int timeoutMs = 45000)
    {
        var bytes = Encoding.Unicode.GetBytes(script);
        var encoded = Convert.ToBase64String(bytes);
        return await RunProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeoutMs);
    }

    private async Task<CommandResult> RunProcessAsync(string fileName, string arguments, int timeoutMs = 45000)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var finished = await Task.WhenAny(waitTask, Task.Delay(timeoutMs));

            if (finished != waitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return CommandResult.Timeout();
            }

            await waitTask;
            var output = await outputTask;
            var error = await errorTask;
            return new CommandResult(process.ExitCode, output, error, false);
        }
        catch (Exception ex)
        {
            return CommandResult.Failed(ex.Message);
        }
    }

    private static OperationResult ToOperationResult(CommandResult commandResult, string successMessage, string failurePrefix)
    {
        if (commandResult.TimedOut)
        {
            return new OperationResult(false, $"{failurePrefix}: timed out.");
        }

        if (commandResult.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(commandResult.StandardError) ? commandResult.StandardOutput : commandResult.StandardError;
            var compact = detail.Trim();
            if (compact.Length > 140)
            {
                compact = compact[..140] + "...";
            }

            return new OperationResult(false, $"{failurePrefix}: {compact}");
        }

        return new OperationResult(true, successMessage);
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);
}
