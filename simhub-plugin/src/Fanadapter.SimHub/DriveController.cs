using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Fanadapter.Core;
using SimHub.Plugins;

namespace Fanadapter.SimHub
{
    /// <summary>
    /// Turns SimHub input into wheelbase output.
    ///
    /// SimHub owns the input half entirely — it enumerates the PC's controllers,
    /// runs the capture UI and decides when a mapped control is pressed. This
    /// class only reacts to that and issues the firmware's direct-output
    /// commands, so none of SimHub's device handling is reimplemented here.
    ///
    /// The overrides these commands set are **sticky**: the firmware has no
    /// timeout, so whatever was last commanded stays applied until
    /// release_outputs. Every path that stops driving has to release, or the
    /// wheelbase is left holding a gear and a set of pedal positions.
    /// </summary>
    public class DriveController : IDisposable
    {
        /// <summary>
        /// Target period between pedal updates — 100 Hz, matching the pedal
        /// frame rate the adapter sends the wheelbase, so streaming faster
        /// buys nothing. Two transports, chosen per tick by firmware
        /// capability:
        ///
        /// * protocol >= 6: stream_axes, fire-and-forget. No reply, no
        ///   round-trip in the hot path, no change suppression — every tick
        ///   carries the latest full-resolution values, which also makes the
        ///   stream its own keep-alive. Cost is one-way transit (~2 ms).
        /// * older firmware: acked set_outputs with change suppression and a
        ///   1 s keep-alive. The ack round-trip (7.9 ms measured post-#33,
        ///   13 ms before) is the ceiling and the jitter source; the loop
        ///   self-limits rather than pipelining, which would trade
        ///   back-pressure for an unbounded queue if the adapter fell behind.
        /// </summary>
        private const int StreamPeriodMs = 10;

        /// <summary>
        /// Resend an unchanged channel this often anyway, so a value that
        /// happens to sit still is re-asserted after any adapter-side reset.
        /// </summary>
        private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Ignore sub-0.1% wobble. Streaming every LSB of a noisy pot would fill
        /// the link with traffic that changes nothing.
        /// </summary>
        private const int ChangeEpsilon = 64;

        private readonly AdapterSession _session;
        private readonly Func<PluginManager> _pluginManager;
        private readonly Func<DriveSettings> _settings;
        private readonly Action<string> _log;

        // Commands are queued rather than fired directly: SimHub raises input
        // callbacks on its own threads, and a gear press must not overtake the
        // release that came before it.
        private readonly BlockingCollection<Func<Task>> _commands =
            new BlockingCollection<Func<Task>>(new ConcurrentQueue<Func<Task>>());

        private readonly Task _pump;
        private CancellationTokenSource _streamCancel;
        private Task _streamTask;

        /// <summary>Gear channel this plugin currently has forced, or null.</summary>
        private string _heldGear;

        private readonly int?[] _lastSent = new int?[4];
        private DateTime _lastKeepAlive = DateTime.MinValue;

        public DriveController(AdapterSession session, Func<PluginManager> pluginManager,
            Func<DriveSettings> settings, Action<string> log)
        {
            _session = session;
            _pluginManager = pluginManager;
            _settings = settings;
            _log = log;

            _pump = Task.Run(PumpAsync);
        }

        public bool IsStreaming => _streamTask != null && !_streamTask.IsCompleted;

        /// <summary>Latest values pushed to the adapter, for the settings UI readout.</summary>
        public int?[] LastSent => _lastSent;

        public string LastError { get; private set; }

        // ---------- Gear / sequential ----------

        /// <summary>
        /// Held-gear semantics for an H-pattern shifter on the PC: the gear is
        /// engaged while the control is down and neutral once it is released.
        /// The release only takes effect if this is still the gear we forced —
        /// otherwise letting go of 2nd after already grabbing 3rd would drop the
        /// car into neutral mid-corner.
        /// </summary>
        public void GearPressed(string gearChannel)
        {
            Enqueue(async proto =>
            {
                _heldGear = gearChannel;
                await proto.SetGearAsync(gearChannel).ConfigureAwait(false);
            });
        }

        public void GearReleased(string gearChannel)
        {
            Enqueue(async proto =>
            {
                if (_heldGear != gearChannel) return;
                _heldGear = "gear_N";
                await proto.SetGearAsync("gear_N").ConfigureAwait(false);
            });
        }

        /// <summary>Latching select — used for gears bound as plain actions.</summary>
        public void SelectGear(string gearChannel)
        {
            Enqueue(async proto =>
            {
                _heldGear = gearChannel;
                await proto.SetGearAsync(gearChannel).ConfigureAwait(false);
            });
        }

        public void Shift(ShiftDirection direction)
        {
            Enqueue(proto => proto.PulseShiftAsync(direction));
        }

        public void RearmPedals()
        {
            Enqueue(proto => proto.ResetPedalsAsync());
        }

        // ---------- Axis streaming ----------

        public void StartStreaming()
        {
            if (IsStreaming) return;

            _streamCancel = new CancellationTokenSource();
            var token = _streamCancel.Token;
            ResetSentState();
            _streamTask = Task.Run(() => StreamAsync(token), token);
            _log("pedal streaming started");
        }

        /// <summary>
        /// Stops the stream and waits for it to actually stop before returning.
        /// Callers release overrides afterwards, and a tick still in flight would
        /// re-apply them immediately after the release.
        /// </summary>
        public void StopStreaming()
        {
            var cancel = _streamCancel;
            var task = _streamTask;
            _streamCancel = null;
            _streamTask = null;

            if (cancel == null) return;

            cancel.Cancel();
            try { task?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { /* cancellation */ }
            cancel.Dispose();

            ResetSentState();
            _log("pedal streaming stopped");
        }

        private void ResetSentState()
        {
            for (int i = 0; i < _lastSent.Length; i++) _lastSent[i] = null;
            _lastKeepAlive = DateTime.MinValue;
        }

        private async Task StreamAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;

                try
                {
                    await SendTickAsync().ConfigureAwait(false);
                    LastError = null;
                }
                catch (Exception ex)
                {
                    // One bad tick shouldn't kill the stream — the adapter may
                    // just have been busy. Record it and try again next period.
                    LastError = ex.Message;
                }

                var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                var remaining = StreamPeriodMs - elapsed;
                if (remaining > 0)
                {
                    try { await Task.Delay(remaining, token).ConfigureAwait(false); }
                    catch (TaskCanceledException) { return; }
                }
            }
        }

        private async Task SendTickAsync()
        {
            var proto = _session.Protocol;
            var pm = _pluginManager();
            if (proto == null || pm == null) return;

            var settings = _settings();
            var version = _session.Version;

            if (version != null && version.SupportsAxisStreaming)
            {
                // Fire-and-forget path: send every configured axis at full
                // resolution every tick. No suppression epsilon (which cost 64
                // counts of dead-band), no separate keep-alive (the stream is
                // one), no ack wait. See StreamPeriodMs for the rationale.
                int? throttle = ReadRaw(pm, settings.Throttle, 0);
                int? brake = ReadRaw(pm, settings.Brake, 1);
                int? clutch = ReadRaw(pm, settings.Clutch, 2);
                int? handbrake = ReadRaw(pm, settings.Handbrake, 3);

                if (throttle == null && brake == null && clutch == null && handbrake == null) return;

                proto.StreamAxes(throttle, brake, clutch, handbrake);
                return;
            }

            // Legacy acked path (Teensy-era or pre-protocol-6 STM32 firmware).
            bool keepAlive = DateTime.UtcNow - _lastKeepAlive >= KeepAlive;

            int? t = Read(pm, settings.Throttle, 0, keepAlive);
            int? b = Read(pm, settings.Brake, 1, keepAlive);
            int? c = Read(pm, settings.Clutch, 2, keepAlive);
            int? h = Read(pm, settings.Handbrake, 3, keepAlive);

            if (t == null && b == null && c == null && h == null) return;

            if (keepAlive) _lastKeepAlive = DateTime.UtcNow;

            await proto.SetOutputsAsync(t, b, c, h).ConfigureAwait(false);
        }

        /// <summary>
        /// Current value for a channel with no change suppression — the
        /// fire-and-forget stream re-sends every tick by design. Still null
        /// for unconfigured or non-numeric sources.
        /// </summary>
        private int? ReadRaw(PluginManager pm, AxisSource source, int index)
        {
            if (source == null || !source.IsConfigured) return null;

            var scaled = source.Scale(pm.GetPropertyValue(source.PropertyName));
            if (scaled == null) return null;

            _lastSent[index] = scaled; // keeps the settings-UI readout live
            return scaled;
        }

        /// <summary>
        /// Current value for a channel, or null when there is nothing new to send
        /// — an unconfigured source, a property that isn't numeric, or a value
        /// that hasn't moved since last tick.
        /// </summary>
        private int? Read(PluginManager pm, AxisSource source, int index, bool keepAlive)
        {
            if (source == null || !source.IsConfigured) return null;

            var scaled = source.Scale(pm.GetPropertyValue(source.PropertyName));
            if (scaled == null) return null;

            var previous = _lastSent[index];
            if (!keepAlive && previous.HasValue && Math.Abs(previous.Value - scaled.Value) < ChangeEpsilon)
            {
                return null;
            }

            _lastSent[index] = scaled;
            return scaled;
        }

        // ---------- Releasing ----------

        /// <summary>
        /// Hands every channel back to the adapter's own USB mapping. Safe to
        /// call when not connected or not driving.
        /// </summary>
        public void ReleaseAll()
        {
            StopStreaming();
            _heldGear = null;

            var proto = _session.Protocol;
            if (proto == null) return;

            try
            {
                // Deliberately synchronous with a bounded wait: this runs from
                // plugin shutdown, and letting the process tear down before the
                // release reaches the adapter is exactly the failure it prevents.
                proto.ReleaseOutputsAsync().Wait(TimeSpan.FromSeconds(2));
                _log("outputs released back to the adapter's own mapping");
            }
            catch (Exception ex)
            {
                _log("release failed: " + ex.Message);
            }
        }

        // ---------- Command pump ----------

        private void Enqueue(Func<Protocol, Task> work)
        {
            if (_commands.IsAddingCompleted) return;

            try
            {
                _commands.Add(async () =>
                {
                    var proto = _session.Protocol;
                    if (proto == null) return;
                    await work(proto).ConfigureAwait(false);
                });
            }
            catch (InvalidOperationException)
            {
                // Raced with disposal.
            }
        }

        private async Task PumpAsync()
        {
            foreach (var work in _commands.GetConsumingEnumerable())
            {
                try
                {
                    await work().ConfigureAwait(false);
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    _log("drive command failed: " + ex.Message);
                }
            }
        }

        public void Dispose()
        {
            ReleaseAll();
            _commands.CompleteAdding();
            try { _pump.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { /* shutting down */ }
            _commands.Dispose();
        }
    }
}
