using System;
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
                    // remember it across a reconnect.
                    await _session.Protocol.SetLiveOutputsAsync(true);
                }
                catch (Exception ex)
                {
                    AppendLog("connected, but output telemetry could not be enabled: " + ex.Message);
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
            _session.StateChanged -= OnSessionStateChanged;
            _session.LogLine -= OnLogLine;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            RunOnUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}
