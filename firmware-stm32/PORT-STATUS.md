# STM32H743 firmware — development notes

Engineering record for the STM32 firmware: what's implemented, what was hard, and
what not to re-derive. **Read this before changing firmware code.**

**If you just want to build or wire one, you're in the wrong file — see
[`README.md`](README.md).** Board choice, pin map, wiring, flashing and troubleshooting all live
there. This file deliberately doesn't repeat them.

## Status

**M0–M7 implemented and hardware-validated end to end**, including a
real driving session on a ClubSport DD+. USB-host HID decode, mapping, DAC H-pattern gears,
sequential, handbrake, and the CSL Elite pedal-UART stream all drive the wheelbase, with webconfig
on the board's native USB CDC.

Current hardware: **WeAct MiniSTM32H743VITX** (STM32H743VIT6, 2 MB flash / 1 MB RAM, 25 MHz HSE,
LDO VCORE), both USB controllers live at once. Bring-up happened on a different board — see
[Historical](#historical-fk743m3-bring-up) at the end.

The input-freeze investigation has a **bench-reproduced cause and shipped mitigations**. The final long driving soak requested in [PR #33](https://github.com/Gugic/fanadapter/pull/33) is not recorded as completed: the Simnet pedal's own firmware hangs its USB endpoint when it goes unpolled
mid-stream for ~2 s (measured threshold: survives 1.6 s, dies at 2.0 s), and older firmware
really did create such gaps — console starvation, the 720 ms connect stall, CDC interrupt
pressure. The post-fix bench measured a worst connection-time polling gap of ~120 ms, excluding Save. Full
investigation record, the measured threshold sweep, and the one remaining >0.5 s gap
(`save_config`) in [the freeze sections](#verdict-2026-07-21-threshold-sweep-the-terminal-mechanism-is-the-pedals-own-firmware).

The investigation below is chronological: hypotheses and measurements describe the builds tested at each step. The **VERDICT** and [remaining limitations](#remaining-validation-and-known-limitations) give the latest recorded conclusion. Earlier claims that the console alone explained every freeze were superseded.

Later work is merged: protocol 6 uses no-reply `stream_axes` for PC pedals ([#35](https://github.com/Gugic/fanadapter/pull/35)); releases are independent of Pages and USB labels include the version ([#42](https://github.com/Gugic/fanadapter/pull/42)); browser first-flash serial setup preserves programming success ([#44](https://github.com/Gugic/fanadapter/pull/44)). These PRs do not establish completion of the long hardware soak above.

## Milestones (M0–M7, all hardware-validated)

- **M0 — plumbing:** USART1 RX (interrupt + 16-byte FIFO, ISR-drained, **priority 5 = above USB**
  so command bursts don't overrun); console at **8E1** to match the ROM bootloader.
- **M1 — HID decode + protocol:** generic report-descriptor walker (`hid_parse.c`), 8-slot device
  pool (`usb_input.c`), the `InputSource` vtable (`input_source.c` — the cornerstone), and
  `protocol.c` with shapes matched to webconfig. Verified against 4 devices / 7 HID interfaces.
- **Axis fidelity:** `hid_parse.c` stores the **raw native** logical value (cast to `uint16_t`) — a 10-bit handbrake reads 0..1023, a 12-bit pedal 0..4095, clean
  step-of-1. An earlier version normalized to 0..65535 and produced confusing 16/64-count jumps.
  `scaleAxis` maps `[rawMin,rawMax]` → 0..65535 at eval time.
- **M2 — Config + flash + get/set/save/reset:** `mapping.{c,h}` (Config **1132 B, CONFIG_VERSION 3**, `_Static_assert`-locked; `crc32` verbatim from
  `webconfig/src/lib/crc32.ts`), `json_min.{c,h}` (zero-alloc JSON reader),
  `config_store.{c,h}`. `get_config` streams in small chunks. Current console policy preserves the authoritative reply path; when USB is mounted, the UART mirror may truncate instead of blocking it (see the later mirror fix). **Two flash bugs here cost a bench session — see below.**
- **M3 — DAC H-pattern:** `outputs.{c,h}` — **DAC1 ch1 = PA4 (X), ch2 = PA5 (Y)**, codes map 1:1
  to the schematics voltage table (output buffer on). Non-blocking neutral-transit FSM inserts a 50 ms release between gears. Sequential pulse landed here too: open-drain **PC6/PC7**, idle HIGH,
  non-blocking FSM.
- **M4 — direct output control** (replaced an earlier "serial virtual-device inject" design): the
  PC (SimHub / the web app) owns the mapping and commands outputs directly. `set_gear`,
  `set_outputs` (the original acknowledged axis path; protocol 6 later added no-reply `stream_axes` for the 100 Hz path), `pulse_shift`, `release_outputs`. Semantics = **command
  override**: a PC-set channel wins over the USB mapping, sticky until `release_outputs`. State in
  `mapping.c` (`g_ovr_*`). Standalone mode is unchanged.
- **M5 — handbrake PWM fallback:** TIM3_CH3 on **PC8** (AF2), ARR=4095 / PSC=0 → ~15.6 kHz for an
  RC low-pass. Init is non-fatal — a TIM fault must not brick the USB host. This is the *fallback*;
  the primary path is the pedal stream, and `mapping.c` dual-writes both.
- **M6 — CSL Elite pedal UART (the keystone):** `pedals.{c,h}` on **USART2 (PA2/PA3), 8N1** — the
  CSL Elite V2 state machine (INIT→STEP0→STEP1→STEP2→STREAMING), handshake
  bytes (0x0A→0x1A, 0x05→0x15), 250000↔115200 switch, 2 s warmup drain, lenient any-order STEP2
  collector, CRC-8 (poly 0x8C), 100 Hz 12-byte `0x7B..0x7D` frames. RX is a register-level RXNE
  ring (priority 5, above USB); TX is blocking `HAL_UART_Transmit` (12 B ≈ 1 ms at 115200, gated to
  100 Hz). **This is what makes `set_outputs` and USB-mapped pedals
  electrically reach the wheelbase** — before M6 those values stopped at the snapshot.
- **M7 — polish:** `test_axis`, `test_pulse`, `reboot`, `reset_pedals`, `pedals_status`. All shapes
  match the existing `webconfig/src/lib/serial.ts` wrappers — **no webconfig change
  was needed for bring-up.**

## Bugs already fixed — do not reintroduce

### The clock bug that ate the first session

`SystemClock_Config` **must** call `HAL_PWREx_ConfigSupply(PWR_LDO_SUPPLY)` **before**
`__HAL_PWR_VOLTAGESCALING_CONFIG`. Skip it and `PWR_FLAG_VOSRDY` never sets → the firmware hangs in
that `while` loop forever, before USB ever inits. There's now a timeout that calls `Error_Handler`
(fast LED blink) instead of hanging silently.

### Two flash-store bugs that ate a bench session

1. **The config sector must be in a DIFFERENT flash bank than the running code.** Code runs from
   bank 1; config is **bank 2, sector 0 @ `0x08100000`**. The H7 cannot fetch instructions from a
   bank while it's being erased — a bank-1 config sector **hard-faults the core** on the 128 KB
   erase. (Verified bank 2 is real and not aliased by reading `0x08000000` vs `0x08100000` over the
   ROM bootloader.) `DUAL_BANK` is defined for STM32H743xx, so the HAL unlocks both banks and waits
   on `QW_BANK2`. Erase ~1 s.
2. **Never call `SCB_InvalidateDCache_by_Addr()` with the D-cache DISABLED** (this build never
   enables it). That faulted the core right after a clean erase+program. Removed — with D-cache off,
   memory-mapped flash reads are already coherent. If D-cache is ever enabled, re-add it guarded.

The flash op is still wrapped in `__disable_irq()`/`__enable_irq()`. Its roughly 1 s USB/console pause is a known limitation during driving; see the remaining polling-gap item below.

### Four latent USB bugs, all invisible until a board with two working USB controllers existed

Found the day the WeAct H743VIT6 arrived. The bring-up board had only one usable controller, so the
entire device-side path had never actually run.

1. **CDC RX was never implemented.** `console_cli_poll()` drained only the USART1 ring — there was
   no `tud_cdc_read()` anywhere, despite a comment promising it. The CDC could transmit but never
   read a byte, so every webconfig command vanished. Both sources now feed a shared
   `console_feed_byte()`.
2. **CDC output was gated on DTR.** `console_printf` wrote the CDC only `if (tud_cdc_connected())`,
   which additionally requires the host to assert DTR — and webconfig deliberately **deasserts** it
   (so opening a port can't reset an MCU sitting behind a CH340). Result: the firmware received and
   executed commands but never replied → `command timeout: version`. Now gated on **`tud_mounted()`**,
   which is DTR-independent and also works with terminals that never raise DTR.
3. **USB roles were the wrong way round.** The host cannot live on the on-board USB-C — it's a
   device receptacle. See the README's USB table for the full rationale.
4. **Phantom HID interfaces.** Composite controllers (Logitech RS Shifter & Handbrake, RS H-Shifter)
   each expose a **second HID interface with no axes/buttons/hat/keys**. Those were claimed anyway:
   **4 physical devices filled 7 of the 8 pool slots**, each holding an interrupt-IN host channel.
   `usb_input_on_mount()` now returns whether it claimed the interface and rejects input-less ones
   (the counts come from the report descriptor and are never revised from reports, so such an
   interface can never *become* bindable); `tuh_hid_mount_cb` only arms the pipe when claimed.

### Pedal-UART line integrity (a debug session that was never a code bug)

The pedal port is **unforgiving about wire integrity — suspect the wiring before the firmware.** A
marginal jumper (especially **ground**, or TX PA2 → pedal pin 5) CRC-corrupts the 100 Hz frames; the
wheelbase rejects them and falls back to analog sensing, and pre-fix the adapter kept streaming into
the void → jitter on the now-analog pins. Symptom: handshake completes, pedals show for ~2 s, drop
to analog, repeat. **The fix was re-seating a wire.** The `[pedals]` console trace is the
diagnostic — it showed the adapter reaching `STREAMING` and staying (so the drop was wheelbase-side)
plus a burst of RX framing errors, which is exactly what a flaky line produces.

Which is also why **wheelbase-restart auto-recovery** exists: once STREAMING we ignore RX, so a
power-cycled wheelbase (re-initiating its handshake at 250000 while we're camped at 115200) used to
leave us streaming into a base that had fallen back to analog. The mismatched baud arrives as a
burst of framing/overrun errors; the RX ISR tallies them and STREAMING re-arms the handshake on a
burst (`RESTART_ERR_THRESHOLD = 32`, 1 Hz decay so isolated glitches don't trip it). Manual re-arm remains available through `reset_pedals`.

### "Inputs freeze and everything lags" is the CONSOLE starving the loop — not USB

**Read this before touching `usb_input_task` or believing a USB-host theory.** One evening was
spent chasing this as a USB problem, twice, wrongly. The actual cause is `console_printf`.

Field symptom: minutes into a session with webconfig or the SimHub plugin connected, inputs get
coarse ("low fps"), then freeze at their last values — a pedal caught mid-press holds 100% and the
wheelbase keeps receiving it — gear changes arrive up to a minute late, and the pedal link cycles
`RX error burst` → re-handshake. Rebooting fixes it for a few minutes. Devices stay listed as
connected throughout, which is what makes it read as a stuck HID pipe.

In that build, `live` and `outputs` events were emitted through `console_printf`, which did a
**blocking** `HAL_UART_Transmit` on the USART1 leg for every line, whether or not anything is
attached to USART1. Do the arithmetic at 115200 8E1 (11 bits/char): a `live` line is ~58 chars ≈
**5.5 ms**; an `outputs` line ~110 chars ≈ **10.5 ms**. Inputs stream at up to ~30 Hz and outputs
stream *continuously*, so with both enabled **over half of every second is spent blocked in the
console** — in a loop where `pedals_update()` runs last and needs a 10 ms cadence. The pedal stream
collapses, the wheelbase falls back to analog, and mapping evaluation crawls. At that time both clients enabled both streams on connect without tying them to pane visibility. SimHub now ties input telemetry to its settings pane. The old CDC leg had the same shape: it spun up to 1000
`tud_task()` pumps per line when the host has stopped draining (port closed while still enumerated
— exactly what a closed browser tab or a capture script leaves behind).

Confirmed by turning both streams off on a stuck board: it recovered immediately.

**Fixed.** `console_printf` no longer transmits inline. USART1 output goes through an
interrupt-driven TX ring (`s_tx_ring`, drained by the TXE half of `USART1_IRQHandler`), and the
sink has two policies, selected by the `lossy` flag on `console_emit()`:

- **Responses, CLI, banners** (`console_printf`) — wait for room. Low-rate and must arrive intact:
  `get_config` alone is ~8 KB streamed in chunks, and truncating it breaks every client.
- **`live` / `outputs` events** (`console_event_printf` / `console_event_write`) — dropped when the
  ring is over half full. They are superseded ~30 times a second, so a lost frame costs nothing
  while *waiting* for one costs the pedal stream its cadence. The half-full reserve also means a
  telemetry burst can never squeeze out a command reply.

Two traps found while building it, both worth not repeating:

1. **Bound these waits by wall-clock, never by iteration count.** The first version spun 20 000
   times, described in the comment as "~2 ms" — but 20 000 spins take ~3 ms while draining a
   2 KB ring at 115200 takes ~175 ms. The guard expired with the ring still full, chunks were
   dropped, `get_config` truncated, and every client timed out on connect. Same defect on the CDC
   leg from the other side: a "consecutive no-progress pumps" counter elapses in microseconds,
   while the host only polls the endpoint once per 1 ms frame. Now 50 ms (UART) and 20 ms (CDC).
2. **A line assembled in pieces must be emitted atomically on the lossy path.** `emit_live()` used
   to build its JSON across a dozen `console_printf` calls; each drops independently, so a
   partially-dropped line would put malformed JSON on the wire. It now formats into one buffer and
   calls `console_event_write()` once.

Measured on the board after the fix, with **both** telemetry streams on: command round-trip 8.7 ms
average (10.1 ms with telemetry off — i.e. no penalty), `get_config` 8332 B in 720 ms and parsing
clean, and a 60 s soak with zero `[pedals]` lines and zero malformed lines. Before the fix that
same state produced `RX error burst` → re-handshake within seconds.

#### Two failed attempts at the USB theory — do not repeat them

Both looked obviously right and both were wrong; the negative results are the useful part.

1. **Re-arm idle pipes.** The report pipe is kept alive solely by the `tuh_hid_receive_report()`
   call at the end of `tuh_hid_report_received_cb` (TinyUSB 0.18 never re-arms itself), and that
   call can fail transiently, so a dropped arm would silence a device forever. Real hazard, so
   `usb_input_task()` still re-arms genuinely idle pipes — but it **never fired in the field**,
   which is the evidence that this was not the bug.
2. **Escalate to `tuh_hid_receive_abort()` on silence.** Wrong twice over. A quiet device is
   *indistinguishable* from a wedged one here — a device with nothing to report NAKs, and a
   NAK-looping endpoint reads exactly as busy as a dead one — so it fired constantly on the
   untouched shifters, and aborting a live transfer leaves a partial report to be decoded as
   garbage axis values (observed: two axes swinging full-range, perfectly anti-correlated). Worse,
   logging each episode put `console_printf` in the main loop and reproduced the starvation above
   at full strength. Reverted.

The lesson generalises: **before blaming a peripheral, account for how long the loop is blocked.**
`console_printf` is not free, and anything on the main-loop path that calls it is a latency bug in
waiting.

#### The freeze came back with the console fixed — a USB-host component exists after all (2026-07-21)

The non-blocking console eliminated the lag and the pedal-stream collapse (gear changes instant,
soaks clean), but a session on the rig still produced the original freeze: the Simnet pedal froze
at axis 0 = 4095 mid-drive — throttle held at 100% at the wheelbase — while every other device
kept working and the slot stayed listed. So the console starvation explained the *coarse/late/
laggy* symptoms, but "one device goes silent while still enumerated" has its own cause in the USB
host path (or the device itself hangs until a bus reset — an adapter reboot resets the bus, which
is why rebooting always recovers it).

The failed-attempts list above still stands: blind auto-recovery made things worse. What's new is
**instrumentation to interrogate the next freeze in the act**, wired to buttons on the SimHub
plugin's Devices tab (results land in its Logs tab):

- **`usb_status`** — per claimed slot: `{mounted, busy, reports, age_ms, idle_rearms}`. A healthy
  device near-always reads `busy` (NAK looping); the tell is `age_ms` growing while the user is
  actively moving that device.
- **`usb_kick`** — abort + re-arm every claimed pipe, once, on demand. If the frozen device comes
  back, the wedge is host-side (a dwc2 channel stuck busy) and a *targeted* recovery can be built
  on that evidence. If it stays silent, the device itself stopped talking and the next lever is a
  hub port reset (SetPortFeature(PORT_RESET)), not anything channel-level.

The planned experiment was to run `usb_status` while frozen, then kick and watch. It was completed as recorded next; it is not an outstanding TODO.

**UPDATE (2026-07-21, the experiment ran):** caught live. `usb_status` while frozen: the pedal
slot read `busy:true, reports:57705, age_ms:70900+` and growing, `idle_rearms:0` — armed forever,
never completing, arm never dropped. `usb_kick` aborted and re-armed all four pipes
(`aborted_mask:15`) and the pedal did NOT resume. A replug of just the pedal at the hub revived it
(so hub detection + mid-session re-enumeration are healthy) — once for 15 seconds, once
sustained. The user then observed the decisive correlation: **connecting SimHub kills the pedals**
— which matched the very first field report ("a couple of minutes into SimHub activation"). That
led straight to the root cause below.

### The 16-deep event-queue theory — plausible, "three facts confirmed", and WRONG (2026-07-21)

Kept as a cautionary tale. The theory: `CFG_TUH_TASK_QUEUE_SZ` defaults to 16 (usbh.c:40), a full
queue silently drops events (`TU_ASSERT` at usbh.c:300 is a bare `return false` in release), and
`busy` is only cleared when the event is *processed* (usbh.c:553) — so a connect-time stall lets
the ~450 report/s pedal overflow the queue and a dropped completion wedges its pipe forever. All
three code facts are TRUE, the consequence chain is real, the connect correlation fit — and the
mechanism is still **architecturally impossible**, caught by adversarial review: interrupt-IN
endpoints are strictly ONE-SHOT. An endpoint cannot enqueue completion N+1 until completion N is
consumed, because consumption is what re-arms it (the data path never self-reschedules; only the
NAK path does, and it enqueues nothing). A stall therefore FREEZES event production instead of
piling events up; queue occupancy is bounded by the number of armed endpoints (~6 here) and never
reaches 16. The `CFG_TUH_TASK_QUEUE_SZ 512` bump ships anyway as free headroom — but it fixes
nothing, and the lesson is the method one: "every fact checks out" is not the same as "the
mechanism can occur". Model the system's dynamics, not just its lines.

### Historical hypotheses: three candidate causes in the dwc2 host driver (superseded by the verdict below)

An 18-agent adversarial review of the vendored TinyUSB 0.18 dwc2 host driver (11 hypotheses, 5
refuted with reasons worth reading in the session record) left three mechanisms standing — all in
`hcd_dwc2.c`, all terminating in the exact observed state (`busy:true`, `idle_rearms:0`, only
re-enumeration recovers):

- **Cause A — silently swallowed token post (top-ranked).** Every (re-)arm ends at
  `channel_send_in_token()` (hcd_dwc2.c:198) whose first line asserts on the 8-deep hardware
  periodic request queue; in release a full queue silently returns false, `channel_xfer_start`
  ignores it and returns true unconditionally (:601/:612) — which *defeats the SOF retry*, since
  `handle_sof_irq` only re-kicks on a false kickoff (:1191). Channel allocated (and leaked),
  CHENA never set, no token ever reaches the bus. The pedal re-arms ~450×/s, making it the
  statistically most-exposed endpoint by orders of magnitude. Also explains why `usb_kick` failed
  to revive: the kick's fresh arm can fail the same silent way, leaving `busy:1` again.
- **Cause B — DTERR dead-end branch.** A data-toggle-error interrupt arriving *without* HALTED
  lands in a branch that does nothing (`err_count = 0; TU_ASSERT(false);`, :914-917): no disable,
  no reschedule, no completion. One stochastic bus glitch wedges the pipe permanently.
- **Cause C — channel double-allocation race.** `channel_alloc` (:156) is an unlocked
  check-then-set under `OPT_OS_NONE`; the thread-context re-arm can race the SOF ISR's
  re-scheduler, both grabbing the same channel id and orphaning one endpoint.

A fourth candidate survives on field evidence alone: **the pedal's own firmware hanging when it
goes unpolled mid-stream** (kick-no-revive fits; connect stalls pause polling ~400 ms; replug or
bus reset revives it). Discriminating instrumentation, all pure register reads, no vendored
edits: `usb_status` now includes each slot's USB address plus a dump of the live dwc2 host
channels (`HCCHAR` device/endpoint/CHENA/CHDIS, raw `HCINT`, and the HPTXSTS request-queue
level). At the next freeze the frozen device's channel signature decides: **CHENA=0 → Cause A;
CHENA=1 → Cause B or a hung device; no channel at all → Cause C.** And `usb_stall` (skip
`tuh_task()` for N ms while everything else runs) is an on-demand reproducer for the
stall-correlated trigger — if it works, iteration drops from nights to minutes.

Also worth keeping: **abort + re-arm (`usb_kick`) demonstrably does not restart a wedged pipe on
this stack** — measured live. Never build an auto-recovery on it.

### VERDICT (2026-07-21, threshold sweep): the terminal mechanism is the PEDAL's own firmware

The `usb_stall` sweep measured how much polling silence the Simnet pedal tolerates mid-stream:
100–500 ms → instant full recovery; 600–1300 ms → recovers sluggishly (~0.6–0.9 s); 1600 ms →
barely (+18 reports, 1 s lag); **2000 ms → dead until re-enumeration**. So a device-side hang
past ~2 s of no IN polls is the unfixable core: no host patch can pass a 2 s blackout test, which
is why three successive driver fixes "failed" the bench reproducer — the reproducer sat above the
device's intrinsic death threshold.

The FIELD bug was our firmware actually creating such gaps: the console-starvation era stalled
the loop >50% of every second, and the pre-mirror-fix connect sequence held it ~720 ms with CDC
interrupt pressure on top. The post-fix bench's worst measured gap was ~120 ms during connection, excluding Save. The three dwc2 patches (token-post propagation, queue-full counters, alloc-race masking)
stay: each is a real latent bug, and the counters/alloc_mask remain live diagnostics.

**The rule this leaves behind: never let host polling gaps approach 1 second.** Anything added to
the main loop that can block must stay well under that. Known remaining offender: `save_config`'s
defensive `__disable_irq()` flash erase (~1 s — inside the sluggish zone, near the cliff). Rare
and user-initiated; if pedals stutter right after a Save, that's why. A possible change would remove IRQ masking, but that alone does not guarantee continuous report polling: completed transfers also require main-loop `tuh_task()` service. No fix has been validated.

Two smaller finds from the same session: the `~+` line-noise burst on every port open used to
start an unterminated console line that SWALLOWED the first command sent after connect (fixed:
'{' now always restarts the line assembler — this had masked itself as flaky `dfu` entry and
empty first replies all along), and `usb_stall` remains the tool for re-testing the threshold if
the pedal's firmware ever changes.

### A partially-dropped event line poisoned the reply stream (fixed 2026-07-21)

The lossy CDC leg of `console_emit()` wrote *what fit* into the USB FIFO and bailed when it
filled. Result on the wire, caught in the field:

    {"event":"outputs","gear":"gear_N","shift_up":fals{"ok":true}

— a truncated `outputs` event with the next (intact, non-lossy) command reply glued on. The client
can parse neither line, so the reply is effectively destroyed — and because every client matches
replies strictly FIFO (it must: the firmware answers in order), the queue goes permanently
one-behind: **every subsequent request times out and every reply arrives "stale", until
reconnect.** That was the endless `[stale reply to pedals_status]` spam in the SimHub log.

Fixed on both sides. The lossy paths (UART ring *and* CDC) now check for room for the WHOLE line
before writing a single byte, else drop it entirely — a dropped event is free, a partial one is
poison. And `Fanadapter.Core/SerialClient.cs` self-heals from any future wire corruption: two
consecutive stale absorptions with no success between them trigger a `version` probe whose reply
(the only one carrying an `"fw"` field) re-anchors the queue; one isolated stale still just
absorbs, as before.

### "RX error burst — wheelbase restart?" with no power cycle: the USART1 mirror was starving the loop (fixed 2026-07-21)

The user was right to be suspicious: no one power-cycled anything. The observed chain:

1. Every console write is mirrored to USART1 — **including the ~8.3 KB `get_config` response,
   even when nobody is on USART1** — and the non-lossy path applied backpressure: wait for TX-ring
   room. At 115200 baud, 8E1 uses 11 bits per byte: about 10.5 KB/s, or ~0.8 s to transmit
   8332 bytes. Some bytes can remain queued when the call returns; the measured main-loop stall
   was **~720 ms**. The mirror's backpressure, not CDC speed, caused that delay.
2. `pedals_update()` therefore missed ~70 consecutive frames on every client connect.
3. The wheelbase's own link watchdog declared the stream dead and re-initiated its handshake —
   sending 0x0A at **250000** baud into our 115200 receiver, which manifests as a framing-error
   burst: `RX error burst (33)`, i.e. just past the 32 threshold.
4. Our auto-recovery re-armed and completed the handshake, so it looked like a spontaneous
   wheelbase restart. It was us all along.

Fix: when `tud_mounted()` (USB is enumerated, even if no serial application is reading), the non-lossy
USART1 leg mirrors what fits and drops the rest — it never blocks the loop. When no CDC client is
enumerated, USART1 *is* the console and keeps the full wait-for-room semantics. Two forensic aids
were added so the next anomaly explains itself in the client's timestamped log: the burst line now
breaks down error types (`fe=` wrong-baud traffic from a re-handshaking base, `ore=` our ISR ran
late), and STREAMING logs `[pedals] stream gap N ms — main loop stalled` whenever 10+ frames were
missed — a gap line printed *before* a burst line pins the causality on us.

Related, still true: `save_config` blocks interrupts during a bank-2 flash erase (roughly 1 s in the recorded bench test), which will gap the
stream and provoke exactly this base-side re-handshake. Rare and user-initiated; the gap sentinel
now makes it visible rather than mysterious.

## Gotchas — don't re-derive

- **rhport numbers are FIXED BY HARDWARE; roles are not.** OTG_FS is *always* rhport 0, OTG_HS
  *always* rhport 1, so the ISRs must pass those literals — which is why `main.c` keeps
  `OTGFS_RHPORT`/`OTGHS_RHPORT` separate from the role aliases `DEVICE_RHPORT`/`HOST_RHPORT`.
  Current roles: **rhport 0 = DEVICE (console), rhport 1 = HOST.** Wiring an ISR to a *role* alias
  breaks silently the moment roles swap.
- **`CFG_TUH_HID` must be 8, not 4.** With 4, only the first 4 HID *interfaces* mount — the 4-device
  target presents **7** (two composite shifters), so later devices enumerate at the device level but
  never claim. Each mounted interface arms one interrupt-IN channel; 7 of 16 is comfortable.
- **Host VBUS is a non-issue on H7** (verified in the dwc2 source): `GCCFG.VBDEN` resets to 0 (VBUS
  assumed valid), FS-PHY init only sets `PWRDWN`, and `hcd_dwc2.c` writes `HPRT_POWER`. The host
  doesn't gate on a VBUS pin, so PA9 stays free for USART1 and no GCCFG poking is needed. (The *hub*
  still needs to see VBUS on its own upstream — that's a hub-side requirement, not an MCU one.)
- **Feedback-storm risk:** `console_printf` writes the USB CDC, so routing TinyUSB debug to it
  (`CFG_TUSB_DEBUG > 0`) can storm (log → CDC write → USB activity → log). Keep `CFG_TUSB_DEBUG 0`,
  or point `CFG_TUSB_DEBUG_PRINTF` at a UART-only writer when you need enumeration tracing.
- **Don't gate CDC output on `tud_cdc_connected()`** — see bug 2 above. Use `tud_mounted()`. Same
  trap applies to any new inbound path: drain **both** the USART1 ring and `tud_cdc_read()`.
- **The WeAct PCB also ships with an STM32H723VGT6**, which has one USB controller and cannot run
  this firmware. Verify the chip before debugging a "dead" board — full detail in the README.
- **DAC gear outputs take no RC filter.** A cap on the DAC output buffer can oscillate.

## Remaining validation and known limitations

- **Input-freeze: field-confirm the fix.** Bench verdict is in (device hangs past ~2 s unpolled;
  the measured connection-time gap was ~120 ms, excluding Save — see the VERDICT section). Remaining proof: a SimHub-connected
  drive-mode session, the scenario that used to freeze the pedal within minutes. If it EVER
  freezes again, `usb_stall` re-measures the threshold and `usb_status` (counters + alloc_mask +
  channel dump) says which side failed.
- **`save_config` polling gap.** The defensive `__disable_irq()` around the ~1 s flash erase
  stops SOF polling — inside the pedal's sluggish zone. Removing IRQ masking is an unimplemented candidate, not a proven fix: host completions are one-shot and `tuh_task()` must also keep running to re-arm them. Any change needs polling-gap measurements and config-integrity tests.

Per-wheelbase DAC calibration is a supported user operation (`set_gear_dac`, then `save_config`), not unfinished firmware. The defaults engaged every gear on the tested DD+.

## Historical: FK743M3 bring-up

M0–M7 were developed and first validated on an **FK743M3-VGT6** board, which turned out to have
**only one usable USB controller**: PB15 (OTG_HS D+) is a 0.1 mm via with no pad — unsolderable. So
OTG_FS carried the host and there was no device port at all; the console was read over **USART1 +
an ESP32-S3/CH340 bridge** (`tools/esp32s3_uart_bridge/`), and webconfig connected to the bridge's
COM port rather than the board. **That is why the four CDC/HID bugs above survived the entire
bring-up unnoticed.** On that board the USB-C was OTG_FS (PA11/PA12, also mirrored on the A11/A12
header) — the "B15" silkscreen near it was a red herring.

`tools/flash.ps1` + `tools/stm32_uart_flash.py` date from there: a DTR-safe AN3155 UART flasher that
flashes over the bridge (build → flash → Go), prompting you to hold BOOT0 + tap RST. The tools remain for UART recovery with compatible H743 hardware and the documented bridge wiring; they do not make an H723 compatible. Use hardware BOOT0/RST for UART entry. The tested software-entry path is USB DFU; hands-free USART entry was not validated. Do not revive the old `BOOT_ADD0` experiment: it stranded the board in the bootloader.

### Hands-free DFU entry: only the token + reset + early-branch works (July 2026)

The FK-era note above said a software jump comes up "USB-DFU-only" — **that was an assumption, and
it is false.** Bisected on the WeAct board: a **late branch from the running app serves NO ROM
interfaces at all.** Two variants tried and both left the bus permanently dark until an RST tap:

1. The original `jump_to_bootloader()` (clock-gate OTGs → `HAL_RCC_DeInit` → branch). Worse than
   dark: the clock gate froze the D+ pullup latched high, so the host never saw a detach — Windows
   kept a zombie COM port that failed opens with "device not functioning". Looked exactly like a
   wedged board.
2. Same, plus a proper `tud_disconnect()` + OTG force-reset first. Clean detach (no zombie port),
   but still no DFU enumeration — proving the dirty-peripheral theory insufficient. The ROM simply
   will not start its interfaces when entered by a branch from a running app.

**What works: park a magic token at DTCM base (`0x20000000` — all app RAM is in AXI `0x24000000`,
so startup never touches it, and DTCM survives `NVIC_SystemReset`), do a real reset, and branch to
`0x1FF09800` as the FIRST statement of `main()`** — before `HAL_Init`, on a reset-default chip,
which is the state the ROM actually expects. Validated end-to-end with zero button presses:
`dfu` command → reset → ROM DFU enumerates → `platformio -t upload` → `:leave` boots the app →
all hub devices re-enumerate. `check_bootloader_request()` / `request_bootloader_reboot()` in
`main.c`. The one-shot token clear means a crash after entry can't loop the board into the ROM.

The flasher's **bank-1-only erase** (AN3155 special code `0xFFFE`) is what preserves saved config
across reflashes — the ~58 KB app lives entirely in bank 1, so bank 2 @ `0x08100000` survives.
`flash.ps1 -MassErase` explicitly wipes both banks. The UART flasher also falls back to a global erase if `0xFFFE` is rejected, **which loses saved configuration**. A `CONFIG_VERSION` bump needs no mass erase: the application rejects the old blob itself. The browser and SimHub DfuSe flashers erase only the bank-1 sectors covered by the image and do not have this global-erase fallback.

CherryUSB was briefly explored and removed; no `lib/cherryusb` directory remains. TinyUSB is fetched by PlatformIO and patched at build time.
