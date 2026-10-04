using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Fanadapter.Core;
using SimHub.Plugins;

namespace Fanadapter.SimHub
{
    /// <summary>
    /// One gear or shift command and what became of it. Immutable: the pump
    /// thread publishes a new instance rather than mutating the live one, so a
    /// UI thread reading it never sees a half-written record.
    ///
    /// "Acknowledged" means the adapter answered the command, which is the only
    /// confirmation available on this side of the link — the wheelbase itself
    /// never reports back.
    /// </summary>
    public sealed class DriveActivity
    {
        public DriveActivity(long sequence, string channel, bool acknowledged, string error, DateTime at)
        {
            Sequence = sequence;
            Channel = channel;
            Acknowledged = acknowledged;
            Error = error;
            At = at;
        }

        /// <summary>Increments per command, so a repeat of the same action is still a new event.</summary>
        public long Sequence { get; }

        /// <summary>
        /// Firmware channel key the command targeted — "shift_up", "gear_3" — or
        /// "pedals" for the handshake re-arm. A key rather than a label so
        /// consumers can match on it without string-matching prose.
        /// </summary>
        public string Channel { get; }

        /// <summary>Human-readable form of <see cref="Channel"/>.</summary>
        public string Action =>
            Channel == DriveController.PedalsChannel ? "Re-arm pedals" : HidNames.Channel(Channel);

        public bool Acknowledged { get; }

        /// <summary>Null when acknowledged.</summary>
        public string Error { get; }

        public DateTime At { get; }
    }

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

        /// <summary>Raised on the caller's thread when streaming starts or stops.</summary>
        public event Action StreamingChanged;

        /// <summary>
        /// The last gear/shift command and what became of it, or null if none has
        /// been issued. Volatile because the pump thread writes it and the UI
        /// timer reads it without a lock.
        /// </summary>
        public DriveActivity LastActivity
        {
            get { return _lastActivity; }
            private set { _lastActivity = value; }
        }

        private volatile DriveActivity _lastActivity;
        private long _activitySequence;

        /// <summary>Latest values pushed to the adapter, for the settings UI readout.</summary>
        public int?[] LastSent => _lastSent;

        public string LastError { get; private set; }

        // ---------- Gear / sequential ----------

        /// <summary>Pseudo-channel for the pedal handshake re-arm, which targets no output channel.</summary>
        public const string PedalsChannel = "pedals";

        /// <summary>
        /// Held-gear semantics for an H-pattern shifter on the PC: the gear is
        /// engaged while the control is down and neutral once it is released.
        /// The release only takes effect if this is still the gear we forced —
        /// otherwise letting go of 2nd after already grabbing 3rd would drop the
        /// car into neutral mid-corner.
        /// </summary>
        public void GearPressed(string gearChannel)
        {
            Enqueue(gearChannel, async proto =>
            {
                _heldGear = gearChannel;
                await proto.SetGearAsync(gearChannel).ConfigureAwait(false);
                return true;
            });
        }

        public void GearReleased(string gearChannel)
        {
            Enqueue("gear_N", async proto =>
            {
                // Nothing sent, so nothing to report — a release that lost the
                // race to another gear must not show up as a shift to neutral
                // that never happened.
                if (_heldGear != gearChannel) return false;
                _heldGear = "gear_N";
                await proto.SetGearAsync("gear_N").ConfigureAwait(false);
                return true;
            });
        }

        /// <summary>Latching select — used for gears bound as plain actions.</summary>
        public void SelectGear(string gearChannel)
        {
            Enqueue(gearChannel, async proto =>
            {
                _heldGear = gearChannel;
                await proto.SetGearAsync(gearChannel).ConfigureAwait(false);
                return true;
            });
        }

        public void Shift(ShiftDirection direction)
        {
            Enqueue(direction == ShiftDirection.Up ? "shift_up" : "shift_down",
                async proto =>
                {
                    await proto.PulseShiftAsync(direction).ConfigureAwait(false);
                    return true;
                });
        }

        public void RearmPedals()
        {
            Enqueue(PedalsChannel, async proto =>
            {
                await proto.ResetPedalsAsync().ConfigureAwait(false);
                return true;
            });
        }

        // ---------- Axis streaming ----------

        public void StartStreaming()
        {
            if (IsStreaming) return;

            _streamCancel = new CancellationTokenSource();
            var token = _streamCancel.Token;
            ResetSentState();
            _streamTask = Task.Run(() => StreamAsync(token), token);
            StreamingChanged?.Invoke();
            _log("pedal streaming started");
        }

        /// <summary>
        /// Re-applies the persisted "driving pedals" choice. Called after every
        /// successful connect, which is far more often than once per SimHub
        /// start: SimHub tears its plugins down and rebuilds them on each game
        /// change, and that is precisely why the choice has to be re-applied
        /// rather than clicked again.
        ///
        /// When the flag is set but can't be honoured yet, it is left set and
        /// the reason logged. Every reason is transient — the wrong board is
        /// plugged in, the pedal sources aren't filled in — and clearing it
        /// would silently forget a choice the user never revoked.
        /// </summary>
        public void ResumeIfEnabled()
        {
            var settings = _settings();
            if (settings == null || !settings.AxisStreamingEnabled || IsStreaming) return;

            if (!_session.SupportsDirectOutput)
            {
                _log("pedal streaming is enabled, but this firmware cannot be driven from the PC");
                return;
            }

            if (!settings.HasConfiguredAxis())
            {
                _log("pedal streaming is enabled, but no pedal has a source property");
                return;
            }

            StartStreaming();
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
            StreamingChanged?.Invoke();
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

        /// <summary>
        /// Queues one command and reports what became of it. <paramref name="work"/>
        /// returns false when it decided not to send anything, which keeps
        /// no-ops out of the activity readout.
        /// </summary>
        private void Enqueue(string channel, Func<Protocol, Task<bool>> work)
        {
            if (_commands.IsAddingCompleted) return;

            try
            {
                _commands.Add(async () =>
                {
                    var proto = _session.Protocol;
                    if (proto == null)
                    {
                        // Bound to a wheel button, so this is reachable simply by
                        // pulling a paddle while disconnected. Silence there looks
                        // identical to a broken adapter.
                        Record(channel, false, "not connected");
                        return;
                    }

                    try
                    {
                        if (await work(proto).ConfigureAwait(false)) Record(channel, true, null);
                    }
                    catch (Exception ex)
                    {
                        Record(channel, false, ex.Message);
                        throw; // the pump logs it and holds it in LastError
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // Raced with disposal.
            }
        }

        /// <summary>
        /// Publishes the outcome of one command. Written from the pump thread and
        /// read by the UI timer, so it is swapped as a whole immutable object —
        /// the reader can never see a half-updated one. The sequence number is
        /// what lets the reader tell "shifted up again" from "nothing happened".
        /// </summary>
        private void Record(string channel, bool acknowledged, string error)
        {
            LastActivity = new DriveActivity(
                Interlocked.Increment(ref _activitySequence),
                channel, acknowledged, error, DateTime.Now);
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
