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
        /// Target period between pedal updates. The real ceiling is the serial
        /// round-trip, not this: each set_outputs waits for its reply, and that
        /// measured ~13 ms on an STM32 over native USB CDC — about 77 Hz when
        /// the pedals are moving continuously. That is far above what a human
        /// foot produces, so the loop is left to self-limit rather than pipeline
        /// commands, which would trade back-pressure for an unbounded queue if
        /// the adapter ever fell behind.
        ///
        /// Ticks where nothing changed cost no round-trip at all (see the change
        /// suppression in Read), so the link stays idle when the pedals do.
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
            bool keepAlive = DateTime.UtcNow - _lastKeepAlive >= KeepAlive;

            int? throttle = Read(pm, settings.Throttle, 0, keepAlive);
            int? brake = Read(pm, settings.Brake, 1, keepAlive);
            int? clutch = Read(pm, settings.Clutch, 2, keepAlive);
            int? handbrake = Read(pm, settings.Handbrake, 3, keepAlive);

            if (throttle == null && brake == null && clutch == null && handbrake == null) return;

            if (keepAlive) _lastKeepAlive = DateTime.UtcNow;

            await proto.SetOutputsAsync(throttle, brake, clutch, handbrake).ConfigureAwait(false);
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
