using System.Runtime.InteropServices;
using System.Windows;
using FleetView.Services;
using FleetView.ViewModels;

namespace FleetView;

/// <summary>
/// Interaction logic for App.xaml. Composes the object graph and shows the main window.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DisableBackgroundThrottling();

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show($"Unexpected error:\n\n{args.Exception.Message}",
                "FleetView", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            var catalog = CatalogLoader.Load();
            var modifications = ModificationLoader.Load();
            var locker = new ShipLockerReader();
            var journalDir = System.IO.Path.GetDirectoryName(locker.FilePath) ?? "";
            var journal = new JournalReader(journalDir);

            bool mock = Environment.GetEnvironmentVariable("FLEETVIEW_MOCK") == "1";
            // Always our own hosted relay - no Inara scraping path exists in this app anymore.
            // FLEETVIEW_RELAY_URL can still point at a different (e.g. local test) relay instance.
            ICarrierMarketSource market = mock
                ? new MockMarketSource()
                : new RelayMarketSource(
                    Environment.GetEnvironmentVariable("FLEETVIEW_RELAY_URL") ?? "http://77.42.73.218:5085");
            // Distances need EDSM; skip it in mock/offline mode so tests don't hit the network.
            ICoordinateSource? coords = mock ? null : new EdsmCoordinateSource();

            var vm = new MainViewModel(catalog, modifications, locker, market, journal, coords);

            var window = new MainWindow { DataContext = vm };
            window.Show();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show(
                $"FleetView failed to start:\n\n{ex.Message}",
                "FleetView", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Opts this process out of Windows' "Efficiency Mode" (EcoQoS) power throttling, which the
    /// OS otherwise applies to unfocused/background windows and lowers their thread scheduling
    /// priority and timer resolution, the most likely reason live inventory/dock updates were
    /// previously stalling while this app sat open but unfocused (e.g. on a second monitor while
    /// the game has focus). Polling itself was also moved off the UI-thread DispatcherTimer onto
    /// a threadpool Timer for the same reason (see MainViewModel.SetupWatcher) - this call
    /// addresses it at the process level too, for anything else Windows might throttle. Silently
    /// does nothing on Windows versions that don't support this (pre-Windows 11 22H2-ish); never
    /// allowed to affect startup.
    /// </summary>
    private static void DisableBackgroundThrottling()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0 // 0 = this flag is explicitly OFF, i.e. throttling disabled
            };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
                (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { /* best effort, must never block startup */ }
    }

    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static void LogCrash(Exception ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "Data");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "fleetview-crash.log");
            System.IO.File.AppendAllText(path,
                $"{DateTime.Now:s}\n{ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}
