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
            ReportUnexpectedError(args.Exception);
            args.Handled = true;
        };

        try
        {
            var catalog = CatalogLoader.Load();
            var modifications = ModificationLoader.Load();
            var locker = new ShipLockerReader();
            // Null when the locker path has no directory component. JournalReader takes that as
            // "no journals available" and says so; the empty string this used to substitute would
            // instead have been resolved against whatever the process's working directory happened
            // to be, which is not a place this app has any business reading game logs from.
            var journal = new JournalReader(System.IO.Path.GetDirectoryName(locker.FilePath));

            bool mock = Environment.GetEnvironmentVariable("FLEETVIEW_MOCK") == "1";
            ICarrierMarketSource market = mock
                ? new MockMarketSource()
                : new RelayMarketSource(ResolveRelayUrl());
            // Distances need EDSM; skip it in mock/offline mode so tests don't hit the network.
            ICoordinateSource? coords = mock ? null : new EdsmCoordinateSource();

            var vm = new MainViewModel(catalog, modifications, locker, market, journal, coords);

            var window = new MainWindow { DataContext = vm };
            window.Show();
        }
        catch (Exception ex)
        {
            // The message is shown because the reasons startup fails here are the ones the user
            // can act on: "catalog.json entry 3 has no key", "Data\catalog.json not found".
            MessageBox.Show(
                $"FleetFinder failed to start:\n\n{ex.Message}",
                "FleetFinder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Our own hosted relay. No Inara scraping path exists in this app anymore.
    /// </summary>
    /// <remarks>
    /// HTTPS via a Caddy reverse proxy in front of the relay. The sslip.io hostname resolves
    /// straight to the server's own IP, so Let's Encrypt can issue a real certificate for it
    /// without owning a registered domain - worth being clear about what that does and does not
    /// buy: the connection is encrypted and cannot be read or altered in transit, but because the
    /// name is derived from the address, the certificate attests to whoever controls that address
    /// at the time rather than to this project. A registered domain is what would fix that, and
    /// nothing in the code can substitute for one. The old plain-HTTP :5085 listener stays up
    /// alongside it so already-installed builds pointing at the bare IP keep working unaffected.
    /// </remarks>
    private const string DefaultRelayUrl = "https://77-42-73-218.sslip.io";

    /// <summary>
    /// The relay to query: <c>FLEETVIEW_RELAY_URL</c> if it is one this app will talk to, and
    /// <see cref="DefaultRelayUrl"/> otherwise.
    /// </summary>
    /// <remarks>
    /// An environment variable is ordinary process configuration rather than a hostile input, but
    /// it decides where every market query in the app goes, and it was previously passed through
    /// with no check that it was even a URL, let alone an encrypted one.
    /// </remarks>
    private static string ResolveRelayUrl()
    {
        var configured = Environment.GetEnvironmentVariable("FLEETVIEW_RELAY_URL");
        if (string.IsNullOrWhiteSpace(configured)) return DefaultRelayUrl;

        return RelayMarketSource.TryNormaliseBaseUrl(configured, out var normalised)
            ? normalised
            : DefaultRelayUrl;
    }

    /// <summary>
    /// How many error dialogs one session will show before it stops. A fault that repeats every
    /// poll tick would otherwise stack dialogs faster than they can be dismissed - which is what
    /// happened in practice on the mid-edit refresh bug, which fired once a second.
    /// </summary>
    private const int MaxErrorDialogs = 3;

    private static int _errorDialogsShown;

    /// <summary>
    /// Tells the user their action failed, and stops after <see cref="MaxErrorDialogs"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exception message is shown. It is written for whoever is reading a stack trace rather
    /// than for a user, but with no log file there is nowhere else for it to go, and a report of
    /// "it said something went wrong" cannot be acted on. Worth knowing what that trades away: the
    /// message can carry a path or a fragment of a server response, so it is not something to put
    /// on screen if this app ever gets used while streaming.
    /// </para>
    /// <para>
    /// The exception stays handled. This is a WPF shell where an escaping exception is almost
    /// always one action failing - a search, a refresh tick, a file read - and letting it end the
    /// process would discard a whole session's search results over a failure the next tick would
    /// recover from anyway. What it does not do is pretend nothing happened: the affected work is
    /// abandoned, the user is told, and the inventory poll and search paths both rebuild their own
    /// state on the next pass rather than continuing from a half-finished one.
    /// </para>
    /// </remarks>
    private static void ReportUnexpectedError(Exception ex)
    {
        if (_errorDialogsShown >= MaxErrorDialogs) return;
        _errorDialogsShown++;

        var suffix = _errorDialogsShown == MaxErrorDialogs
            ? "\n\nFurther errors this session will not be reported."
            : "";

        MessageBox.Show(
            $"Something went wrong and that action was cancelled. FleetFinder is still running.\n\n{ex.Message}{suffix}",
            "FleetFinder", MessageBoxButton.OK, MessageBoxImage.Warning);
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

}
