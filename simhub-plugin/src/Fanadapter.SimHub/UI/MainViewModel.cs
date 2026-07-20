using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using Fanadapter.Core;

namespace Fanadapter.SimHub.UI
{
    /// <summary>
    /// View model behind the settings pane. Owns the marshalling boundary: the
    /// session raises its events on the serial reader thread, and everything
    /// exposed from here is touched on the UI thread only.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// Console output is for diagnosis, not history — an unbounded buffer
        /// would grow for as long as SimHub runs.
        /// </summary>
        private const int MaxLogLines = 500;

        private readonly FanadapterPlugin _plugin;
        private readonly AdapterSession _session;
        private readonly Dispatcher _dispatcher;

        public MainViewModel(FanadapterPlugin plugin)
        {
            _plugin = plugin;
            _session = plugin.Session;
            _dispatcher = Dispatcher.CurrentDispatcher;

            ConnectCommand = new RelayCommand(() => _ = ConnectAsync(), () => !IsBusy && !IsConnected);
            DisconnectCommand = new RelayCommand(Disconnect, () => !IsBusy && IsConnected);
            RefreshPortsCommand = new RelayCommand(RefreshPorts, () => !IsBusy);
            SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsBusy && IsConnected && IsDirty);
            ResetCommand = new RelayCommand(() => _ = ResetAsync(), () => !IsBusy && IsConnected);
            RebootCommand = new RelayCommand(() => _ = RebootAsync(), () => !IsBusy && IsConnected);
            ClearLogCommand = new RelayCommand(() => LogLines.Clear());

            _session.StateChanged += OnSessionStateChanged;
            _session.LogLine += OnLogLine;
            _session.DevicesChanged += OnDevicesChanged;
            _session.LiveInput += OnLiveInput;

            BuildAxisEditors();
            ToggleStreamingCommand = new RelayCommand(ToggleStreaming, () => CanStream);
            RefreshPropertyListCommand = new RelayCommand(RefreshPropertyList);
            ReleaseOutputsCommand = new RelayCommand(ReleaseOutputs, () => IsConnected);

            // 20 Hz. Live input frames arrive faster than that and applying each
            // one straight to the UI would post a dispatcher callback per frame
            // per slot; coalescing to a fixed tick caps the work regardless of
            // how many devices are moving, and stays responsive enough that a
            // button press still looks instant.
            _readoutTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            _readoutTimer.Tick += (s, e) => OnTick();
            _readoutTimer.Start();

            RefreshPorts();
            SelectedPort = Ports.FirstOrDefault(p => p.PortName == plugin.Settings.PortName)
                           ?? Ports.FirstOrDefault(p => p.IsLikelyAdapter)
                           ?? Ports.FirstOrDefault();

            SyncFromSession();
        }

        // ---------- Connection ----------

        public ObservableCollection<SerialPortInfo> Ports { get; } = new ObservableCollection<SerialPortInfo>();

        private SerialPortInfo _selectedPort;
        public SerialPortInfo SelectedPort
        {
            get => _selectedPort;
            set { _selectedPort = value; OnPropertyChanged(); RaiseCommandStates(); }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); RaiseCommandStates(); }
        }

        public bool IsConnected => _session.IsConnected;

        public string StatusMessage => _session.StatusMessage;

        public string FirmwareBadge
        {
            get
            {
                var v = _session.Version;
                if (v == null) return string.Empty;
                return string.Format("{0} {1} · protocol {2}", v.Firmware, v.Version, v.Protocol);
            }
        }

        /// <summary>
        /// Shown next to the drive controls so "why is this greyed out" has an
        /// answer on screen rather than in the log.
        /// </summary>
        public string DirectOutputNote
        {
            get
            {
                if (!IsConnected) return string.Empty;
                return _session.SupportsDirectOutput
                    ? "This firmware supports PC-driven output."
                    : "This firmware has no direct-output commands — PC-driven output needs the STM32 build.";
            }
        }

        public bool SupportsDirectOutput => _session.SupportsDirectOutput;

        public RelayCommand ConnectCommand { get; }
        public RelayCommand DisconnectCommand { get; }
        public RelayCommand RefreshPortsCommand { get; }
        public RelayCommand SaveCommand { get; }
        public RelayCommand ResetCommand { get; }
        public RelayCommand RebootCommand { get; }
        public RelayCommand ClearLogCommand { get; }

        public void RefreshPorts()
        {
            var selected = SelectedPort?.PortName;
            Ports.Clear();
            foreach (var p in PortLocator.List()) Ports.Add(p);
            SelectedPort = Ports.FirstOrDefault(p => p.PortName == selected)
                           ?? Ports.FirstOrDefault(p => p.IsLikelyAdapter)
                           ?? Ports.FirstOrDefault();
        }

        public async Task ConnectAsync()
        {
            var port = SelectedPort;
            if (port == null)
            {
                AppendLog("No serial ports found.");
                return;
            }

            IsBusy = true;
            try
            {
                try
                {
                    await _session.ConnectAsync(port.PortName);
                }
                catch (Exception ex)
                {
                    AppendLog("connect failed: " + ex.Message);
                    return;
                }

                // Past this point the adapter is connected and usable. The steps
                // below are conveniences, so each reports its own failure rather
                // than being folded into "connect failed" — otherwise a settings
                // write that goes wrong reads as a dead adapter, and the
                // telemetry enable below never runs.
                try
                {
                    _plugin.Settings.PortName = port.PortName;
                    _plugin.SaveSettings();
                }
                catch (Exception ex)
                {
                    AppendLog("connected, but the port could not be remembered: " + ex.Message);
                }

                try
                {
                    // Telemetry is opt-in per connection; the adapter doesn't
                    // remember it across a reconnect. Input frames only arrive
                    // when something actually moves, so leaving them on costs
                    // nothing while the rig is idle.
                    await _session.Protocol.SetLiveOutputsAsync(true);
                    await _session.Protocol.SetLiveInputsAsync(true);
                }
                catch (Exception ex)
                {
                    AppendLog("connected, but telemetry could not be enabled: " + ex.Message);
                }
            }
            finally
            {
                IsBusy = false;
                SyncFromSession();
            }
        }

        public void Disconnect()
        {
            _session.Disconnect();
            SyncFromSession();
        }

        // ---------- Config lifecycle ----------

        private bool _isDirty;
        public bool IsDirty
        {
            get => _isDirty;
            set { _isDirty = value; OnPropertyChanged(); RaiseCommandStates(); }
        }

        private async Task SaveAsync()
        {
            IsBusy = true;
            try
            {
                await _session.Protocol.SaveConfigAsync();
                IsDirty = false;
                AppendLog("config saved to the adapter.");
            }
            catch (Exception ex)
            {
                AppendLog("save failed: " + ex.Message);
            }
            finally { IsBusy = false; }
        }

        private async Task ResetAsync()
        {
            IsBusy = true;
            try
            {
                // Defaults land in the adapter's RAM only — nothing is persisted
                // until Save, so this stays undoable by reconnecting.
                await _session.Protocol.ResetConfigAsync();
                await _session.ReloadConfigAsync();
                IsDirty = true;
                AppendLog("config reset to defaults (not yet saved).");
            }
            catch (Exception ex)
            {
                AppendLog("reset failed: " + ex.Message);
            }
            finally { IsBusy = false; SyncFromSession(); }
        }

        private async Task RebootAsync()
        {
            IsBusy = true;
            try
            {
                await _session.Protocol.RebootAsync();
                AppendLog("reboot requested; the port will drop.");
            }
            catch (Exception ex)
            {
                // The board often drops USB before the reply lands, so a timeout
                // here means it worked, not that it failed.
                AppendLog("reboot: " + ex.Message);
            }
            finally
            {
                _session.Disconnect();
                IsBusy = false;
                SyncFromSession();
            }
        }

        // ---------- Devices on the adapter's own USB hub ----------

        public ObservableCollection<DeviceSlotViewModel> Devices { get; } =
            new ObservableCollection<DeviceSlotViewModel>();

        /// <summary>
        /// Newest live frame per slot, waiting to be applied on the next tick.
        /// Written from the serial reader thread, drained on the UI thread.
        /// </summary>
        private readonly ConcurrentDictionary<int, LiveSlot> _pendingLive =
            new ConcurrentDictionary<int, LiveSlot>();

        private string _devicesSummary = "Not connected.";
        public string DevicesSummary
        {
            get => _devicesSummary;
            private set { _devicesSummary = value; OnPropertyChanged(); }
        }

        private void OnLiveInput(LiveSlot slot)
        {
            // Overwrite rather than queue: only the newest state matters, and
            // this runs on the reader thread where doing less is better.
            _pendingLive[slot.Slot] = slot;
        }

        private void OnDevicesChanged() => RunOnUi(RebuildDeviceList);

        private void RebuildDeviceList()
        {
            // Only slots holding a device: the pool is a fixed 8 and rendering
            // four empty cards would bury the real ones.
            var connected = _session.Devices.Where(d => d.Connected).OrderBy(d => d.Slot).ToList();

            for (int i = Devices.Count - 1; i >= 0; i--)
            {
                if (connected.All(d => d.Slot != Devices[i].Slot)) Devices.RemoveAt(i);
            }

            foreach (var device in connected)
            {
                var existing = Devices.FirstOrDefault(v => v.Slot == device.Slot);
                if (existing != null) existing.Apply(device);
                else Devices.Add(new DeviceSlotViewModel(device));
            }

            var reordered = Devices.OrderBy(d => d.Slot).ToList();
            for (int i = 0; i < reordered.Count; i++)
            {
                int current = Devices.IndexOf(reordered[i]);
                if (current != i) Devices.Move(current, i);
            }

            if (!IsConnected) DevicesSummary = "Not connected.";
            else if (connected.Count == 0) DevicesSummary = "No devices on the adapter's USB hub.";
            else DevicesSummary = string.Format("{0} of {1} pool slots in use.",
                connected.Count, _session.Devices.Count);
        }

        private void ApplyPendingLive()
        {
            if (_pendingLive.IsEmpty) return;

            foreach (var slot in _pendingLive.Keys.ToList())
            {
                LiveSlot live;
                if (!_pendingLive.TryRemove(slot, out live)) continue;

                var device = Devices.FirstOrDefault(d => d.Slot == slot);
                device?.Apply(live);
            }
        }

        // ---------- SimHub drive (PC-attached devices) ----------

        private readonly DispatcherTimer _readoutTimer;

        public ObservableCollection<AxisSourceViewModel> AxisSources { get; } =
            new ObservableCollection<AxisSourceViewModel>();

        /// <summary>
        /// Every property SimHub currently publishes, for the source pickers.
        /// Fetched on demand rather than continuously — the list runs to
        /// thousands of entries and only changes when plugins or games do.
        /// </summary>
        public ObservableCollection<string> AvailableProperties { get; } = new ObservableCollection<string>();

        private string _propertyFilter = string.Empty;
        public string PropertyFilter
        {
            get => _propertyFilter;
            set { _propertyFilter = value ?? string.Empty; OnPropertyChanged(); RefreshPropertyList(); }
        }

        public RelayCommand ToggleStreamingCommand { get; }
        public RelayCommand RefreshPropertyListCommand { get; }
        public RelayCommand ReleaseOutputsCommand { get; }

        /// <summary>
        /// Streaming needs a connection, a firmware that implements the
        /// direct-output commands, and at least one configured pedal source.
        /// </summary>
        public bool CanStream =>
            IsConnected && _session.SupportsDirectOutput && AxisSources.Any(a => a.IsConfigured);

        public bool IsStreaming => _plugin.Drive != null && _plugin.Drive.IsStreaming;

        public string StreamingButtonText => IsStreaming ? "Stop driving pedals" : "Start driving pedals";

        public string StreamingStatus
        {
            get
            {
                if (!IsConnected) return "Connect to the adapter first.";
                if (!_session.SupportsDirectOutput) return "This firmware cannot be driven from the PC.";
                if (!AxisSources.Any(a => a.IsConfigured)) return "Set a source property on at least one pedal.";
                if (!IsStreaming) return "Idle — the adapter's own USB mapping is in control.";

                var error = _plugin.Drive?.LastError;
                return error == null
                    ? "Driving pedals from this PC."
                    : "Driving, but the last update failed: " + error;
            }
        }

        private void BuildAxisEditors()
        {
            var drive = _plugin.Settings.Drive;
            void OnEdited()
            {
                _plugin.SaveSettings();
                RunOnUi(() =>
                {
                    OnPropertyChanged(nameof(CanStream));
                    OnPropertyChanged(nameof(StreamingStatus));
                    ToggleStreamingCommand.RaiseCanExecuteChanged();
                });
            }

            AxisSources.Add(new AxisSourceViewModel("throttle", "Throttle", drive.Throttle, OnEdited));
            AxisSources.Add(new AxisSourceViewModel("brake", "Brake", drive.Brake, OnEdited));
            AxisSources.Add(new AxisSourceViewModel("clutch", "Clutch", drive.Clutch, OnEdited));
            AxisSources.Add(new AxisSourceViewModel("handbrake", "Handbrake", drive.Handbrake, OnEdited));
        }

        public void RefreshPropertyList()
        {
            AvailableProperties.Clear();

            var pm = _plugin.PluginManager;
            if (pm == null) return;

            IEnumerable<string> names;
            try { names = pm.GetAllPropertiesNames(); }
            catch (Exception ex)
            {
                AppendLog("could not read SimHub's property list: " + ex.Message);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_propertyFilter))
            {
                names = names.Where(n => n.IndexOf(_propertyFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // Capped because the unfiltered list is long enough to make the
            // combo box unusable; the filter box is how you get to the rest.
            foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(500))
            {
                AvailableProperties.Add(name);
            }
        }

        private void ToggleStreaming()
        {
            var drive = _plugin.Drive;
            if (drive == null) return;

            if (drive.IsStreaming)
            {
                drive.StopStreaming();
                // Streaming stopped, but the last values it sent are still
                // latched in the firmware — release so the adapter's own mapping
                // takes the pedals back.
                drive.ReleaseAll();
                _plugin.Settings.Drive.AxisStreamingEnabled = false;
            }
            else
            {
                drive.StartStreaming();
                _plugin.Settings.Drive.AxisStreamingEnabled = true;
            }

            _plugin.SaveSettings();
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(StreamingButtonText));
            OnPropertyChanged(nameof(StreamingStatus));
        }

        private void ReleaseOutputs()
        {
            _plugin.Drive?.ReleaseAll();
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(StreamingButtonText));
            OnPropertyChanged(nameof(StreamingStatus));
            AppendLog("outputs released — the adapter's own mapping is back in control.");
        }

        private void OnTick()
        {
            ApplyPendingLive();
            UpdateReadouts();
        }

        private void UpdateReadouts()
        {
            var pm = _plugin.PluginManager;
            foreach (var axis in AxisSources)
            {
                object value = null;
                if (pm != null && axis.IsConfigured)
                {
                    // A property name that no longer exists throws rather than
                    // returning null in some SimHub builds.
                    try { value = pm.GetPropertyValue(axis.PropertyName); }
                    catch { value = null; }
                }
                axis.UpdateReadout(value);
            }

            OnPropertyChanged(nameof(StreamingStatus));
        }

        // ---------- Logs ----------

        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        private void OnLogLine(string line) => AppendLog(line);

        private void AppendLog(string line) => RunOnUi(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        });

        // ---------- Plumbing ----------

        private void OnSessionStateChanged() => RunOnUi(SyncFromSession);

        private void SyncFromSession()
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(FirmwareBadge));
            OnPropertyChanged(nameof(SupportsDirectOutput));
            OnPropertyChanged(nameof(DirectOutputNote));
            OnPropertyChanged(nameof(CanStream));
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(StreamingButtonText));
            OnPropertyChanged(nameof(StreamingStatus));
            RaiseCommandStates();
        }

        private void RaiseCommandStates() => RunOnUi(() =>
        {
            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
            RefreshPortsCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
            RebootCommand.RaiseCanExecuteChanged();
            ToggleStreamingCommand.RaiseCanExecuteChanged();
            ReleaseOutputsCommand.RaiseCanExecuteChanged();
        });

        /// <summary>
        /// Everything that can touch WPF goes through here, rather than trusting
        /// awaits to resume on the UI thread. They mostly do — but the session's
        /// internals use ConfigureAwait(false), so a continuation can land on a
        /// pool thread, and raising CanExecuteChanged from there throws when WPF
        /// reads the bound Button's Command property.
        /// </summary>
        private void RunOnUi(Action action)
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action);
        }

        public void Detach()
        {
            _readoutTimer.Stop();
            _session.StateChanged -= OnSessionStateChanged;
            _session.LogLine -= OnLogLine;
            _session.DevicesChanged -= OnDevicesChanged;
            _session.LiveInput -= OnLiveInput;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            RunOnUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}
