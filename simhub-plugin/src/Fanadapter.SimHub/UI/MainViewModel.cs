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
using Newtonsoft.Json.Linq;

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

            // The application's dispatcher, not whichever thread happened to
            // construct this object. Dispatcher.CurrentDispatcher *creates* a
            // dispatcher for the calling thread if it has none — and a thread
            // that never pumps messages runs nothing queued against it, so both
            // timers and every marshalled notification would sit in a queue
            // forever. The pane would still take button presses (WPF invokes
            // commands directly) while showing nothing live: indistinguishable,
            // from the outside, from a detached view model.
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            ConnectCommand = new RelayCommand(() => _ = ConnectAsync(), () => !IsBusy && !IsConnected);
            DisconnectCommand = new RelayCommand(Disconnect, () => !IsBusy && IsConnected);
            RefreshPortsCommand = new RelayCommand(RefreshPorts, () => !IsBusy);
            SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsBusy && IsConnected && IsDirty);
            ResetCommand = new RelayCommand(() => _ = ResetAsync(), () => !IsBusy && IsConnected);
            RebootCommand = new RelayCommand(() => _ = RebootAsync(), () => !IsBusy && IsConnected);
            ClearLogCommand = new RelayCommand(() => LogLines.Clear());

            BuildChannels();
            StartCaptureCommand = new ParameterCommand(StartCapture, () => IsConnected);
            ClearBindingCommand = new ParameterCommand(ClearBinding, () => IsConnected);
            CancelCaptureCommand = new RelayCommand(CancelCapture, () => IsCapturing);

            TestGearCommand = new ParameterCommand(TestGear, () => IsConnected);
            TestPulseCommand = new ParameterCommand(TestPulse, () => IsConnected);
            TestAxisCommand = new ParameterCommand(TestAxis, () => IsConnected);
            RearmPedalsCommand = new RelayCommand(RearmPedals, () => IsConnected);
            UsbStatusCommand = new RelayCommand(UsbStatus, () => IsConnected);
            UsbKickCommand = new RelayCommand(UsbKick, () => IsConnected);

            BuildAxisEditors();
            ToggleStreamingCommand = new RelayCommand(ToggleStreaming, () => CanStream);
            RefreshPropertyListCommand = new RelayCommand(RefreshPropertyList);
            ReleaseOutputsCommand = new RelayCommand(ReleaseOutputs, () => IsConnected);
            DetectAxisCommand = new ParameterCommand(ToggleAxisDetect);

            // Reconnect after the flash goes through ConnectAsync so the port gets remembered and
            // telemetry re-enabled exactly like a manual connect.
            Firmware = new FirmwareUpdateViewModel(
                _session, plugin.Settings.FirmwareUrl, AppendLog, RunOnUi, ConnectAsync);
            _ = Firmware.RefreshManifestAsync();

            // Live input runs at ~60 Hz on Normal priority. Coalescing frames to
            // a tick still caps the work no matter how many devices are moving,
            // but the priority matters more than the interval: Background only
            // runs when WPF has nothing else to do, and inside an app as busy as
            // SimHub that starves for long enough to be plainly visible as lag
            // when pressing a button. The per-tick work here is a handful of
            // property notifications, so Normal cannot crowd out rendering.
            // Both timers are bound to _dispatcher explicitly for the same
            // reason it is resolved from the application: a DispatcherTimer
            // built with the default constructor attaches to the current
            // thread's dispatcher, which is not necessarily the one that runs.
            _liveTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _liveTimer.Tick += (s, e) => OnLiveTick();

            // Serial polling stays on the slow timer; moving pedal readouts
            // use the live timer so Background priority cannot starve them.
            _slowTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            _slowTimer.Tick += (s, e) => OnSlowTick();

            RefreshPorts();
            SelectedPort = Ports.FirstOrDefault(p => p.PortName == plugin.Settings.PortName)
                           ?? Ports.FirstOrDefault(p => p.IsLikelyAdapter)
                           ?? Ports.FirstOrDefault();

            Attach();
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

        public FirmwareUpdateViewModel Firmware { get; }

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
                    // Output telemetry stays on for the exposed dash properties.
                    // SyncInputTelemetry owns the input stream for this pane.
                    await _session.Protocol.SetLiveOutputsAsync(true);
                }
                catch (Exception ex)
                {
                    AppendLog("connected, but telemetry could not be enabled: " + ex.Message);
                }

                try
                {
                    // Picks the pedal stream back up if it was on when the plugin
                    // last stopped. No-op when it wasn't, or when it already
                    // resumed on the plugin's own auto-connect.
                    _plugin.Drive?.ResumeIfEnabled();
                }
                catch (Exception ex)
                {
                    AppendLog("connected, but pedal streaming could not resume: " + ex.Message);
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
            // Overrides are sticky and the firmware has no timeout, so closing the
            // port mid-stream would leave the wheelbase latched on the last pedal
            // values with nothing able to tell it otherwise. Hand them back first.
            // The persisted "driving pedals" choice is untouched — reconnecting
            // resumes it.
            _plugin.Drive?.ReleaseAll();
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

                ApplyLiveToMappings(live);
            }
        }

        // ---------- Mappings (adapter's own devices → wheelbase channels) ----------

        private static readonly string[] SequentialChannels = { "shift_up", "shift_down" };

        public ObservableCollection<ChannelViewModel> Channels { get; } =
            new ObservableCollection<ChannelViewModel>();

        private CaptureEngine _capture;
        private BindingSlotViewModel _captureTarget;

        public RelayCommand StartCaptureCommand { get; private set; }
        public RelayCommand ClearBindingCommand { get; private set; }
        public RelayCommand CancelCaptureCommand { get; private set; }

        public bool IsLatchMode
        {
            get => _session.Config != null && _session.Config.GearMode == GearMode.Latch;
            set { _ = SetGearModeAsync(value ? GearMode.Latch : GearMode.Hold); }
        }

        public string GearModeExplanation => IsLatchMode
            ? "Latch: pressing a gear binding switches to that gear and stays there until another one is pressed. Suits keyboards and gamepads, and makes the neutral binding meaningful."
            : "Hold: a gear is engaged only while its binding is held, like a real H-pattern shifter. Two gears held at once falls back to neutral.";

        private void BuildChannels()
        {
            Channels.Clear();

            foreach (var gear in Schema.GearKeys)
            {
                Channels.Add(new ChannelViewModel(gear, "H-pattern shifter", prefersButton: true));
            }
            foreach (var seq in SequentialChannels)
            {
                Channels.Add(new ChannelViewModel(seq, "Sequential", prefersButton: true));
            }
            Channels.Add(new ChannelViewModel("handbrake", "Handbrake", prefersButton: false));
            foreach (var pedal in new[] { "throttle", "brake", "clutch" })
            {
                Channels.Add(new ChannelViewModel(pedal, "Pedals", prefersButton: false));
            }
        }

        private void LoadChannelsFromConfig()
        {
            var config = _session.Config;
            if (config == null) return;

            foreach (var channel in Channels)
            {
                var bindings = config.GetChannel(channel.Key);
                channel.Slots.Clear();
                for (int slot = 0; slot < Schema.MaxBindingsPerChannel; slot++)
                {
                    channel.Slots.Add(new BindingSlotViewModel(channel, slot, bindings[slot], PushBindingField));
                }
                channel.RefreshVisibleSlots();

                // Neutral is only bindable in latch mode: in hold mode a gear is
                // engaged while held and neutral is simply the absence of one,
                // so a neutral binding would have nothing to do.
                channel.IsVisible = channel.Key != "gear_N" || IsLatchMode;
            }

            OnPropertyChanged(nameof(IsLatchMode));
            OnPropertyChanged(nameof(GearModeExplanation));
        }

        private async Task SetGearModeAsync(GearMode mode)
        {
            if (!IsConnected) return;
            try
            {
                await _session.Protocol.SetGearModeAsync(mode);
                if (_session.Config != null) _session.Config.GearMode = mode;
                IsDirty = true;

                foreach (var channel in Channels)
                {
                    if (channel.Key == "gear_N") channel.IsVisible = mode == GearMode.Latch;
                }

                OnPropertyChanged(nameof(IsLatchMode));
                OnPropertyChanged(nameof(GearModeExplanation));
            }
            catch (Exception ex)
            {
                AppendLog("could not change the shifter mode: " + ex.Message);
            }
        }

        /// <summary>
        /// Pushes a single edited field. set_binding merges rather than
        /// replaces, so sending just the changed value leaves the rest of the
        /// binding alone — and avoids a race where a stale local copy would
        /// overwrite something the capture flow just wrote.
        /// </summary>
        private void PushBindingField(BindingSlotViewModel slot, string field, object value)
        {
            if (!IsConnected) return;

            var payload = new JObject { [field] = JToken.FromObject(value) };
            _ = SendBindingAsync(slot.Channel.Key, slot.Slot, payload);
        }

        private async Task SendBindingAsync(string channel, int slot, JObject payload)
        {
            try
            {
                await _session.Protocol.SetBindingAsync(channel, slot, payload);
                IsDirty = true;
            }
            catch (Exception ex)
            {
                AppendLog(string.Format("could not update {0} slot {1}: {2}", channel, slot, ex.Message));
            }
        }

        private void StartCapture(object parameter)
        {
            var target = parameter as BindingSlotViewModel;
            if (target == null || !IsConnected) return;

            CancelCapture();

            _captureTarget = target;
            _capture = new CaptureEngine(target.Channel.Key, target.Slot, DateTime.UtcNow,
                s => _session.Devices.FirstOrDefault(d => d.Slot == s));

            target.IsCapturing = true;
            target.CaptureHint = "Hold still…";
            OnPropertyChanged(nameof(IsCapturing));
        }

        public bool IsCapturing => _capture != null;

        private void CancelCapture()
        {
            if (_capture == null) return;
            _capture.Cancel();
            FinishCapture();
        }

        private void FinishCapture()
        {
            if (_captureTarget != null)
            {
                _captureTarget.IsCapturing = false;
                _captureTarget.CaptureHint = null;
            }
            _capture = null;
            _captureTarget = null;
            OnPropertyChanged(nameof(IsCapturing));
        }

        /// <summary>
        /// Drives the capture state machine from the UI tick, so phase changes
        /// and the deadline still happen when the device sits perfectly still
        /// and sends nothing at all.
        /// </summary>
        private void PumpCapture()
        {
            var capture = _capture;
            var target = _captureTarget;
            if (capture == null || target == null) return;

            capture.Tick(DateTime.UtcNow);

            target.CaptureHint = capture.Phase == CapturePhase.Baseline
                ? "Hold still…"
                : capture.Phase == CapturePhase.Tracking
                    ? "Now release it"
                    : "Press or move the control";

            if (!capture.IsFinished) return;

            if (capture.Phase == CapturePhase.Committed)
            {
                var result = capture.Result;
                target.Replace(result.Binding);
                target.Channel.RefreshVisibleSlots();
                _ = SendBindingAsync(result.Channel, result.Slot, JObject.FromObject(result.Binding));
                AppendLog(string.Format("bound {0} to {1}", HidNames.Channel(result.Channel), target.Description));
            }
            else if (capture.Phase == CapturePhase.TimedOut)
            {
                AppendLog("capture timed out — nothing was pressed.");
            }

            FinishCapture();
        }

        private void ClearBinding(object parameter)
        {
            var target = parameter as BindingSlotViewModel;
            if (target == null || !IsConnected) return;

            target.Replace(InputBinding.None());
            target.Channel.RefreshVisibleSlots();
            _ = SendBindingAsync(target.Channel.Key, target.Slot, JObject.FromObject(InputBinding.None()));
        }

        /// <summary>Feeds live frames to the capture engine and the slot previews.</summary>
        private void ApplyLiveToMappings(LiveSlot live)
        {
            _capture?.Observe(live, DateTime.UtcNow);

            // Resolve the device once. This used to be a LINQ scan inside the
            // inner loop, which ran 60 times per frame per slot for no reason.
            DeviceSlot device = null;
            var devices = _session.Devices;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].Slot == live.Slot) { device = devices[i]; break; }
            }
            if (device == null) return;

            foreach (var channel in Channels)
            {
                foreach (var slot in channel.Slots)
                {
                    if (!slot.IsBound) continue;

                    // Same-VID/PID devices are aggregated by the firmware, so a
                    // binding follows the identity rather than the pool slot.
                    if (device.Vid != slot.Binding.Vid || device.Pid != slot.Binding.Pid) continue;
                    slot.ApplyLive(live);
                }
            }
        }

        // ---------- SimHub drive (PC-attached devices) ----------

        private readonly DispatcherTimer _liveTimer;
        private readonly DispatcherTimer _slowTimer;

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
        public ParameterCommand DetectAxisCommand { get; }

        // ---------- Pedal auto-detect ----------
        // Listen-style capture for the PC side: sample every SimHub property,
        // find the one the user presses, commit name + range + direction. Runs
        // entirely against SimHub — no adapter connection required.

        private AxisSourceViewModel _detectAxis;
        private PropertyAxisDetector _detector;
        private DateTime _detectStart;
        private List<string> _detectNames;
        private DetectPhase _detectShownPhase;

        private void ToggleAxisDetect(object parameter)
        {
            var axis = parameter as AxisSourceViewModel;
            if (axis == null) return;

            if (_detectAxis == axis)
            {
                CancelAxisDetect("detect cancelled.");
                return;
            }
            if (_detectAxis != null) CancelAxisDetect(null); // switch channels silently

            var pm = _plugin.PluginManager;
            if (pm == null) return;

            List<string> names;
            try { names = pm.GetAllPropertiesNames().ToList(); }
            catch (Exception ex)
            {
                AppendLog("could not read SimHub's property list: " + ex.Message);
                return;
            }

            _detectAxis = axis;
            _detector = new PropertyAxisDetector();
            _detectStart = DateTime.UtcNow;
            _detectNames = names;
            _detectShownPhase = DetectPhase.Baseline;
            axis.IsDetecting = true;
            axis.DetectStatus = "hold everything still…";
        }

        private void CancelAxisDetect(string message)
        {
            var axis = _detectAxis;
            _detectAxis = null;
            _detector = null;
            _detectNames = null;
            if (axis != null) axis.IsDetecting = false;
            if (message != null) AppendLog(message);
        }

        private void PumpAxisDetect()
        {
            var detector = _detector;
            var axis = _detectAxis;
            if (detector == null || axis == null) return;

            var pm = _plugin.PluginManager;
            if (pm == null) { CancelAxisDetect(null); return; }

            var sample = new List<KeyValuePair<string, double>>(_detectNames.Count);
            foreach (var name in _detectNames)
            {
                object raw;
                try { raw = pm.GetPropertyValue(name); }
                catch { continue; }

                double value;
                if (raw is bool flag) value = flag ? 1 : 0;
                else
                {
                    try { value = Convert.ToDouble(raw); }
                    catch { continue; }
                }
                if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                sample.Add(new KeyValuePair<string, double>(name, value));
            }

            detector.Feed((DateTime.UtcNow - _detectStart).TotalSeconds, sample);

            if (detector.Phase != _detectShownPhase)
            {
                _detectShownPhase = detector.Phase;
                switch (detector.Phase)
                {
                    case DetectPhase.Listening:
                        // Sampling the whole property list is the expensive part;
                        // after the baseline most names are pruned (non-numeric or
                        // already moving) and during tracking only one is left.
                        _detectNames = detector.SurvivingCandidates().ToList();
                        axis.DetectStatus = "press the " + axis.Label.ToLowerInvariant() + " fully, then release…";
                        break;
                    case DetectPhase.Tracking:
                        _detectNames = detector.SurvivingCandidates().ToList();
                        axis.DetectStatus = "got " + detector.LatchedProperty + " — release…";
                        break;
                }
            }

            if (detector.Phase == DetectPhase.Done)
            {
                var d = detector.Result;
                axis.ApplyDetection(d);
                AppendLog(string.Format("{0} detected: {1}  range {2:0.###}..{3:0.###}{4}",
                    axis.Label, d.PropertyName, d.InputMin, d.InputMax, d.Invert ? "  (inverted)" : ""));
                CancelAxisDetect(null);
            }
            else if (detector.Phase == DetectPhase.Failed)
            {
                AppendLog(axis.Label + " detect failed: " + detector.FailureReason);
                CancelAxisDetect(null);
            }
        }

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
                var armed = _plugin.Settings.Drive.AxisStreamingEnabled;

                if (!IsConnected)
                {
                    return armed
                        ? "Connect to the adapter — driving pedals resumes automatically."
                        : "Connect to the adapter first.";
                }
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
                // Identical to pressing Release outputs: stop, hand the channels
                // back (the last streamed values stay latched otherwise), and
                // revoke the persisted choice so it doesn't resume by itself.
                _plugin.ReleaseOutputsByUser();
            }
            else
            {
                // Saved before starting, so a SimHub that dies mid-session still
                // comes back driving.
                _plugin.Settings.Drive.AxisStreamingEnabled = true;
                _plugin.SaveSettings();
                drive.StartStreaming();
            }

            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(StreamingButtonText));
            OnPropertyChanged(nameof(StreamingStatus));
        }

        private void ReleaseOutputs()
        {
            // Revokes the persisted choice too — pressing this is "stop driving",
            // and it would be a nasty surprise for the stream to reappear on the
            // next game change.
            _plugin.ReleaseOutputsByUser();
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(StreamingButtonText));
            OnPropertyChanged(nameof(StreamingStatus));
            AppendLog("outputs released — the adapter's own mapping is back in control.");
        }

        // ---------- Drive feedback (did that shift actually happen?) ----------

        /// <summary>
        /// How long a shift lamp stays lit. Long enough to catch the eye on a
        /// paddle pull, short enough that two quick shifts read as two.
        /// </summary>
        private const int ShiftLampMs = 220;

        private long _lastDriveSequence;
        private DateTime _shiftUpLampUntil = DateTime.MinValue;
        private DateTime _shiftDownLampUntil = DateTime.MinValue;

        /// <summary>Gear the adapter says it is holding, or "—" when nothing is connected.</summary>
        public string DriveGear => IsConnected ? OutputGear : "—";

        private bool _shiftUpLamp;
        public bool ShiftUpLamp
        {
            get => _shiftUpLamp;
            private set { if (_shiftUpLamp == value) return; _shiftUpLamp = value; OnPropertyChanged(); }
        }

        private bool _shiftDownLamp;
        public bool ShiftDownLamp
        {
            get => _shiftDownLamp;
            private set { if (_shiftDownLamp == value) return; _shiftDownLamp = value; OnPropertyChanged(); }
        }

        private string _lastDriveAction = "Nothing sent yet.";
        public string LastDriveAction
        {
            get => _lastDriveAction;
            private set { if (_lastDriveAction == value) return; _lastDriveAction = value; OnPropertyChanged(); }
        }

        private bool _lastDriveActionOk = true;
        public bool LastDriveActionFailed
        {
            get => !_lastDriveActionOk;
            private set { if (_lastDriveActionOk == !value) return; _lastDriveActionOk = !value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Turns "I pressed the paddle" into visible evidence, from the two
        /// independent confirmations available:
        ///
        /// * the adapter <em>acknowledging</em> the command we sent — proof it
        ///   arrived and was understood, but only that;
        /// * the outputs stream showing the pin actually driven — proof the
        ///   firmware acted, but sampled at ~30 Hz, so a 50 ms pulse can fall
        ///   between two frames.
        ///
        /// Either lights the lamp, because neither alone is reliable enough to
        /// be the only evidence. Nothing here can confirm the *wheelbase*
        /// registered the shift — that link reports nothing back.
        /// </summary>
        private void PumpDriveFeedback()
        {
            var now = DateTime.UtcNow;

            var activity = _plugin.Drive?.LastActivity;
            if (activity != null && activity.Sequence != _lastDriveSequence)
            {
                _lastDriveSequence = activity.Sequence;
                LastDriveAction = Describe(activity);
                LastDriveActionFailed = !activity.Acknowledged;

                if (activity.Acknowledged)
                {
                    if (activity.Channel == "shift_up") _shiftUpLampUntil = now.AddMilliseconds(ShiftLampMs);
                    else if (activity.Channel == "shift_down") _shiftDownLampUntil = now.AddMilliseconds(ShiftLampMs);
                }
            }

            var outputs = _lastOutputs;
            if (IsConnected && outputs.ShiftUp) _shiftUpLampUntil = now.AddMilliseconds(ShiftLampMs);
            if (IsConnected && outputs.ShiftDown) _shiftDownLampUntil = now.AddMilliseconds(ShiftLampMs);

            ShiftUpLamp = now < _shiftUpLampUntil;
            ShiftDownLamp = now < _shiftDownLampUntil;
        }

        private static string Describe(DriveActivity activity)
        {
            var when = activity.At.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            return activity.Acknowledged
                ? activity.Action + " · acknowledged " + when
                : activity.Action + " · FAILED " + when + " — " + activity.Error;
        }

        private int _slowTickCount;
        private int _liveTickCount;

        private void OnLiveTick()
        {
            ApplyPendingLive();
            PumpCapture();
            PumpAxisDetect();
            RaiseOutputProperties();
            PumpDriveFeedback();

            // Every other tick — ~30 Hz, which is where a moving pedal bar stops
            // looking like it is stepping. This used to ride the 200 ms slow
            // timer, and worse, that timer runs at Background priority: inside an
            // app as busy as SimHub it gets starved well below its own 5 Hz, which
            // is what made the readout look like 2-3 fps.
            if ((_liveTickCount++ & 1) == 0) UpdateAxisReadouts();
        }

        private void OnSlowTick()
        {
            // Cheap, but it builds a string and nothing on it changes fast.
            OnPropertyChanged(nameof(StreamingStatus));

            // The pedal link state changes rarely and costs a serial round-trip,
            // so it is polled every couple of seconds rather than every tick.
            if (++_slowTickCount % 10 == 0) _ = RefreshPedalStateAsync();
        }

        /// <summary>
        /// Pulls each configured pedal property and pushes it at the editors.
        /// Four dictionary lookups per pass — the same properties the drive
        /// stream itself reads at 100 Hz — so the cost is in the notifications,
        /// which is why the view model suppresses unchanged ones.
        /// </summary>
        private void UpdateAxisReadouts()
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
        }

        // ---------- Outputs (what the wheelbase is being told) ----------

        public ObservableCollection<GearDacViewModel> GearDacs { get; } =
            new ObservableCollection<GearDacViewModel>();

        private OutputsState _lastOutputs = new OutputsState { Gear = "gear_N" };

        public string OutputGear => HidNames.Channel(_lastOutputs.Gear ?? "gear_N");
        public bool OutputShiftUp => _lastOutputs.ShiftUp;
        public bool OutputShiftDown => _lastOutputs.ShiftDown;
        public double OutputThrottle => Percent(_lastOutputs.Throttle);
        public double OutputBrake => Percent(_lastOutputs.Brake);
        public double OutputClutch => Percent(_lastOutputs.Clutch);
        public double OutputHandbrake => Percent(_lastOutputs.Handbrake);

        private static double Percent(int raw) => Math.Round(raw * 100.0 / 65535.0, 1);

        private int _pulseMs = 50;
        public int PulseMs
        {
            get => _pulseMs;
            set { _pulseMs = value; OnPropertyChanged(); _ = SetPulseMsAsync(value); }
        }

        public RelayCommand TestGearCommand { get; private set; }
        public RelayCommand TestPulseCommand { get; private set; }
        public RelayCommand TestAxisCommand { get; private set; }
        public RelayCommand RearmPedalsCommand { get; private set; }
        public RelayCommand UsbStatusCommand { get; private set; }
        public RelayCommand UsbKickCommand { get; private set; }

        private string _pedalLinkState = "unknown";
        public string PedalLinkState
        {
            get => _pedalLinkState;
            private set { _pedalLinkState = value; OnPropertyChanged(); }
        }

        private volatile bool _outputsDirty;

        private void OnOutputs(OutputsState state)
        {
            _lastOutputs = state;
            _outputsDirty = true;
        }

        private void RaiseOutputProperties()
        {
            // The adapter streams outputs continuously, not only on change, so
            // without this the fast tick would fire seven notifications 60 times
            // a second forever.
            if (!_outputsDirty) return;
            _outputsDirty = false;

            OnPropertyChanged(nameof(OutputGear));
            OnPropertyChanged(nameof(DriveGear));
            OnPropertyChanged(nameof(OutputShiftUp));
            OnPropertyChanged(nameof(OutputShiftDown));
            OnPropertyChanged(nameof(OutputThrottle));
            OnPropertyChanged(nameof(OutputBrake));
            OnPropertyChanged(nameof(OutputClutch));
            OnPropertyChanged(nameof(OutputHandbrake));
        }

        private void LoadOutputsFromConfig()
        {
            var config = _session.Config;
            if (config == null) return;

            _pulseMs = config.PulseMs;
            OnPropertyChanged(nameof(PulseMs));

            GearDacs.Clear();
            foreach (var gear in Schema.GearKeys)
            {
                var dac = config.GetGearDac(gear);
                GearDacs.Add(new GearDacViewModel(gear, dac, PushGearDac));
            }
        }

        private void PushGearDac(GearDacViewModel vm)
        {
            if (!IsConnected) return;
            _ = RunGuardedAsync(
                () => _session.Protocol.SetGearDacAsync(vm.Gear, vm.X, vm.Y),
                "could not set the " + vm.Label + " voltages");
        }

        private async Task SetPulseMsAsync(int value)
        {
            if (!IsConnected) return;
            await RunGuardedAsync(() => _session.Protocol.SetPulseMsAsync(value),
                "could not set the pulse width");
        }

        /// <summary>
        /// Runs an adapter command, turning a failure into a log line rather
        /// than an unobserved task exception. Bench-test buttons are fire and
        /// forget by nature — there is nothing to await them.
        /// </summary>
        private async Task RunGuardedAsync(Func<Task> work, string failureMessage)
        {
            try
            {
                await work();
                IsDirty = true;
            }
            catch (Exception ex)
            {
                AppendLog(failureMessage + ": " + ex.Message);
            }
        }

        private void TestGear(object parameter)
        {
            var gear = parameter as string;
            if (gear == null || !IsConnected) return;
            _ = RunGuardedAsync(() => _session.Protocol.TestGearAsync(gear), "gear test failed");
        }

        private void TestPulse(object parameter)
        {
            if (!IsConnected) return;
            var direction = (parameter as string) == "down" ? ShiftDirection.Down : ShiftDirection.Up;
            _ = RunGuardedAsync(() => _session.Protocol.TestPulseAsync(direction), "pulse test failed");
        }

        /// <summary>Parameter is "channel:percent", e.g. "throttle:50".</summary>
        private void TestAxis(object parameter)
        {
            var spec = parameter as string;
            if (spec == null || !IsConnected) return;

            var parts = spec.Split(':');
            if (parts.Length != 2) return;

            int percent;
            if (!int.TryParse(parts[1], out percent)) return;

            int value = (int)Math.Round(percent * 65535.0 / 100.0);
            _ = RunGuardedAsync(() => _session.Protocol.TestAxisAsync(parts[0], value), "axis test failed");
        }

        private void RearmPedals()
        {
            if (!IsConnected) return;
            _ = RunGuardedAsync(() => _session.Protocol.ResetPedalsAsync(), "could not re-arm the pedal handshake");
            AppendLog("pedal handshake re-armed.");
        }

        // ---------- USB-host diagnostics ----------
        // For the open field bug where a hub device goes silent while still
        // enumerated, freezing its last values. Run these WHILE it is frozen:
        // status shows whether the stuck slot's pipe is still armed and how
        // long since it last delivered a report; kick aborts and re-arms every
        // pipe — if inputs resume, the wedge was on the adapter's USB host
        // controller; if not, the device itself stopped talking. Results land
        // in the Logs tab. Not RunGuardedAsync: diagnostics don't dirty the config.

        private void UsbStatus() => _ = UsbStatusAsync();

        private async Task UsbStatusAsync()
        {
            if (!IsConnected) return;
            try
            {
                var status = await _session.Protocol.GetUsbStatusAsync();
                AppendLog("usb_status: " + status.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                AppendLog("usb status failed: " + ex.Message);
            }
        }

        private void UsbKick() => _ = UsbKickAsync();

        private async Task UsbKickAsync()
        {
            if (!IsConnected) return;
            try
            {
                int aborted = await _session.Protocol.UsbKickAsync();
                AppendLog(aborted == 0
                    ? "usb pipes kicked — none were busy (all idle pipes re-armed)."
                    : string.Format("usb pipes kicked — aborted outstanding transfers on slot mask 0x{0:X2}. " +
                                    "If the frozen device now responds, the wedge was host-side.", aborted));
            }
            catch (Exception ex)
            {
                AppendLog("usb kick failed: " + ex.Message);
            }
        }

        private async Task RefreshPedalStateAsync()
        {
            if (!IsConnected) return;
            try
            {
                var status = await _session.Protocol.GetPedalsStatusAsync();
                PedalLinkState = status.State ?? "unknown";
            }
            catch
            {
                // Polled on a timer; a transient failure isn't worth logging
                // every tick.
                PedalLinkState = "unknown";
            }
        }

        // ---------- Logs ----------

        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        private void OnLogLine(string line) => AppendLog(line);

        private void AppendLog(string line)
        {
            // Stamped at receipt, not at render: RunOnUi may queue the append
            // behind other dispatcher work, and the whole point of the stamp is
            // ordering diagnostics ("did the reboot come before the stale
            // replies?") — so take the clock before crossing threads.
            var stamped = DateTime.Now.ToString("HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture) + "  " + line;
            RunOnUi(() =>
            {
                LogLines.Add(stamped);
                while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
            });
        }

        // ---------- Plumbing ----------

        private void OnSessionStateChanged() => SyncFromSession();

        /// <summary>
        /// Marshalled as a whole, not just its notifications: it rebuilds the
        /// mapping and output editors, and WPF rejects changes to a bound
        /// ObservableCollection from any thread but the dispatcher's. Callers
        /// reach this from async continuations that resume on the pool.
        /// </summary>
        private void SyncFromSession() => RunOnUi(() =>
        {
            // The config arrives with the connection, so the editors are rebuilt
            // whenever session state changes rather than on a separate signal
            // that could arrive first.
            LoadChannelsFromConfig();
            LoadOutputsFromConfig();

            // Devices likewise, and for a sharper reason: the plugin auto-connects in Init(),
            // long before SimHub builds the settings UI, so the DevicesChanged raised during
            // that connect fires with nothing subscribed. Rebuilding only from the event left
            // the collection permanently empty — no device cards, and every live frame
            // discarded because ApplyPendingLive found no view model to apply it to (outputs
            // kept working, since they bind to scalars rather than this collection). Nothing
            // recovered it either: device_attached only fires for devices that arrive AFTER we
            // connect, and the rig's are already plugged in. So derive the list from session
            // state here rather than trusting we existed when the event went out.
            RebuildDeviceList();

            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(FirmwareBadge));
            OnPropertyChanged(nameof(SupportsDirectOutput));
            OnPropertyChanged(nameof(DirectOutputNote));
            OnPropertyChanged(nameof(CanStream));
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(StreamingButtonText));
            OnPropertyChanged(nameof(StreamingStatus));
            OnPropertyChanged(nameof(DriveGear));
            RaiseCommandStates();
            Firmware?.OnSessionChanged();

            // Covers the connection arriving while the pane is already open —
            // the adapter forgets telemetry across a reconnect.
            SyncInputTelemetry();
        });

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
            // Every connection-gated command must be here: RelayCommand does not
            // hook CommandManager.RequerySuggested, so a button bound before the
            // connect keeps its stale disabled state until this fires (the
            // "Re-arm handshake greyed out while connected" bug).
            StartCaptureCommand.RaiseCanExecuteChanged();
            ClearBindingCommand.RaiseCanExecuteChanged();
            TestGearCommand.RaiseCanExecuteChanged();
            TestPulseCommand.RaiseCanExecuteChanged();
            TestAxisCommand.RaiseCanExecuteChanged();
            RearmPedalsCommand.RaiseCanExecuteChanged();
            UsbStatusCommand.RaiseCanExecuteChanged();
            UsbKickCommand.RaiseCanExecuteChanged();
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

        private bool _attached;

        /// <summary>
        /// Starts the timers and subscribes to the session. Idempotent, and it
        /// must be, because it is called from both the constructor and the
        /// control's Loaded event.
        ///
        /// The pairing with <see cref="Detach"/> is the point. WPF raises
        /// Unloaded whenever it pulls the control out of the visual tree —
        /// navigating to another SimHub page does exactly that — and the control
        /// that comes back is the *same instance*, already detached. Detaching
        /// without a matching re-attach produced a pane that looked alive and
        /// took button presses (commands bind straight to this object) while
        /// every live readout sat frozen, because the timers were stopped and
        /// the session events had nobody listening.
        /// </summary>
        public void Attach()
        {
            if (_attached) return;
            _attached = true;

            _session.StateChanged += OnSessionStateChanged;
            _session.LogLine += OnLogLine;
            _session.DevicesChanged += OnDevicesChanged;
            _session.LiveInput += OnLiveInput;
            _session.Outputs += OnOutputs;

            _liveTimer.Start();
            _slowTimer.Start();

            // Whatever happened while detached — a connect, a reboot, devices
            // arriving — never reached this view model, so rebuild from session
            // state rather than waiting for the next event to notice.
            SyncFromSession();
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;

            _liveTimer.Stop();
            _slowTimer.Stop();
            _session.StateChanged -= OnSessionStateChanged;
            _session.LogLine -= OnLogLine;
            _session.DevicesChanged -= OnDevicesChanged;
            _session.LiveInput -= OnLiveInput;
            _session.Outputs -= OnOutputs;

            SyncInputTelemetry();
        }

        private bool _inputTelemetryOn;
        private Protocol _inputTelemetryProtocol;

        /// <summary>
        /// Input telemetry follows this pane, because this pane is its only
        /// consumer: the Devices tab and Listen capture. Two reasons it isn't
        /// simply switched on with the connection — the plugin's own
        /// auto-connect doesn't enable it (so after a SimHub restart the Devices
        /// tab would sit empty forever), and a moving pedal streams events at up
        /// to 50 Hz, which is traffic worth not putting on the link while nobody
        /// is looking. Output telemetry is the opposite case and stays on for
        /// the whole connection: the exposed dash properties feed on it.
        /// </summary>
        private void SyncInputTelemetry()
        {
            var proto = IsConnected ? _session.Protocol : null;
            if (proto == null)
            {
                _inputTelemetryOn = false;
                _inputTelemetryProtocol = null;
                return;
            }

            bool wanted = _attached;
            if (proto == _inputTelemetryProtocol && wanted == _inputTelemetryOn) return;
            _inputTelemetryProtocol = proto;
            _inputTelemetryOn = wanted;

            // Send queues the small command synchronously, then yields for its
            // reply. Task.Run here would let a quick close/reopen send "off"
            // after "on", freezing the reopened pane until the next reconnect.
            _ = SetInputTelemetryAsync(proto, wanted);
        }

        private async Task SetInputTelemetryAsync(Protocol proto, bool wanted)
        {
            try { await proto.SetLiveInputsAsync(wanted).ConfigureAwait(false); }
            catch (Exception ex)
            {
                RunOnUi(() =>
                {
                    // Only invalidate this request's state; a failed command on
                    // an old connection must not undo a newer connection's state.
                    if (_inputTelemetryProtocol == proto && _inputTelemetryOn == wanted)
                        _inputTelemetryProtocol = null;
                    AppendLog("live input telemetry could not be " +
                              (wanted ? "enabled" : "disabled") + ": " + ex.Message);
                });
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            RunOnUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}
