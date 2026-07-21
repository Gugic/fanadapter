using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fanadapter.Core
{
    /// <summary>
    /// Typed wrappers over the firmware's JSON command set. Mirrors the command
    /// catalogue in firmware/protocol.cpp and firmware-stm32/src/protocol.c —
    /// see the cross-file invariants table in AGENTS.md before changing a shape.
    /// </summary>
    public class Protocol
    {
        private readonly SerialClient _client;

        public Protocol(SerialClient client)
        {
            _client = client;
        }

        public SerialClient Client => _client;

        // ---------- Query ----------

        public async Task<VersionInfo> GetVersionAsync() =>
            (await SendAsync(Cmd("version")).ConfigureAwait(false)).ToObject<VersionInfo>();

        public async Task<List<DeviceSlot>> ListDevicesAsync()
        {
            var reply = await SendAsync(Cmd("list_devices")).ConfigureAwait(false);
            var devices = reply["devices"] as JArray;
            return devices == null
                ? new List<DeviceSlot>()
                : devices.ToObject<List<DeviceSlot>>();
        }

        // get_config is the one big reply; the STM32 streams it in chunks, so it
        // gets a longer budget than the 3 s default.
        public async Task<Config> GetConfigAsync() =>
            (await SendAsync(Cmd("get_config"), 6000).ConfigureAwait(false)).ToObject<Config>();

        public async Task<PedalsStatus> GetPedalsStatusAsync() =>
            (await SendAsync(Cmd("pedals_status")).ConfigureAwait(false)).ToObject<PedalsStatus>();

        // ---------- Config writes ----------
        // There is no set_config: the firmware keeps a working Config in RAM and
        // every write is granular. Pushing a whole profile means iterating these
        // and finishing with SaveConfigAsync.

        /// <summary>
        /// Writes one binding slot. Fields absent from <paramref name="fields"/>
        /// keep their current firmware-side value, so a single knob can be
        /// changed without resending the whole binding.
        /// </summary>
        public Task SetBindingAsync(string channel, int slot, JObject fields)
        {
            var cmd = Cmd("set_binding");
            cmd["channel"] = channel;
            cmd["slot"] = slot;
            cmd["binding"] = fields;
            return SendOkAsync(cmd);
        }

        public Task SetBindingAsync(string channel, int slot, InputBinding binding) =>
            SetBindingAsync(channel, slot, JObject.FromObject(binding));

        public Task ClearBindingAsync(string channel, int slot) =>
            SetBindingAsync(channel, slot, InputBinding.None());

        public Task SetGearDacAsync(string gearChannel, int x, int y)
        {
            var cmd = Cmd("set_gear_dac");
            cmd["channel"] = gearChannel;
            cmd["x"] = x;
            cmd["y"] = y;
            return SendOkAsync(cmd);
        }

        public Task SetPulseMsAsync(int value)
        {
            var cmd = Cmd("set_pulse_ms");
            cmd["value"] = value;
            return SendOkAsync(cmd);
        }

        public Task SetGearModeAsync(GearMode mode)
        {
            var cmd = Cmd("set_gear_mode");
            cmd["value"] = mode == GearMode.Latch ? "latch" : "hold";
            return SendOkAsync(cmd);
        }

        /// <summary>Persists the working config to EEPROM / flash.</summary>
        public Task SaveConfigAsync() => SendOkAsync(Cmd("save_config"), 5000);

        /// <summary>Restores defaults in RAM only — not persisted until SaveConfigAsync.</summary>
        public Task ResetConfigAsync() => SendOkAsync(Cmd("reset_config"));

        // ---------- Telemetry ----------

        public Task SetLiveInputsAsync(bool on)
        {
            var cmd = Cmd("live_inputs");
            cmd["on"] = on;
            return SendOkAsync(cmd);
        }

        public Task SetLiveOutputsAsync(bool on)
        {
            var cmd = Cmd("live_outputs");
            cmd["on"] = on;
            return SendOkAsync(cmd);
        }

        // ---------- Bench tests (500 ms hold, then back to the mapping) ----------

        public Task TestAxisAsync(string channel, int value)
        {
            var cmd = Cmd("test_axis");
            cmd["channel"] = channel;
            cmd["value"] = value;
            return SendOkAsync(cmd);
        }

        public Task TestGearAsync(string gearChannel)
        {
            var cmd = Cmd("test_gear");
            cmd["channel"] = gearChannel;
            return SendOkAsync(cmd);
        }

        public Task TestPulseAsync(ShiftDirection direction)
        {
            var cmd = Cmd("test_pulse");
            cmd["direction"] = Wire(direction);
            return SendOkAsync(cmd);
        }

        // ---------- Direct output (PC drives the wheelbase) ----------
        //
        // STM32 firmware only — the Teensy dispatcher answers unknown_cmd. These
        // override the adapter's own USB-device mapping for the channels they
        // touch, and the override is STICKY: there is no firmware-side timeout,
        // so whatever was last commanded stays applied until ReleaseOutputsAsync.
        // Every path that stops driving must call it, or the wheelbase is left
        // holding the last gear and pedal positions.

        public Task SetGearAsync(string gearChannel)
        {
            var cmd = Cmd("set_gear");
            cmd["gear"] = gearChannel;
            return SendOkAsync(cmd);
        }

        /// <summary>
        /// Sets any subset of the four axis outputs (0..65535). Channels left
        /// null keep whatever they had — an unset channel still follows the
        /// adapter's own mapping if it was never overridden.
        /// </summary>
        public Task SetOutputsAsync(int? throttle = null, int? brake = null, int? clutch = null, int? handbrake = null)
        {
            var cmd = Cmd("set_outputs");
            if (throttle.HasValue) cmd["throttle"] = throttle.Value;
            if (brake.HasValue) cmd["brake"] = brake.Value;
            if (clutch.HasValue) cmd["clutch"] = clutch.Value;
            if (handbrake.HasValue) cmd["handbrake"] = handbrake.Value;
            return SendOkAsync(cmd);
        }

        /// <summary>Momentary pulse of the configured width — not a sticky state.</summary>
        public Task PulseShiftAsync(ShiftDirection direction)
        {
            var cmd = Cmd("pulse_shift");
            cmd["direction"] = Wire(direction);
            return SendOkAsync(cmd);
        }

        /// <summary>Drops every override at once and hands control back to the adapter's mapping.</summary>
        public Task ReleaseOutputsAsync() => SendOkAsync(Cmd("release_outputs"));

        // ---------- Housekeeping ----------

        /// <summary>
        /// Soft-resets the board. USB drops within ~100 ms of the reply, so the
        /// caller should close the port immediately after this returns.
        /// </summary>
        public Task RebootAsync() => SendOkAsync(Cmd("reboot"), 1500);

        /// <summary>
        /// Reboots into the ROM bootloader for a firmware flash: the COM port
        /// disappears and a USB DFU device (0483:DF11) takes its place. STM32
        /// only — the Teensy answers unknown_cmd, same as the direct-output
        /// commands. The plugin has no flasher; webconfig's Flash dialog (or
        /// dfu-util) does the programming. Close the port right after this.
        /// </summary>
        public Task DfuAsync() => SendOkAsync(Cmd("dfu"), 1500);

        /// <summary>Re-arms the CSL Elite pedal-port handshake from step 0.</summary>
        public Task ResetPedalsAsync() => SendOkAsync(Cmd("reset_pedals"));

        // ---------- Plumbing ----------

        private static JObject Cmd(string name) => new JObject { ["cmd"] = name };

        internal static string Wire(ShiftDirection d) => d == ShiftDirection.Up ? "up" : "down";

        private Task<JObject> SendAsync(JObject cmd, int timeoutMs = SerialClient.DefaultTimeoutMs) =>
            _client.SendAsync(cmd, timeoutMs);

        /// <summary>
        /// Sends a command whose reply is {"ok":true} or {"err":"..."} and turns
        /// the error form into an exception, so callers don't each have to check.
        /// </summary>
        private async Task SendOkAsync(JObject cmd, int timeoutMs = SerialClient.DefaultTimeoutMs)
        {
            var reply = await _client.SendAsync(cmd, timeoutMs).ConfigureAwait(false);
            var err = reply.Value<string>("err");
            if (!string.IsNullOrEmpty(err))
            {
                throw new FirmwareErrorException(cmd.Value<string>("cmd"), err);
            }
        }
    }

    /// <summary>The firmware answered <c>{"err": "..."}</c>.</summary>
    public class FirmwareErrorException : Exception
    {
        public string Command { get; }
        public string ErrorCode { get; }

        public FirmwareErrorException(string command, string errorCode)
            : base("firmware rejected '" + command + "': " + errorCode)
        {
            Command = command;
            ErrorCode = errorCode;
        }

        /// <summary>
        /// True when the firmware simply doesn't know the command — the marker
        /// for a Teensy being asked to do direct output.
        /// </summary>
        public bool IsUnknownCommand => ErrorCode == "unknown_cmd";
    }
}
