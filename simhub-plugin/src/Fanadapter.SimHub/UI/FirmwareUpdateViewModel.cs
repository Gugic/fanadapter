using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Fanadapter.Core;

namespace Fanadapter.SimHub.UI
{
    /// <summary>
    /// One-click firmware update, the SimHub twin of webconfig's Firmware dialog — and one step
    /// more hands-free: native code has no WebUSB permission model, so there is no device picker
    /// even on the first run. The cycle: fetch the published image → {"cmd":"dfu"} → release the
    /// COM port → open the ROM bootloader over WinUSB → erase/write/leave → wait for the COM port
    /// to re-enumerate → reconnect → confirm the version.
    /// </summary>
    public class FirmwareUpdateViewModel : INotifyPropertyChanged
    {
        private readonly AdapterSession _session;
        private readonly FirmwareChannel _channel;
        private readonly Action<string> _log;
        private readonly Action<Action> _runOnUi;
        private readonly Func<Task> _reconnect;

        public FirmwareUpdateViewModel(
            AdapterSession session,
            string baseUrlOverride,
            Action<string> log,
            Action<Action> runOnUi,
            Func<Task> reconnect)
        {
            _session = session;
            _channel = new FirmwareChannel(baseUrlOverride);
            _log = log;
            _runOnUi = runOnUi;
            _reconnect = reconnect;
            UpdateCommand = new RelayCommand(() => _ = RunUpdateAsync(), () => CanUpdate);
        }

        public RelayCommand UpdateCommand { get; }

        private FirmwareManifest _manifest;
        private bool _checked;

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                _isRunning = value;
                OnPropertyChanged();
                _runOnUi(UpdateCommand.RaiseCanExecuteChanged);
            }
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            private set { _progressPercent = value; OnPropertyChanged(); }
        }

        public string LatestText =>
            _manifest == null
                ? (_checked ? "latest: unavailable (offline?)" : "latest: checking…")
                : string.Format("latest: {0} · {1}", _manifest.Version, _manifest.Commit);

        /// <summary>
        /// Connected to an STM32 adapter with a published build available, or — deliberately —
        /// disconnected with one available: a board stranded in the bootloader by an interrupted
        /// update has no serial port, and re-running the update is exactly how it recovers.
        /// </summary>
        public bool CanUpdate =>
            !IsRunning && _manifest != null &&
            (!_session.IsConnected || (_session.Version?.SupportsDfu ?? false));

        /// <summary>Called by the owner on connect (and once at startup) — never throws.</summary>
        public async Task RefreshManifestAsync()
        {
            try
            {
                _manifest = await _channel.TryGetManifestAsync(CancellationToken.None);
            }
            catch
            {
                _manifest = null;
            }
            _checked = true;
            _runOnUi(() =>
            {
                OnPropertyChanged(nameof(LatestText));
                UpdateCommand.RaiseCanExecuteChanged();
            });
        }

        private async Task RunUpdateAsync()
        {
            IsRunning = true;
            ProgressPercent = 0;
            try
            {
                Status("Downloading firmware…");
                var image = await _channel.GetImageAsync(CancellationToken.None);

                // Remember where to come back to before tearing the session down.
                var portName = _session.PortName;

                if (_session.IsConnected)
                {
                    Status("Rebooting into the bootloader…");
                    try
                    {
                        await _session.Protocol.DfuAsync();
                    }
                    catch (Exception ex)
                    {
                        // The reply can race the reboot; the reset still happened. Log and go on.
                        _log("dfu command reply lost (" + ex.Message + ") — continuing");
                    }
                    _session.Disconnect(); // release the COM port; it is about to vanish anyway
                }

                Status("Waiting for the bootloader…");
                WinUsbDfuDevice dfu = await Task.Run(
                    () => WinUsbDfuDevice.WaitAndOpen(10000, CancellationToken.None));
                if (dfu == null)
                {
                    Fail("The bootloader never appeared on USB. If the adapter is connected and " +
                         "powered, Windows may be missing the WinUSB driver for \"STM32 " +
                         "BOOTLOADER\" — installing STM32CubeProgrammer (or one Zadig run) fixes " +
                         "that.");
                    return;
                }

                Status("Flashing…");
                using (dfu)
                {
                    await Task.Run(() => new DfuseFlasher(dfu).Flash(image, OnFlashProgress));
                }

                Status("Waiting for the adapter…");
                ProgressPercent = 0;
                var deadline = Environment.TickCount + 15000;
                for (;;)
                {
                    if (portName != null &&
                        SerialPort.GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    if (Environment.TickCount >= deadline)
                    {
                        Fail("Flashed, but the adapter did not re-enumerate its serial port " +
                             "within 15 s — reconnect manually.");
                        return;
                    }
                    await Task.Delay(1000);
                }

                Status("Reconnecting…");
                await _reconnect();
                var v = _session.Version;
                if (_session.IsConnected && v != null)
                {
                    Status("Updated to " + v.Version + ". All done.");
                    _log("firmware updated to " + v.Version);
                }
                else
                {
                    Fail("Flashed, but reconnecting failed — hit Connect manually.");
                }
            }
            catch (Exception ex)
            {
                Fail("Update failed: " + ex.Message + " — the bootloader keeps running after a " +
                     "failed write; fix the issue and update again.");
            }
            finally
            {
                IsRunning = false;
            }
        }

        private void OnFlashProgress(FlashProgress p)
        {
            _runOnUi(() =>
            {
                // One bar for both phases: erase is the first fifth, writing the rest — sector
                // erases dominate wall-clock far less than their count suggests.
                double frac = p.Total > 0 ? (double)p.Done / p.Total : 0;
                ProgressPercent = p.Phase == FlashPhase.Erase ? frac * 20
                    : p.Phase == FlashPhase.Write ? 20 + frac * 75
                    : 95 + frac * 5;
                StatusText = p.Phase == FlashPhase.Erase ? "Erasing…"
                    : p.Phase == FlashPhase.Write
                        ? string.Format("Writing {0} / {1} KB", p.Done / 1024, p.Total / 1024)
                        : "Booting the new firmware…";
            });
        }

        private void Status(string text)
        {
            _runOnUi(() => StatusText = text);
        }

        private void Fail(string text)
        {
            _log(text);
            Status(text);
        }

        /// <summary>Owner calls this when the session state changes (installed fw / gates).</summary>
        public void OnSessionChanged()
        {
            _runOnUi(() =>
            {
                OnPropertyChanged(nameof(CanUpdate));
                UpdateCommand.RaiseCanExecuteChanged();
            });
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            _runOnUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}
