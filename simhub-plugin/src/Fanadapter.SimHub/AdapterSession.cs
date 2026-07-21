using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fanadapter.Core;

namespace Fanadapter.SimHub
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Failed,
    }

    /// <summary>
    /// Owns the link to the adapter: opening a port, the initial handshake, and
    /// tearing everything down again. Everything above it (the settings UI, the
    /// SimHub actions) talks to the adapter through here rather than holding a
    /// <see cref="SerialClient"/> of its own, so there is exactly one owner of
    /// the port and one place that guarantees cleanup.
    ///
    /// Events are raised on whatever thread produced them — the serial reader
    /// thread for telemetry. UI consumers must marshal.
    /// </summary>
    public class AdapterSession : IDisposable
    {
        private readonly object _sync = new object();
        private SerialClient _client;
        private Protocol _protocol;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public string PortName { get; private set; }
        public string StatusMessage { get; private set; } = "Not connected";
        public VersionInfo Version { get; private set; }
        public Config Config { get; private set; }
        public IReadOnlyList<DeviceSlot> Devices { get; private set; } = new List<DeviceSlot>();

        /// <summary>Null unless connected. Check <see cref="IsConnected"/> before use.</summary>
        public Protocol Protocol => _protocol;

        public bool IsConnected => State == ConnectionState.Connected;

        /// <summary>
        /// Whether the connected firmware implements the direct-output commands.
        /// False on a Teensy, which answers unknown_cmd to all four.
        /// </summary>
        public bool SupportsDirectOutput => Version != null && Version.SupportsDirectOutput;

        public event Action StateChanged;
        public event Action<string> LogLine;
        public event Action DevicesChanged;
        public event Action<LiveSlot> LiveInput;
        public event Action<OutputsState> Outputs;

        public async Task ConnectAsync(string portName, CancellationToken cancellationToken = default(CancellationToken))
        {
            Disconnect();

            PortName = portName;
            SetState(ConnectionState.Connecting, "Opening " + portName + "…");

            SerialClient client = null;
            try
            {
                client = SerialClient.ForPort(portName);
                client.LogLineReceived += OnLogLine;
                client.LiveInput += OnLiveInput;
                client.OutputsChanged += OnOutputs;
                client.DeviceAttached += OnDeviceAttached;
                client.DeviceDetached += OnDeviceDetached;
                client.Faulted += OnFaulted;

                await client.OpenAsync(cancellationToken).ConfigureAwait(false);

                lock (_sync)
                {
                    _client = client;
                    _protocol = new Protocol(client);
                }

                Version = await _protocol.GetVersionAsync().ConfigureAwait(false);
                Devices = await _protocol.ListDevicesAsync().ConfigureAwait(false);
                Config = await _protocol.GetConfigAsync().ConfigureAwait(false);

                SetState(ConnectionState.Connected,
                    string.Format("{0} {1} on {2}", Version.Firmware, Version.Version, portName));
                DevicesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // Leave nothing half-open: a port held by a failed attempt would
                // block the next one with an access-denied that looks unrelated.
                if (client != null)
                {
                    try { client.Dispose(); } catch { /* already broken */ }
                }
                lock (_sync)
                {
                    _client = null;
                    _protocol = null;
                }
                SetState(ConnectionState.Failed, Describe(ex, portName));
                throw;
            }
        }

        private static string Describe(Exception ex, string portName)
        {
            if (ex is UnauthorizedAccessException)
            {
                // Overwhelmingly the common case, and the raw message doesn't say
                // who is holding it.
                return portName + " is in use — close webconfig or another serial tool first.";
            }
            if (ex is TimeoutException)
            {
                return portName + " opened but did not answer. Is this the adapter?";
            }
            return ex.Message;
        }

        public void Disconnect()
        {
            SerialClient client;
            lock (_sync)
            {
                client = _client;
                _client = null;
                _protocol = null;
            }

            if (client == null) return;

            client.LogLineReceived -= OnLogLine;
            client.LiveInput -= OnLiveInput;
            client.OutputsChanged -= OnOutputs;
            client.DeviceAttached -= OnDeviceAttached;
            client.DeviceDetached -= OnDeviceDetached;
            client.Faulted -= OnFaulted;

            try { client.Dispose(); } catch { /* nothing useful to do */ }

            Version = null;
            Config = null;
            Devices = new List<DeviceSlot>();
            SetState(ConnectionState.Disconnected, "Not connected");
            DevicesChanged?.Invoke();
        }

        /// <summary>Re-reads the device list from the adapter.</summary>
        public async Task RefreshDevicesAsync()
        {
            var proto = _protocol;
            if (proto == null) return;
            Devices = await proto.ListDevicesAsync().ConfigureAwait(false);
            DevicesChanged?.Invoke();
        }

        /// <summary>Re-reads the whole config, discarding unsaved local edits.</summary>
        public async Task ReloadConfigAsync()
        {
            var proto = _protocol;
            if (proto == null) return;
            Config = await proto.GetConfigAsync().ConfigureAwait(false);
            StateChanged?.Invoke();
        }

        private void OnFaulted(Exception ex)
        {
            LogLine?.Invoke("link lost: " + ex.Message);
            Disconnect();
            SetState(ConnectionState.Failed, "Link lost: " + ex.Message);
        }

        private void OnLogLine(string line) => LogLine?.Invoke(line);
        private void OnLiveInput(LiveSlot slot) => LiveInput?.Invoke(slot);
        private void OnOutputs(OutputsState outputs) => Outputs?.Invoke(outputs);

        private void OnDeviceAttached(DeviceSlot device)
        {
            var updated = new List<DeviceSlot>(Devices);
            int existing = updated.FindIndex(d => d.Slot == device.Slot);
            if (existing >= 0) updated[existing] = device; else updated.Add(device);
            Devices = updated;
            DevicesChanged?.Invoke();
        }

        private void OnDeviceDetached(int slot)
        {
            var updated = new List<DeviceSlot>(Devices);
            int existing = updated.FindIndex(d => d.Slot == slot);
            if (existing >= 0)
            {
                updated[existing].Connected = false;
                Devices = updated;
                DevicesChanged?.Invoke();
            }
        }

        private void SetState(ConnectionState state, string message)
        {
            State = state;
            StatusMessage = message;
            StateChanged?.Invoke();
        }

        public void Dispose() => Disconnect();
    }
}
