import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Plug, Unplug, Save, RotateCcw, Mic, Trash2, Play, AlertCircle,
  Loader2, CheckCircle2, Cable, X, Power,
} from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Slider } from "@/components/ui/slider";
import { Switch } from "@/components/ui/switch";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { SerialClient, type ProtocolEvent } from "@/lib/serial";
import {
  CHANNELS, GEAR_KEYS, MAX_BINDINGS_PER_CHANNEL, NONE_BINDING,
  firstEmptySlot, getBinding, getChannelBindings,
  type ChannelBindings, type ChannelKey, type Config, type DeviceSlot,
  type GearKey, type InputBinding, type LiveSlot, type OutputsEvent,
  type VersionInfo,
} from "@/lib/types";
import { scaleAxisJS } from "@/lib/scaleAxis";

// ------------------ Helpers ------------------

function hex(n: number, w = 4) {
  return n.toString(16).toUpperCase().padStart(w, "0");
}

function vidPid(b: { vid: number; pid: number }) {
  return `${hex(b.vid)}:${hex(b.pid)}`;
}

function bindingSummary(b: InputBinding): string {
  if (b.type === "none" || !b.vid) return "Unmapped";
  const kind = b.type === "button" ? `button ${b.index}` : `axis ${b.index}`;
  return `${vidPid(b)} · ${kind}`;
}

function pct(value: number, max = 65535): string {
  return `${((value / max) * 100).toFixed(1)}%`;
}

// Pick a bar-display max that auto-scales to the device's effective range.
// Below 512 raw we assume "not enough motion observed yet" and fall back to
// the 16-bit max so a tiny jitter doesn't look like 100 %.
function effectiveAxisMax(observed: number | undefined): number {
  if (!observed || observed < 512) return 65535;
  return Math.ceil(observed * 1.05);
}

// ------------------ Live bar ------------------

function Bar({
  value,
  max = 65535,
  className = "",
}: {
  value: number;
  max?: number;
  className?: string;
}) {
  const w = Math.max(0, Math.min(100, (value / max) * 100));
  return (
    <div className={"relative h-2.5 w-full overflow-hidden rounded-full bg-secondary " + className}>
      <div
        className="absolute inset-y-0 left-0 bg-primary"
        style={{ width: `${w}%`, transition: "width 80ms linear" }}
      />
    </div>
  );
}

// ------------------ Capture state ------------------
// Three-phase capture:
//   Phase 1 ("baseline"): 400 ms during which we sample each connected
//     slot's buttons + axes to learn the noise floor at rest. The user is
//     prompted to release / not touch inputs.
//   Phase 2 ("active"): wait for the first NEW button bit or for an axis
//     to deviate from its baseline midpoint by max(noise*5, 500 raw).
//     Button triggers commit immediately. Axis triggers latch the axis
//     identity and move to phase 3.
//   Phase 3 ("tracking"): keep accumulating the peak value on the latched
//     axis. Commit when the axis returns to within noise band of the
//     baseline midpoint, i.e. the user has let go. This way rawMax
//     reflects the full press, not the moment we first noticed motion.
// Auto-calibration: rawMin near the baseline minimum, rawMax at observed
// peak across phase 3, deadzoneLow at 3× scaled noise to absorb idle
// jitter (e.g. brake pedal at 1 %).

interface SlotBaseline {
  buttons: number;
  axesMin: number[];
  axesMax: number[];
}

interface TrackingState {
  channel: ChannelKey;
  bindingSlot: number;   // which slot within the channel's bindings array
  deviceSlot: number;    // which device-pool slot
  axisIdx: number;
  vid: number;
  pid: number;
  baselineMid: number;
  noise: number;
  // Track both directions so inverted axes (e.g. resting-high pedals) are
  // calibrated correctly. Whichever side has the larger deviation from
  // baselineMid at commit time wins; the binding is set up with the right
  // rawMin/rawMax (and invert flag for descending axes).
  peakHigh: number;
  peakLow: number;
  triggerThreshold: number;
}

interface CaptureState {
  channel: ChannelKey;
  bindingSlot: number;   // slot within the channel's bindings array
  phase: "baseline" | "active" | "tracking";
  baselineEnd: number;
  deadline: number;
}

// Baseline data lives in a ref so the per-tick accumulation doesn't trigger
// a render every time a live event arrives. Capture state in React is just
// the metadata that drives UI (phase, deadline).

// ------------------ App ------------------

export default function App() {
  const [client] = useState(() => new SerialClient());
  const [connected, setConnected] = useState(false);
  const [supported] = useState(() => typeof navigator !== "undefined" && "serial" in navigator);
  const [version, setVersion] = useState<VersionInfo | null>(null);
  const [devices, setDevices] = useState<DeviceSlot[]>([]);
  const [config, setConfig] = useState<Config | null>(null);
  // Mirror of `config` for non-render code paths (capture/commit handlers).
  // Reading state inside setTimeout / Promise callbacks via closure would
  // see stale data — the ref always has the latest.
  const configRef = useRef<Config | null>(null);
  configRef.current = config;
  const [outputs, setOutputs] = useState<OutputsEvent | null>(null);
  // Rolling buffer of firmware log lines (non-JSON output from Serial.println).
  // Ring-limited so a busy firmware can't OOM the tab.
  const [logs, setLogs] = useState<{ ts: number; line: string }[]>([]);
  const [liveSlots, setLiveSlots] = useState<Map<number, LiveSlot>>(new Map());
  // observed peak raw value per `${slot}:${axisIdx}`. Auto-scales the bar
  // display so a 10-bit (0..1023) handbrake or 12-bit (0..4095) pedal
  // fills the bar instead of barely registering against the 16-bit max.
  const [axisMax, setAxisMax] = useState<Map<string, number>>(new Map());
  const [dirty, setDirty] = useState(false);
  const [capturing, setCapturing] = useState<CaptureState | null>(null);
  const baselineRef = useRef<Map<number, SlotBaseline>>(new Map());
  const trackingRef = useRef<TrackingState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string>("");
  const [tab, setTab] = useState<string>("devices");

  // ------------------ Connection lifecycle ------------------

  const handleConnect = useCallback(async () => {
    try {
      setError(null);
      setStatus("Opening port…");
      await client.connect();
      setConnected(true);
      setStatus("Reading firmware…");
      const v = await client.version();
      setVersion(v);
      const devs = await client.listDevices();
      setDevices(devs);
      const cfg = await client.getConfig();
      setConfig(cfg);
      await client.setLiveInputs(true);
      await client.setLiveOutputs(true);
      setStatus("");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setStatus("");
      try { await client.disconnect(); } catch { /* ignore */ }
      setConnected(false);
    }
  }, [client]);

  const handleDisconnect = useCallback(async () => {
    try { await client.setLiveInputs(false); } catch { /* ignore */ }
    try { await client.setLiveOutputs(false); } catch { /* ignore */ }
    await client.disconnect();
    setConnected(false);
    setDevices([]);
    setConfig(null);
    setLiveSlots(new Map());
    setAxisMax(new Map());
    setOutputs(null);
    setDirty(false);
    setVersion(null);
    setCapturing(null);
    trackingRef.current = null;
  }, [client]);

  // Event subscription
  useEffect(() => {
    return client.on((ev: ProtocolEvent) => {
      switch (ev.type) {
        case "device_attached": {
          setDevices((prev) => {
            const next = [...prev];
            const idx = next.findIndex((d) => d.slot === ev.slot);
            const slot: DeviceSlot = {
              slot: ev.slot,
              connected: true,
              vid: ev.vid,
              pid: ev.pid,
              axis_count: ev.axis_count,
              button_count: ev.button_count,
            };
            if (idx >= 0) next[idx] = slot;
            else next.push(slot);
            return next.sort((a, b) => a.slot - b.slot);
          });
          break;
        }
        case "device_detached": {
          setDevices((prev) =>
            prev.map((d) => (d.slot === ev.slot ? { ...d, connected: false } : d))
          );
          setLiveSlots((prev) => {
            const next = new Map(prev);
            next.delete(ev.slot);
            return next;
          });
          break;
        }
        case "live": {
          setLiveSlots((prev) => {
            const next = new Map(prev);
            next.set(ev.slot, { slot: ev.slot, buttons: ev.buttons, axes: ev.axes });
            return next;
          });
          // Grow observed counts so the UI knows how many to render.
          setDevices((prev) =>
            prev.map((d) => {
              if (d.slot !== ev.slot) return d;
              const ac = Math.max(d.axis_count, ev.axes.length);
              const highest = ev.buttons === 0 ? 0 : 32 - Math.clz32(ev.buttons);
              const bc = Math.max(d.button_count, highest);
              return ac === d.axis_count && bc === d.button_count ? d : { ...d, axis_count: ac, button_count: bc };
            })
          );
          // Track per-axis peak for bar auto-scale.
          setAxisMax((prev) => {
            let changed = false;
            const next = new Map(prev);
            for (let i = 0; i < ev.axes.length; i++) {
              const key = `${ev.slot}:${i}`;
              const curr = next.get(key) ?? 0;
              if (ev.axes[i] > curr) {
                next.set(key, ev.axes[i]);
                changed = true;
              }
            }
            return changed ? next : prev;
          });
          break;
        }
        case "outputs":
          setOutputs(ev.outputs);
          break;
        case "log":
          setLogs((prev) => {
            const next = [...prev, { ts: Date.now(), line: ev.line }];
            return next.length > 500 ? next.slice(next.length - 500) : next;
          });
          break;
      }
    });
  }, [client]);

  // ------------------ Capture loop ------------------
  // Baseline accumulation mutates baselineRef directly — no render churn.
  // Phase / deadline transitions are driven by setTimeout, so they fire
  // even when the device sits idle and emits no live events.

  useEffect(() => {
    if (!capturing) return;

    // Baseline → active timer
    const baselineMs = Math.max(0, capturing.baselineEnd - Date.now());
    const baselineTimer = setTimeout(() => {
      setCapturing((prev) => (prev && prev.phase === "baseline" ? { ...prev, phase: "active" } : prev));
    }, baselineMs);

    // Overall deadline timer — also commits whatever tracking captured
    // so a "press but never release" doesn't throw the calibration away.
    const deadlineMs = Math.max(0, capturing.deadline - Date.now());
    const deadlineTimer = setTimeout(() => {
      commitAxisCapture();
      setCapturing(null);
    }, deadlineMs);

    return () => {
      clearTimeout(baselineTimer);
      clearTimeout(deadlineTimer);
    };
    // commitAxisCapture is stable enough — its dependencies (client, setters)
    // don't change meaningfully between renders.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [capturing]);

  useEffect(() => {
    if (!capturing) return;

    // Phase 1 — learn the noise floor at rest. Mutate ref, no setState.
    if (capturing.phase === "baseline") {
      for (const [slot, live] of liveSlots) {
        const b = baselineRef.current.get(slot) ?? {
          buttons: live.buttons,
          axesMin: [...live.axes],
          axesMax: [...live.axes],
        };
        b.buttons |= live.buttons;
        for (let i = 0; i < live.axes.length; i++) {
          if (b.axesMin[i] === undefined) b.axesMin[i] = live.axes[i];
          if (b.axesMax[i] === undefined) b.axesMax[i] = live.axes[i];
          if (live.axes[i] < b.axesMin[i]) b.axesMin[i] = live.axes[i];
          if (live.axes[i] > b.axesMax[i]) b.axesMax[i] = live.axes[i];
        }
        baselineRef.current.set(slot, b);
      }
      return;
    }

    // Phase 3 — accumulate the peaks on the latched axis and wait for the
    // user to release back to baseline before committing the calibration.
    if (capturing.phase === "tracking") {
      const t = trackingRef.current;
      if (!t) return;
      const live = liveSlots.get(t.deviceSlot);
      if (!live) return;
      const v = live.axes[t.axisIdx];
      if (v === undefined) return;
      if (v > t.peakHigh) t.peakHigh = v;     // mutate ref — no render needed
      if (v < t.peakLow)  t.peakLow  = v;
      const returnBand = Math.max(t.noise * 2, 200);
      const deviation = Math.abs(v - t.baselineMid);
      // Require some real travel in either direction before treating
      // "near baseline" as a release.
      const travel = Math.max(
        t.peakHigh - t.baselineMid,
        t.baselineMid - t.peakLow,
      );
      const hasPressed = travel > t.triggerThreshold * 1.5;
      if (hasPressed && deviation <= returnBand) {
        commitAxisCapture();
        setCapturing(null);
      }
      return;
    }

    // Phase 2 — wait for an input that significantly exceeds the noise floor.
    for (const [slot, live] of liveSlots) {
      const dev = devices.find((d) => d.slot === slot);
      if (!dev || !dev.connected) continue;
      const b = baselineRef.current.get(slot);
      if (!b) continue;

      // New button bit (a bit that wasn't held during baseline)
      const newBits = (live.buttons | 0) & ~(b.buttons | 0);
      if (newBits !== 0) {
        let bit = 0;
        while ((newBits & (1 << bit)) === 0) bit++;
        void applyBinding(capturing.channel, capturing.bindingSlot, {
          ...NONE_BINDING,
          vid: dev.vid,
          pid: dev.pid,
          type: "button",
          index: bit,
        });
        setCapturing(null);
        return;
      }

      // Axis movement well outside baseline range — latch the axis and
      // switch to tracking phase so we can capture the full press range.
      for (let i = 0; i < live.axes.length; i++) {
        const lo = b.axesMin[i] ?? live.axes[i];
        const hi = b.axesMax[i] ?? live.axes[i];
        const mid = (lo + hi) / 2;
        const noise = hi - lo;
        const threshold = Math.max(noise * 5, 500);
        const deviation = Math.abs(live.axes[i] - mid);
        if (deviation <= threshold) continue;

        trackingRef.current = {
          channel: capturing.channel,
          bindingSlot: capturing.bindingSlot,
          deviceSlot: slot,
          axisIdx: i,
          vid: dev.vid,
          pid: dev.pid,
          baselineMid: mid,
          noise,
          peakHigh: live.axes[i],
          peakLow:  live.axes[i],
          triggerThreshold: threshold,
        };
        setCapturing((prev) => (prev ? { ...prev, phase: "tracking" } : prev));
        return;
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [liveSlots, capturing, devices]);

  // ------------------ Tracking commit ------------------
  // Build the binding from the data accumulated in trackingRef and push it.
  // Idempotent: clears trackingRef so a follow-up call is a no-op.

  function commitAxisCapture() {
    const t = trackingRef.current;
    if (!t) return;
    trackingRef.current = null;

    // Pick direction based on which side travelled further from baseline.
    // Ascending (rising on press): rawMin near baseline, rawMax at peak high.
    // Descending (falling on press): rawMin at peak low, rawMax near baseline,
    // and invert=true so the scaled output still ramps 0 → 65535 on press.
    const highDelta = t.peakHigh - t.baselineMid;
    const lowDelta  = t.baselineMid - t.peakLow;
    const ascending = highDelta >= lowDelta;
    let newRawMin: number;
    let newRawMax: number;
    let invert = false;
    if (ascending) {
      newRawMin = Math.max(0, Math.floor(t.baselineMid - t.noise));
      newRawMax = Math.max(t.peakHigh, newRawMin + 256);
    } else {
      newRawMin = Math.max(0, Math.floor(t.peakLow));
      newRawMax = Math.max(Math.floor(t.baselineMid + t.noise), newRawMin + 256);
      invert = true;
    }
    const range = newRawMax - newRawMin;
    const scaledNoise = range > 0 ? Math.floor((t.noise * 65535) / range) : 0;
    // 3× the scaled noise gives a comfortable dead-zone above the idle
    // jitter band without eating real motion.
    const deadzoneLow = Math.min(scaledNoise * 3, 5000);
    void applyBinding(t.channel, t.bindingSlot, {
      ...NONE_BINDING,
      vid: t.vid,
      pid: t.pid,
      type: "axis",
      index: t.axisIdx,
      rawMin: newRawMin,
      rawMax: newRawMax,
      deadzoneLow,
      invert,
    });
  }

  // ------------------ Mutation helpers ------------------

  function setConfigBinding(channel: ChannelKey, slot: number, b: InputBinding) {
    setConfig((prev) => {
      if (!prev) return prev;
      if (channel === "gear_N") return prev;
      const current = getChannelBindings(prev, channel);
      const next = current.slice();
      next[slot] = b;
      if (channel.startsWith("gear_")) {
        return { ...prev, gear: { ...prev.gear, [channel]: next } } as Config;
      }
      return { ...prev, [channel]: next } as Config;
    });
  }

  function setConfigGearDac(gear: GearKey, x: number, y: number) {
    setConfig((prev) => {
      if (!prev) return prev;
      return { ...prev, gearOut: { ...prev.gearOut, [gear]: { x, y } } };
    });
  }

  function setConfigPulseMs(v: number) {
    setConfig((prev) => (prev ? { ...prev, pulseMs: v } : prev));
  }

  async function applyBinding(channel: ChannelKey, slot: number, b: InputBinding) {
    try {
      await client.setBinding(channel, slot, b as unknown as Record<string, unknown>);
      setConfigBinding(channel, slot, b);
      setDirty(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function unbind(channel: ChannelKey, slot: number) {
    await applyBinding(channel, slot, { ...NONE_BINDING });
  }

  async function updateBindingField<K extends keyof InputBinding>(
    channel: ChannelKey,
    slot: number,
    key: K,
    value: InputBinding[K],
  ) {
    if (!config) return;
    const current = getBinding(config, channel, slot);
    const next: InputBinding = { ...current, [key]: value };
    await applyBinding(channel, slot, next);
  }

  async function pushGearDac(gear: GearKey, x: number, y: number) {
    setConfigGearDac(gear, x, y);
    setDirty(true);
    try { await client.setGearDac(gear, x, y); } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function pushPulseMs(v: number) {
    setConfigPulseMs(v);
    setDirty(true);
    try { await client.setPulseMs(v); } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  function beginCapture(channel: ChannelKey, bindingSlot: number) {
    // Seed the baseline from the current live snapshot so a fast first
    // sample is still useful even before phase 1's window has elapsed.
    const baseline = new Map<number, SlotBaseline>();
    for (const [slot, live] of liveSlots) {
      baseline.set(slot, {
        buttons: live.buttons,
        axesMin: [...live.axes],
        axesMax: [...live.axes],
      });
    }
    baselineRef.current = baseline;
    trackingRef.current = null;
    const now = Date.now();
    setCapturing({
      channel,
      bindingSlot,
      phase: "baseline",
      baselineEnd: now + 400,
      // Allow plenty of time for the full press + release cycle.
      deadline: now + 12000,
    });
  }

  function cancelCapture() {
    trackingRef.current = null;
    setCapturing(null);
  }

  async function handleSave() {
    try {
      setStatus("Saving…");
      await client.saveConfig();
      setDirty(false);
      setStatus("Saved");
      setTimeout(() => setStatus(""), 1500);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setStatus("");
    }
  }

  async function handleReset() {
    try {
      await client.resetConfig();
      const cfg = await client.getConfig();
      setConfig(cfg);
      setDirty(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  // Soft-reset the Teensy. The firmware drops USB immediately after acking,
  // so we tear our side down too — the user reconnects when ready.
  async function handleReboot() {
    try {
      setStatus("Rebooting Teensy…");
      await client.reboot();
    } catch {
      // The firmware vanishes mid-response; a timeout or disconnect here
      // is the expected happy path. Swallow it.
    }
    try { await client.disconnect(); } catch { /* ignore */ }
    setConnected(false);
    setDevices([]);
    setConfig(null);
    setLiveSlots(new Map());
    setAxisMax(new Map());
    setOutputs(null);
    setDirty(false);
    setVersion(null);
    setCapturing(null);
    trackingRef.current = null;
    setStatus("Teensy rebooted — click Connect when it re-enumerates");
    setTimeout(() => setStatus(""), 4000);
  }

  // ------------------ Render ------------------

  return (
    <div className="min-h-screen bg-background text-foreground">
      <Header
        connected={connected}
        version={version}
        dirty={dirty}
        status={status}
        onConnect={handleConnect}
        onDisconnect={handleDisconnect}
        onSave={handleSave}
        onReset={handleReset}
        onReboot={handleReboot}
        supported={supported}
      />

      <main className="container mx-auto max-w-5xl px-4 py-6">
        {error && (
          <Alert variant="destructive" className="mb-4">
            <AlertCircle className="h-4 w-4" />
            <AlertTitle>Error</AlertTitle>
            <AlertDescription className="flex items-center justify-between">
              <span>{error}</span>
              <Button size="sm" variant="ghost" onClick={() => setError(null)}>
                <X className="h-4 w-4" />
              </Button>
            </AlertDescription>
          </Alert>
        )}

        {!connected ? (
          <ConnectGate supported={supported} onConnect={handleConnect} />
        ) : !config ? (
          <div className="flex items-center justify-center gap-2 py-12 text-muted-foreground">
            <Loader2 className="h-4 w-4 animate-spin" /> Loading config…
          </div>
        ) : (
          <>
            {capturing && (
              <CaptureBanner
                channel={capturing.channel}
                phase={capturing.phase}
                onCancel={cancelCapture}
                deadline={capturing.deadline}
              />
            )}

            <Tabs value={tab} onValueChange={setTab}>
              <TabsList>
                <TabsTrigger value="devices">Devices ({devices.filter(d => d.connected).length})</TabsTrigger>
                <TabsTrigger value="mappings">Mappings</TabsTrigger>
                <TabsTrigger value="outputs">Outputs</TabsTrigger>
                <TabsTrigger value="logs">
                  Logs{logs.length > 0 ? ` (${logs.length})` : ""}
                </TabsTrigger>
              </TabsList>
              <TabsContent value="devices">
                <DevicesView devices={devices} liveSlots={liveSlots} axisMax={axisMax} />
              </TabsContent>
              <TabsContent value="mappings">
                <MappingsView
                  config={config}
                  devices={devices}
                  liveSlots={liveSlots}
                  axisMax={axisMax}
                  capturing={capturing}
                  onCapture={beginCapture}
                  onUnbind={unbind}
                  onUpdateField={updateBindingField}
                />
              </TabsContent>
              <TabsContent value="outputs">
                <OutputsView
                  config={config}
                  outputs={outputs}
                  onPushGearDac={pushGearDac}
                  onPushPulseMs={pushPulseMs}
                  onTestGear={(g) => client.testGear(g).catch((e) => setError(String(e)))}
                  onTestPulse={(d) => client.testPulse(d).catch((e) => setError(String(e)))}
                  onTestAxis={(c, v) => client.testAxis(c, v).catch((e) => setError(String(e)))}
                  onResetPedals={() => client.resetPedals().catch((e) => setError(String(e)))}
                />
              </TabsContent>
              <TabsContent value="logs">
                <LogsView logs={logs} onClear={() => setLogs([])} />
              </TabsContent>
            </Tabs>
          </>
        )}
      </main>
    </div>
  );
}

// ------------------ Header ------------------

function Header({
  connected, version, dirty, status, supported,
  onConnect, onDisconnect, onSave, onReset, onReboot,
}: {
  connected: boolean;
  version: VersionInfo | null;
  dirty: boolean;
  status: string;
  supported: boolean;
  onConnect: () => void;
  onDisconnect: () => void;
  onSave: () => void;
  onReset: () => void;
  onReboot: () => void;
}) {
  return (
    <header className="sticky top-0 z-10 border-b bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60">
      <div className="container mx-auto flex max-w-5xl items-center gap-3 px-4 py-3">
        <div className="flex flex-col">
          <span className="text-lg font-semibold leading-none">fanadapter</span>
          <span className="text-xs text-muted-foreground leading-none mt-1">config</span>
        </div>
        <div className="ml-2 flex items-center gap-2">
          {connected ? (
            <Badge variant="success" className="gap-1">
              <CheckCircle2 className="h-3 w-3" />
              {version ? `fw ${version.ver}` : "Connected"}
            </Badge>
          ) : (
            <Badge variant="secondary">Disconnected</Badge>
          )}
          {status && (
            <span className="text-xs text-muted-foreground flex items-center gap-1">
              {status === "Saved" ? (
                <CheckCircle2 className="h-3 w-3" />
              ) : (
                <Loader2 className="h-3 w-3 animate-spin" />
              )}
              {status}
            </span>
          )}
        </div>

        <div className="ml-auto flex items-center gap-2">
          {connected ? (
            <>
              <Button
                variant="outline"
                size="sm"
                onClick={onReset}
                title="Reset bindings in RAM (does not touch EEPROM until Save)"
              >
                <RotateCcw className="h-4 w-4" />
                Reset
              </Button>
              <Button size="sm" onClick={onSave} disabled={!dirty}>
                <Save className="h-4 w-4" />
                Save {dirty && <span className="ml-1 text-xs opacity-70">*</span>}
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={onReboot}
                title="Soft-reboot the Teensy. EEPROM is preserved; live USB host pool is reset."
              >
                <Power className="h-4 w-4" />
                Reboot
              </Button>
              <Button variant="outline" size="sm" onClick={onDisconnect}>
                <Unplug className="h-4 w-4" />
                Disconnect
              </Button>
            </>
          ) : (
            <Button size="sm" onClick={onConnect} disabled={!supported}>
              <Plug className="h-4 w-4" />
              Connect
            </Button>
          )}
        </div>
      </div>
    </header>
  );
}

// ------------------ Connect gate ------------------

function ConnectGate({ supported, onConnect }: { supported: boolean; onConnect: () => void }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Cable className="h-5 w-5" />
          Connect to a fanadapter
        </CardTitle>
        <CardDescription>
          Plug the Teensy into this computer via USB. WebSerial works in Chrome, Edge, and Brave on desktop.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!supported && (
          <Alert variant="destructive">
            <AlertCircle className="h-4 w-4" />
            <AlertTitle>WebSerial not supported</AlertTitle>
            <AlertDescription>
              This browser doesn't expose <code>navigator.serial</code>. Try Chrome, Edge, or Brave on
              desktop.
            </AlertDescription>
          </Alert>
        )}
        <Button size="lg" onClick={onConnect} disabled={!supported}>
          <Plug className="mr-2 h-4 w-4" />
          Connect
        </Button>
      </CardContent>
    </Card>
  );
}

// ------------------ Capture banner ------------------

function CaptureBanner({
  channel,
  phase,
  deadline,
  onCancel,
}: {
  channel: ChannelKey;
  phase: "baseline" | "active" | "tracking";
  deadline: number;
  onCancel: () => void;
}) {
  const [secs, setSecs] = useState(() => Math.max(0, Math.ceil((deadline - Date.now()) / 1000)));
  useEffect(() => {
    const id = setInterval(() => {
      setSecs(Math.max(0, Math.ceil((deadline - Date.now()) / 1000)));
    }, 250);
    return () => clearInterval(id);
  }, [deadline]);

  const ch = CHANNELS.find((c) => c.key === channel);
  const title =
    phase === "baseline"
      ? "Measuring noise floor — hold still…"
      : phase === "tracking"
      ? "Recording full range — press fully, then release…"
      : `Listening for input… ${secs}s`;
  const body =
    phase === "baseline" ? (
      <span>
        Sampling each axis at rest so we can ignore jitter. Don't touch anything for a moment.
      </span>
    ) : phase === "tracking" ? (
      <span>
        Push <strong>{ch?.label ?? channel}</strong> through its full travel, then let it return to
        neutral. Calibration commits the moment you release.
      </span>
    ) : (
      <span>
        Press a button or move an axis on the device you want bound to{" "}
        <strong>{ch?.label ?? channel}</strong>.
      </span>
    );
  return (
    <Alert className="mb-4 border-primary/50">
      <Mic className="h-4 w-4" />
      <AlertTitle>{title}</AlertTitle>
      <AlertDescription className="flex items-center justify-between gap-2">
        {body}
        <Button size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </AlertDescription>
    </Alert>
  );
}

// ------------------ Devices view ------------------

function DevicesView({
  devices,
  liveSlots,
  axisMax,
}: {
  devices: DeviceSlot[];
  liveSlots: Map<number, LiveSlot>;
  axisMax: Map<string, number>;
}) {
  if (devices.length === 0) {
    return (
      <Card>
        <CardContent className="pt-6 text-center text-muted-foreground">
          No HID devices detected yet. Plug something in.
        </CardContent>
      </Card>
    );
  }
  return (
    <div className="grid gap-3 sm:grid-cols-2">
      {devices.map((d) => (
        <DeviceCard key={d.slot} dev={d} live={liveSlots.get(d.slot)} axisMax={axisMax} />
      ))}
    </div>
  );
}

function DeviceCard({
  dev,
  live,
  axisMax,
}: {
  dev: DeviceSlot;
  live: LiveSlot | undefined;
  axisMax: Map<string, number>;
}) {
  const buttonCount = Math.max(dev.button_count, 0);
  const axisCount = Math.max(dev.axis_count, 0);

  return (
    <Card className={dev.connected ? "" : "opacity-50"}>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center justify-between text-base">
          <span>Slot {dev.slot}</span>
          {dev.connected ? (
            <Badge variant="success">connected</Badge>
          ) : (
            <Badge variant="secondary">offline</Badge>
          )}
        </CardTitle>
        <CardDescription className="font-mono text-xs">
          VID {hex(dev.vid)} · PID {hex(dev.pid)}
          {buttonCount > 0 && ` · ${buttonCount} btn`}
          {axisCount > 0 && ` · ${axisCount} ax`}
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {buttonCount === 0 && axisCount === 0 ? (
          <p className="text-xs text-muted-foreground">
            No input seen yet — press a button or move an axis to populate.
          </p>
        ) : null}

        {buttonCount > 0 && (
          <div>
            <Label className="text-xs text-muted-foreground">buttons</Label>
            <div className="mt-1.5 flex flex-wrap gap-1">
              {Array.from({ length: buttonCount }, (_, i) => {
                const on = !!(live && (live.buttons & (1 << i)));
                return (
                  <div
                    key={i}
                    className={
                      "flex h-6 min-w-[1.5rem] items-center justify-center rounded text-[10px] font-mono " +
                      (on ? "bg-primary text-primary-foreground" : "bg-secondary text-secondary-foreground")
                    }
                  >
                    {i}
                  </div>
                );
              })}
            </div>
          </div>
        )}

        {axisCount > 0 && (
          <div className="space-y-2">
            <Label className="text-xs text-muted-foreground">axes</Label>
            {Array.from({ length: axisCount }, (_, i) => {
              const v = live?.axes[i] ?? 0;
              const seen = axisMax.get(`${dev.slot}:${i}`);
              const max = effectiveAxisMax(seen);
              return (
                <div key={i} className="flex items-center gap-2">
                  <span className="w-10 font-mono text-xs text-muted-foreground">ax {i}</span>
                  <Bar value={v} max={max} />
                  <span className="w-20 text-right font-mono text-xs tabular-nums">
                    {v}
                    {seen && seen >= 512 && (
                      <span className="text-muted-foreground">/{seen}</span>
                    )}
                  </span>
                </div>
              );
            })}
          </div>
        )}
      </CardContent>
    </Card>
  );
}

// ------------------ Mappings view ------------------

function MappingsView({
  config,
  devices,
  liveSlots,
  axisMax,
  capturing,
  onCapture,
  onUnbind,
  onUpdateField,
}: {
  config: Config;
  devices: DeviceSlot[];
  liveSlots: Map<number, LiveSlot>;
  axisMax: Map<string, number>;
  capturing: CaptureState | null;
  onCapture: (c: ChannelKey, slot: number) => void;
  onUnbind: (c: ChannelKey, slot: number) => void;
  onUpdateField: <K extends keyof InputBinding>(
    c: ChannelKey, slot: number, k: K, v: InputBinding[K]
  ) => Promise<void>;
}) {
  const groups: { title: string; keys: ChannelKey[]; description?: string }[] = [
    {
      title: "H-Pattern Shifter",
      description: "Pick one input per gear (or several — they OR together). Multiple gears active simultaneously = neutral (defensive).",
      keys: ["gear_R", "gear_1", "gear_2", "gear_3", "gear_4", "gear_5", "gear_6", "gear_7"],
    },
    {
      title: "Sequential Shifter",
      description: "Rising edges trigger a pulse to the wheelbase's Shifter 2 port.",
      keys: ["shift_up", "shift_down"],
    },
    {
      title: "Handbrake",
      keys: ["handbrake"],
    },
    {
      title: "Pedals",
      keys: ["throttle", "brake", "clutch"],
    },
  ];

  return (
    <div className="space-y-4">
      {groups.map((g) => (
        <Card key={g.title}>
          <CardHeader>
            <CardTitle>{g.title}</CardTitle>
            {g.description && <CardDescription>{g.description}</CardDescription>}
          </CardHeader>
          <CardContent className="space-y-3">
            {g.keys.map((key, i) => (
              <div key={key}>
                {i > 0 && <Separator className="my-3" />}
                <ChannelEditor
                  channel={key}
                  bindings={getChannelBindings(config, key)}
                  devices={devices}
                  liveSlots={liveSlots}
                  axisMax={axisMax}
                  capturing={capturing}
                  onCapture={(slot) => onCapture(key, slot)}
                  onUnbind={(slot) => onUnbind(key, slot)}
                  onUpdateField={(slot, k, v) => onUpdateField(key, slot, k, v)}
                />
              </div>
            ))}
          </CardContent>
        </Card>
      ))}
    </div>
  );
}

function ChannelEditor({
  channel,
  bindings,
  devices,
  liveSlots,
  axisMax,
  capturing,
  onCapture,
  onUnbind,
  onUpdateField,
}: {
  channel: ChannelKey;
  bindings: ChannelBindings;
  devices: DeviceSlot[];
  liveSlots: Map<number, LiveSlot>;
  axisMax: Map<string, number>;
  capturing: CaptureState | null;
  onCapture: (slot: number) => void;
  onUnbind: (slot: number) => void;
  onUpdateField: <K extends keyof InputBinding>(
    slot: number, k: K, v: InputBinding[K]
  ) => Promise<void>;
}) {
  const ch = CHANNELS.find((c) => c.key === channel)!;
  const isAxisChannel = ch.preferred === "axis";

  // Which slots are occupied vs. empty. We show all populated slots plus
  // (when below the cap) a single "+ Add another input" button targeting
  // the first empty slot. Capturing into an empty slot uses that slot.
  const populated: number[] = [];
  for (let i = 0; i < bindings.length; i++) {
    if (bindings[i].type !== "none") populated.push(i);
  }
  // Always render at least slot 0 so the user has a Listen entry point.
  const visible = populated.length > 0 ? populated : [0];
  const emptySlot = firstEmptySlot(bindings);
  const canAdd = emptySlot >= 0 && populated.length > 0 && populated.length < MAX_BINDINGS_PER_CHANNEL;

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <span className="font-medium">{ch.label}</span>
        <span className="font-mono text-xs text-muted-foreground">{channel}</span>
        {populated.length > 1 && (
          <Badge variant="secondary" className="text-[10px]">
            {populated.length} inputs
          </Badge>
        )}
      </div>

      {visible.map((slot) => (
        <BindingSlotRow
          key={slot}
          channel={channel}
          slot={slot}
          binding={bindings[slot]}
          devices={devices}
          liveSlots={liveSlots}
          axisMax={axisMax}
          isAxisChannel={isAxisChannel}
          capturingThis={capturing?.channel === channel && capturing.bindingSlot === slot}
          showRemove={populated.length > 1 || bindings[slot].type !== "none"}
          onCapture={() => onCapture(slot)}
          onUnbind={() => onUnbind(slot)}
          onUpdateField={(k, v) => onUpdateField(slot, k, v)}
        />
      ))}

      {canAdd && (
        <Button
          size="sm"
          variant="ghost"
          className="text-xs text-muted-foreground"
          onClick={() => onCapture(emptySlot)}
        >
          <Mic className="h-3.5 w-3.5" />
          Add another input
        </Button>
      )}
    </div>
  );
}

function BindingSlotRow({
  binding,
  devices,
  liveSlots,
  axisMax,
  isAxisChannel,
  capturingThis,
  showRemove,
  onCapture,
  onUnbind,
  onUpdateField,
}: {
  channel: ChannelKey;
  slot: number;
  binding: InputBinding;
  devices: DeviceSlot[];
  liveSlots: Map<number, LiveSlot>;
  axisMax: Map<string, number>;
  isAxisChannel: boolean;
  capturingThis: boolean;
  showRemove: boolean;
  onCapture: () => void;
  onUnbind: () => void;
  onUpdateField: <K extends keyof InputBinding>(k: K, v: InputBinding[K]) => Promise<void>;
}) {
  return (
    <div className="space-y-3 rounded-md border bg-card/40 p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="mt-0.5 text-sm text-muted-foreground font-mono">
          {bindingSummary(binding)}
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <Button
            size="sm"
            variant={capturingThis ? "default" : "outline"}
            onClick={onCapture}
            disabled={capturingThis}
          >
            <Mic className="h-3.5 w-3.5" />
            {capturingThis ? "Listening…" : binding.type === "none" ? "Listen" : "Remap"}
          </Button>
          {showRemove && binding.type !== "none" && (
            <Button size="sm" variant="ghost" onClick={onUnbind} title="Remove this input">
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
          )}
        </div>
      </div>

      {binding.type === "axis" && (
        <AxisCalibration
          binding={binding}
          devices={devices}
          liveSlots={liveSlots}
          axisMax={axisMax}
          isAxisChannel={isAxisChannel}
          onUpdateField={onUpdateField}
        />
      )}
    </div>
  );
}

// ------------------ Axis calibration ------------------

function AxisCalibration({
  binding,
  devices,
  liveSlots,
  axisMax,
  isAxisChannel,
  onUpdateField,
}: {
  binding: InputBinding;
  devices: DeviceSlot[];
  liveSlots: Map<number, LiveSlot>;
  axisMax: Map<string, number>;
  isAxisChannel: boolean;
  onUpdateField: <K extends keyof InputBinding>(k: K, v: InputBinding[K]) => Promise<void>;
}) {
  // MAX raw across all slots whose VID/PID matches this binding (mirrors
  // the firmware's same-VID/PID aggregation).
  const liveRaw = useMemo(() => {
    let max = 0;
    let found = false;
    for (const dev of devices) {
      if (!dev.connected) continue;
      if (dev.vid !== binding.vid || dev.pid !== binding.pid) continue;
      const ls = liveSlots.get(dev.slot);
      const v = ls?.axes[binding.index];
      if (v === undefined) continue;
      if (!found || v > max) {
        max = v;
        found = true;
      }
    }
    return found ? max : 0;
  }, [liveSlots, devices, binding.vid, binding.pid, binding.index]);

  // Observed peak across same-VID/PID slots — used for the raw-bar scale.
  const observedPeak = useMemo(() => {
    let peak = 0;
    for (const dev of devices) {
      if (dev.vid !== binding.vid || dev.pid !== binding.pid) continue;
      const v = axisMax.get(`${dev.slot}:${binding.index}`) ?? 0;
      if (v > peak) peak = v;
    }
    return peak;
  }, [axisMax, devices, binding.vid, binding.pid, binding.index]);

  // The raw-bar scales to a sensible upper bound: the configured rawMax
  // if the user set one, otherwise the observed peak with headroom,
  // otherwise the 16-bit max as a last resort.
  const rawBarMax =
    binding.rawMax > 0
      ? binding.rawMax
      : effectiveAxisMax(observedPeak);

  const processed = scaleAxisJS(liveRaw, binding);

  const [captureBuf, setCaptureBuf] = useState<{ min: number; max: number; until: number } | null>(null);

  // End-of-recalibration timer (separate effect so it survives renders
  // where deps churn but the buffer hasn't actually moved).
  useEffect(() => {
    if (!captureBuf) return;
    const remain = Math.max(0, captureBuf.until - Date.now());
    const id = setTimeout(() => {
      setCaptureBuf((cur) => {
        if (!cur) return cur;
        void onUpdateField("rawMin", cur.min);
        void onUpdateField("rawMax", cur.max);
        return null;
      });
    }, remain);
    return () => clearTimeout(id);
  }, [captureBuf, onUpdateField]);

  // Track live min/max during the capture window. Return prev reference
  // when nothing changed so React doesn't re-render in a loop.
  useEffect(() => {
    if (!captureBuf) return;
    setCaptureBuf((prev) => {
      if (!prev) return prev;
      const newMin = Math.min(prev.min, liveRaw);
      const newMax = Math.max(prev.max, liveRaw);
      if (newMin === prev.min && newMax === prev.max) return prev;
      return { ...prev, min: newMin, max: newMax };
    });
    // captureBuf intentionally NOT in deps — we only want this to fire on
    // liveRaw changes; the setter handles the no-op case.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [liveRaw]);

  function startCaptureMinMax() {
    setCaptureBuf({ min: liveRaw, max: liveRaw, until: Date.now() + 4000 });
  }

  const invertId = `invert-${binding.vid}-${binding.pid}-${binding.index}`;

  return (
    <div className="rounded-md border bg-muted/30 p-3 space-y-3">
      <div className="space-y-2">
        <div className="flex items-center gap-2">
          <Label className="w-16 text-xs text-muted-foreground">raw</Label>
          <Bar value={liveRaw} max={rawBarMax} />
          <span className="w-24 text-right font-mono text-xs tabular-nums">
            {liveRaw}
            <span className="text-muted-foreground">/{rawBarMax}</span>
          </span>
        </div>
        <div className="flex items-center gap-2">
          <Label className="w-16 text-xs text-muted-foreground">{isAxisChannel ? "output" : "as button"}</Label>
          {isAxisChannel ? (
            <>
              <Bar value={processed} />
              <span className="w-24 text-right font-mono text-xs tabular-nums">{pct(processed)}</span>
            </>
          ) : (
            <Badge variant={processed >= binding.threshold ? "success" : "secondary"}>
              {processed >= binding.threshold ? "pressed" : "released"}
            </Badge>
          )}
        </div>
      </div>

      <Separator />

      <div className="space-y-2">
        <div className="flex items-center justify-between gap-2">
          <Label className="text-xs uppercase tracking-wide text-muted-foreground">
            Range — manual
          </Label>
          <span className="text-[10px] text-muted-foreground">
            type values directly to override
          </span>
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <NumberField
            label="rawMin"
            value={binding.rawMin}
            onChange={(v) => onUpdateField("rawMin", v)}
          />
          <NumberField
            label="rawMax"
            value={binding.rawMax}
            onChange={(v) => onUpdateField("rawMax", v)}
          />
        </div>
      </div>

      <div className="space-y-2">
        <Label className="text-xs uppercase tracking-wide text-muted-foreground">
          Range — auto
        </Label>
        <Button size="sm" variant="outline" onClick={startCaptureMinMax} disabled={!!captureBuf} className="w-full">
          {captureBuf
            ? `Sampling for 4 s… ${Math.ceil((captureBuf.until - Date.now()) / 1000)}s`
            : "Recalibrate from live (4 s)"}
        </Button>
        <p className="text-[10px] text-muted-foreground">
          Move the axis through its full range while sampling. The observed min and max
          replace the manual values above.
        </p>
      </div>

      <Separator />

      <div className="space-y-3">
        <SliderField
          label="Deadzone low"
          value={binding.deadzoneLow}
          onChange={(v) => onUpdateField("deadzoneLow", v)}
        />
        <SliderField
          label="Deadzone high"
          value={binding.deadzoneHigh}
          onChange={(v) => onUpdateField("deadzoneHigh", v)}
        />
        {!isAxisChannel && (
          <SliderField
            label="Threshold (button trigger)"
            value={binding.threshold}
            onChange={(v) => onUpdateField("threshold", v)}
          />
        )}
      </div>

      <div className="flex items-center justify-between">
        <Label htmlFor={invertId} className="text-sm">Invert</Label>
        <Switch
          id={invertId}
          checked={binding.invert}
          onCheckedChange={(v) => onUpdateField("invert", v)}
        />
      </div>
    </div>
  );
}

function NumberField({
  label,
  value,
  onChange,
  min = 0,
  max = 65535,
}: {
  label: string;
  value: number;
  onChange: (v: number) => void;
  min?: number;
  max?: number;
}) {
  const [local, setLocal] = useState(String(value));
  useEffect(() => setLocal(String(value)), [value]);
  return (
    <div className="space-y-1">
      <Label className="text-xs text-muted-foreground">{label}</Label>
      <Input
        type="number"
        value={local}
        min={min}
        max={max}
        onChange={(e) => setLocal(e.currentTarget.value)}
        onBlur={() => {
          const n = Math.max(min, Math.min(max, Number(local) | 0));
          if (n !== value) onChange(n);
          setLocal(String(n));
        }}
      />
    </div>
  );
}

function PulseMsField({
  value,
  onCommit,
}: {
  value: number;
  onCommit: (v: number) => void | Promise<void>;
}) {
  const [local, setLocal] = useState(String(value));
  useEffect(() => setLocal(String(value)), [value]);
  return (
    <Input
      id="pulse-ms"
      type="number"
      min={10}
      max={500}
      value={local}
      onChange={(e) => setLocal(e.currentTarget.value)}
      onBlur={() => {
        const n = Math.max(10, Math.min(500, Number(local) | 0));
        setLocal(String(n));
        if (n !== value) void onCommit(n);
      }}
      className="w-32"
    />
  );
}

function SliderField({
  label,
  value,
  onChange,
  max = 65535,
}: {
  label: string;
  value: number;
  onChange: (v: number) => void;
  max?: number;
}) {
  // Local state lets the bar / percentage update at drag rate while we
  // postpone the firmware roundtrip until the user releases the thumb.
  // A 64-step slider over a 65535 range would otherwise emit ~1000 set_binding
  // commands per full sweep.
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);
  return (
    <div className="space-y-1">
      <div className="flex items-center justify-between">
        <Label className="text-xs text-muted-foreground">{label}</Label>
        <span className="font-mono text-xs tabular-nums">{pct(local, max)}</span>
      </div>
      <Slider
        value={[local]}
        min={0}
        max={max}
        step={64}
        onValueChange={(v) => setLocal(v[0])}
        onValueCommit={(v) => {
          if (v[0] !== value) onChange(v[0]);
        }}
      />
    </div>
  );
}

// ------------------ Outputs view ------------------

// ------------------ Logs view ------------------
//
// Surfaces every non-JSON line the firmware emits over USB CDC Serial
// (anything sent via Serial.print/println in the sketch). Useful for
// watching the CSL Elite UART handshake state, gear transitions, and
// EEPROM load/save messages without disconnecting WebSerial to attach a
// separate terminal.

function LogsView({
  logs,
  onClear,
}: {
  logs: { ts: number; line: string }[];
  onClear: () => void;
}) {
  const endRef = useRef<HTMLDivElement | null>(null);
  // Auto-scroll to bottom when new lines arrive.
  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: "auto", block: "end" });
  }, [logs.length]);

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between text-base">
          <span>Firmware log</span>
          <div className="flex items-center gap-2">
            <span className="text-xs font-normal text-muted-foreground">
              {logs.length} / 500 lines
            </span>
            <Button size="sm" variant="ghost" onClick={onClear} disabled={logs.length === 0}>
              <Trash2 className="h-3.5 w-3.5" />
              Clear
            </Button>
          </div>
        </CardTitle>
        <CardDescription>
          Plain-text output from the firmware's USB Serial. JSON command responses
          and live-data events are filtered out — this is just the diagnostic prints.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {logs.length === 0 ? (
          <p className="py-8 text-center text-sm text-muted-foreground">
            No log lines yet. The firmware emits status messages on device attach/
            detach, gear/shift transitions, EEPROM load/save, and CSL Elite handshake.
          </p>
        ) : (
          <div className="max-h-[60vh] overflow-y-auto rounded-md border bg-muted/30 p-2 font-mono text-xs leading-relaxed">
            {logs.map((entry, i) => (
              <div key={i} className="whitespace-pre-wrap break-all">
                <span className="text-muted-foreground">
                  {new Date(entry.ts).toLocaleTimeString([], { hour12: false })}
                </span>
                {"  "}
                {entry.line}
              </div>
            ))}
            <div ref={endRef} />
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function OutputsView({
  config,
  outputs,
  onPushGearDac,
  onPushPulseMs,
  onTestGear,
  onTestPulse,
  onTestAxis,
  onResetPedals,
}: {
  config: Config;
  outputs: OutputsEvent | null;
  onPushGearDac: (g: GearKey, x: number, y: number) => void | Promise<void>;
  onPushPulseMs: (v: number) => void | Promise<void>;
  onTestGear: (g: GearKey) => void;
  onTestPulse: (d: "up" | "down") => void;
  onTestAxis: (channel: string, value: number) => void;
  onResetPedals: () => void;
}) {
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>Live outputs</CardTitle>
          <CardDescription>
            What the firmware is currently driving to the wheelbase.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-2 sm:grid-cols-2">
          <Field label="Gear" value={outputs?.gear ?? "—"} />
          <Field label="Sequential" value={outputs?.shift_up ? "↑ up" : outputs?.shift_down ? "↓ down" : "idle"} />
          <Field label="Handbrake" value={outputs ? pct(outputs.handbrake) : "—"} />
          <Field label="Throttle" value={outputs ? pct(outputs.throttle) : "—"} />
          <Field label="Brake" value={outputs ? pct(outputs.brake) : "—"} />
          <Field label="Clutch" value={outputs ? pct(outputs.clutch) : "—"} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Wheelbase pedal link</CardTitle>
          <CardDescription>
            CSL Elite UART handshake state lives on Serial3. After a Teensy soft
            reboot the wheelbase may stay in its prior streaming state and skip
            the next handshake — re-arming forces a fresh Step 0 / 250000 baud
            attempt without unplugging USB. Check the Logs tab for handshake
            progress.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <Button variant="outline" size="sm" onClick={onResetPedals}>
            <RotateCcw className="h-4 w-4" />
            Re-arm pedals handshake
          </Button>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>H-pattern DAC voltages</CardTitle>
          <CardDescription>
            12-bit DAC values per gear (0..4095 → 0..3.30 V after the RC filter).
            Tweak if the wheelbase's calibration drifts.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="grid grid-cols-[auto_1fr_1fr_auto] items-center gap-3 text-sm">
            <div className="font-medium text-muted-foreground">Gear</div>
            <div className="font-medium text-muted-foreground">X</div>
            <div className="font-medium text-muted-foreground">Y</div>
            <div></div>
            {GEAR_KEYS.map((g) => (
              <GearDacRow
                key={g}
                gear={g}
                dac={config.gearOut[g]}
                onUpdate={(x, y) => onPushGearDac(g, x, y)}
                onTest={() => onTestGear(g)}
              />
            ))}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Sequential</CardTitle>
        </CardHeader>
        <CardContent className="flex items-end gap-3">
          <div className="space-y-1">
            <Label className="text-xs text-muted-foreground">
              Pulse width (ms)
            </Label>
            <PulseMsField value={config.pulseMs} onCommit={onPushPulseMs} />
          </div>
          <Button variant="outline" onClick={() => onTestPulse("up")}>
            <Play className="h-4 w-4" /> Test up
          </Button>
          <Button variant="outline" onClick={() => onTestPulse("down")}>
            <Play className="h-4 w-4" /> Test down
          </Button>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Test axes</CardTitle>
          <CardDescription>
            Drive an output for 500 ms without an HID input — useful when verifying the wheelbase side.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-2">
          {(["handbrake", "throttle", "brake", "clutch"] as const).map((c) => (
            <div key={c} className="flex items-center gap-2">
              <span className="w-24 text-sm">{c}</span>
              <Button variant="outline" size="sm" onClick={() => onTestAxis(c, 0)}>
                0%
              </Button>
              <Button variant="outline" size="sm" onClick={() => onTestAxis(c, 32768)}>
                50%
              </Button>
              <Button variant="outline" size="sm" onClick={() => onTestAxis(c, 65535)}>
                100%
              </Button>
            </div>
          ))}
        </CardContent>
      </Card>
    </div>
  );
}

function GearDacRow({
  gear,
  dac,
  onUpdate,
  onTest,
}: {
  gear: GearKey;
  dac: { x: number; y: number };
  onUpdate: (x: number, y: number) => void | Promise<void>;
  onTest: () => void;
}) {
  const [x, setX] = useState(String(dac.x));
  const [y, setY] = useState(String(dac.y));
  useEffect(() => setX(String(dac.x)), [dac.x]);
  useEffect(() => setY(String(dac.y)), [dac.y]);
  return (
    <>
      <div className="font-mono">{gear.replace("gear_", "")}</div>
      <Input
        type="number"
        value={x}
        min={0}
        max={4095}
        onChange={(e) => setX(e.currentTarget.value)}
        onBlur={() => {
          const v = Math.max(0, Math.min(4095, Number(x) | 0));
          setX(String(v));
          if (v !== dac.x) void onUpdate(v, dac.y);
        }}
      />
      <Input
        type="number"
        value={y}
        min={0}
        max={4095}
        onChange={(e) => setY(e.currentTarget.value)}
        onBlur={() => {
          const v = Math.max(0, Math.min(4095, Number(y) | 0));
          setY(String(v));
          if (v !== dac.y) void onUpdate(dac.x, v);
        }}
      />
      <Button variant="outline" size="sm" onClick={onTest}>
        <Play className="h-3.5 w-3.5" />
      </Button>
    </>
  );
}

function Field({ label, value }: { label: string; value: string | number | undefined }) {
  return (
    <div className="flex items-baseline justify-between rounded-md border bg-muted/30 px-3 py-2">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span className="font-mono text-sm">{value ?? "—"}</span>
    </div>
  );
}
