using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fanadapter.Core
{
    /// <summary>
    /// Line-based JSON client for the fanadapter firmware — the C# counterpart
    /// of webconfig/src/lib/serial.ts. Owns framing, the request/response queue
    /// and async event fan-out; <see cref="Protocol"/> layers typed commands on top.
    /// </summary>
    public class SerialClient : IDisposable
    {
        /// <summary>
        /// A USB-UART bridge resets itself and/or the MCU when its port opens.
        /// Commands sent during that window are simply lost, so wait it out
        /// before the first request. Harmless on a native USB CDC.
        /// </summary>
        public static readonly TimeSpan OpenSettleDelay = TimeSpan.FromSeconds(2);

        public const int DefaultTimeoutMs = 3000;

        private sealed class PendingRequest
        {
            public string Command;
            public TaskCompletionSource<JObject> Completion;
            public CancellationTokenRegistration Registration;
            public CancellationTokenSource Timeout;

            /// <summary>
            /// Set once the request has given up. The entry stays queued anyway
            /// so a late reply is absorbed here instead of being handed to the
            /// next request in line — see DispatchResponse.
            /// </summary>
            public bool Abandoned;
        }

        private readonly ISerialTransport _transport;
        private readonly object _sync = new object();
        private readonly StringBuilder _rxBuffer = new StringBuilder();
        private readonly Queue<PendingRequest> _pending = new Queue<PendingRequest>();

        public event Action<string> LogLineReceived;
        public event Action<DeviceSlot> DeviceAttached;
        public event Action<int> DeviceDetached;
        public event Action<LiveSlot> LiveInput;
        public event Action<OutputsState> OutputsChanged;

        /// <summary>Raised when the link drops unexpectedly (unplug, port error).</summary>
        public event Action<Exception> Faulted;

        public bool IsOpen => _transport.IsOpen;

        public SerialClient(ISerialTransport transport)
        {
            _transport = transport;
            _transport.DataReceived += OnDataReceived;
            _transport.ErrorOccurred += OnTransportError;
        }

        public static SerialClient ForPort(string portName) =>
            new SerialClient(new SerialPortTransport(portName));

        public async Task OpenAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            _transport.Open();
            await Task.Delay(OpenSettleDelay, cancellationToken).ConfigureAwait(false);
        }

        public void Close()
        {
            _transport.Close();
            FailAllPending(new InvalidOperationException("disconnected"));
        }

        private void OnTransportError(Exception ex)
        {
            FailAllPending(ex);
            Faulted?.Invoke(ex);
        }

        private void FailAllPending(Exception ex)
        {
            List<PendingRequest> drained;
            lock (_sync)
            {
                drained = new List<PendingRequest>(_pending);
                _pending.Clear();
            }

            foreach (var p in drained)
            {
                p.Timeout?.Dispose();
                p.Registration.Dispose();
                if (!p.Abandoned) p.Completion.TrySetException(ex);
            }
        }

        // ---------- Receive path ----------

        private void OnDataReceived(string chunk)
        {
            var lines = new List<string>();
            lock (_sync)
            {
                _rxBuffer.Append(chunk);
                var all = _rxBuffer.ToString();

                int start = 0;
                int nl;
                while ((nl = all.IndexOf('\n', start)) >= 0)
                {
                    var line = all.Substring(start, nl - start).Trim();
                    if (line.Length > 0) lines.Add(line);
                    start = nl + 1;
                }

                _rxBuffer.Clear();
                if (start < all.Length) _rxBuffer.Append(all, start, all.Length - start);
            }

            foreach (var line in lines) HandleLine(line);
        }

        internal void HandleLine(string line)
        {
            // Firmware console output (banners, [pedals] traces) is interleaved
            // with JSON on the same stream; anything that isn't an object is a
            // log line.
            if (!line.StartsWith("{", StringComparison.Ordinal))
            {
                LogLineReceived?.Invoke(line);
                return;
            }

            JObject msg;
            try
            {
                msg = JObject.Parse(line);
            }
            catch (JsonException)
            {
                LogLineReceived?.Invoke(line);
                return;
            }

            var eventName = msg.Value<string>("event");
            if (!string.IsNullOrEmpty(eventName))
            {
                DispatchEvent(eventName, msg);
                return;
            }

            DispatchResponse(msg, line);
        }

        private void DispatchResponse(JObject msg, string rawLine)
        {
            PendingRequest pend = null;
            lock (_sync)
            {
                if (_pending.Count > 0) pend = _pending.Dequeue();
            }

            if (pend == null)
            {
                // Nothing was waiting — an error the firmware volunteered, or a
                // reply we already gave up on and dropped. Surface it, don't guess.
                LogLineReceived?.Invoke(rawLine);
                return;
            }

            pend.Timeout?.Dispose();
            pend.Registration.Dispose();

            if (pend.Abandoned)
            {
                // A late reply to a timed-out command. Consuming it here is the
                // point: handing it to the next request instead would silently
                // shift every subsequent response by one. (serial.ts removes the
                // timed-out entry from its queue and has exactly that hazard.)
                LogLineReceived?.Invoke("[stale reply to " + pend.Command + "] " + rawLine);
                return;
            }

            pend.Completion.TrySetResult(msg);
        }

        private void DispatchEvent(string eventName, JObject msg)
        {
            switch (eventName)
            {
                case "device_attached":
                    DeviceAttached?.Invoke(msg.ToObject<DeviceSlot>());
                    break;

                case "device_detached":
                    DeviceDetached?.Invoke(msg.Value<int>("slot"));
                    break;

                case "live":
                    LiveInput?.Invoke(ParseLive(msg));
                    break;

                case "outputs":
                    OutputsChanged?.Invoke(msg.ToObject<OutputsState>());
                    break;

                default:
                    LogLineReceived?.Invoke(msg.ToString(Formatting.None));
                    break;
            }
        }

        internal static LiveSlot ParseLive(JObject msg)
        {
            var live = new LiveSlot
            {
                Slot = msg.Value<int>("slot"),
                Buttons = msg.Value<uint?>("buttons") ?? 0u,
            };

            var axes = msg["axes"] as JArray;
            if (axes != null)
            {
                var values = new int[axes.Count];
                for (int i = 0; i < axes.Count; i++) values[i] = (int)axes[i];
                live.Axes = values;
            }

            // The hat field is omitted entirely for devices without one, and is
            // -1 when the hat is centred. Those are different states: "no hat"
            // hides the control, "released" draws it inactive.
            var hat = msg["hat"];
            if (hat != null && hat.Type == JTokenType.Integer)
            {
                live.HasHat = true;
                int dir = (int)hat;
                live.Hat = (dir >= 0 && dir <= 7) ? (int?)dir : null;
            }

            var keys = msg["keys"] as JArray;
            if (keys != null)
            {
                var values = new int[keys.Count];
                for (int i = 0; i < keys.Count; i++) values[i] = (int)keys[i];
                live.Keys = values;
            }

            return live;
        }

        // ---------- Send path ----------

        /// <summary>
        /// Send a command and await its reply. Every command must go through
        /// here — the firmware answers strictly in order, so a write that skips
        /// the queue would shift the matching of every reply after it.
        /// </summary>
        public Task<JObject> SendAsync(JObject command, int timeoutMs = DefaultTimeoutMs,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!_transport.IsOpen) return FromException(new InvalidOperationException("not connected"));

            var commandName = command.Value<string>("cmd") ?? "?";
            var pend = new PendingRequest
            {
                Command = commandName,
                Completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously),
            };

            var timeout = new CancellationTokenSource(timeoutMs);
            pend.Timeout = timeout;
            pend.Registration = timeout.Token.Register(() =>
            {
                lock (_sync) { pend.Abandoned = true; }
                pend.Completion.TrySetException(
                    new TimeoutException("command timeout: " + commandName));
            });

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    lock (_sync) { pend.Abandoned = true; }
                    pend.Completion.TrySetCanceled();
                });
            }

            lock (_sync)
            {
                _pending.Enqueue(pend);
                try
                {
                    _transport.Write(command.ToString(Formatting.None) + "\n");
                }
                catch (Exception ex)
                {
                    pend.Abandoned = true;
                    pend.Completion.TrySetException(ex);
                }
            }

            return pend.Completion.Task;
        }

        private static Task<JObject> FromException(Exception ex)
        {
            var tcs = new TaskCompletionSource<JObject>();
            tcs.SetException(ex);
            return tcs.Task;
        }

        public void Dispose()
        {
            _transport.DataReceived -= OnDataReceived;
            _transport.ErrorOccurred -= OnTransportError;
            Close();
            _transport.Dispose();
        }
    }
}
