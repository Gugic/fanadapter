// Mirrors the firmware Config schema (mapping.h). Field names match the
// JSON encoding emitted by the firmware's protocol.cpp.

export type InputType = "none" | "button" | "axis";

export interface InputBinding {
  vid: number;
  pid: number;
  type: InputType;
  index: number;
  threshold: number;     // 0..65535, post-scale
  rawMin: number;        // 0..65535
  rawMax: number;        // 0..65535
  deadzoneLow: number;   // 0..65535
  deadzoneHigh: number;  // 0..65535
  invert: boolean;
}

export interface GearDac {
  x: number;
  y: number;
}

export type GearKey =
  | "gear_R" | "gear_1" | "gear_2" | "gear_3" | "gear_4"
  | "gear_5" | "gear_6" | "gear_7" | "gear_N";

// Each channel carries up to MAX_BINDINGS_PER_CHANNEL bindings. Buttons OR
// together, axes MAX together. Empty slots have type === "none".
export const MAX_BINDINGS_PER_CHANNEL = 4;
export type ChannelBindings = InputBinding[]; // length === MAX_BINDINGS_PER_CHANNEL

export interface Config {
  version: number;
  pulseMs: number;
  max_bindings_per_channel?: number;
  gear: Record<Exclude<GearKey, "gear_N">, ChannelBindings>;
  gearOut: Record<GearKey, GearDac>;
  shift_up: ChannelBindings;
  shift_down: ChannelBindings;
  handbrake: ChannelBindings;
  throttle: ChannelBindings;
  brake: ChannelBindings;
  clutch: ChannelBindings;
}

export type ChannelKey =
  | GearKey
  | "shift_up" | "shift_down"
  | "handbrake" | "throttle" | "brake" | "clutch";

export interface DeviceSlot {
  slot: number;
  connected: boolean;
  vid: number;
  pid: number;
  axis_count: number;
  button_count: number;
}

export interface LiveSlot {
  slot: number;
  buttons: number;
  axes: number[];
}

export interface OutputsEvent {
  gear: GearKey;
  shift_up: boolean;
  shift_down: boolean;
  throttle: number;
  brake: number;
  clutch: number;
  handbrake: number;
}

export interface VersionInfo {
  fw: string;
  ver: string;
  protocol: number;
  max_bindings_per_channel?: number;
}

// ---------- Default binding (used when creating a new mapping) ----------

// rawMin === rawMax === 0 triggers the firmware's pass-through path in
// scaleAxis(), so a freshly-bound (uncalibrated) axis emits its raw value
// directly instead of being scaled against a wrong upper bound. The
// capture-on-bind flow replaces these zeros with real calibration values;
// manual edits that touch rawMin/rawMax disable pass-through deliberately.
export const NONE_BINDING: InputBinding = {
  vid: 0, pid: 0, type: "none", index: 0,
  threshold: 32768,
  rawMin: 0, rawMax: 0,
  deadzoneLow: 0, deadzoneHigh: 65535,
  invert: false,
};

// ---------- Channel metadata ----------

export interface ChannelInfo {
  key: ChannelKey;
  label: string;
  group: "shifter" | "sequential" | "handbrake" | "pedals";
  // Preferred input shape for the firmware semantics:
  //   - "button" → typical for gear positions, shift up/down
  //   - "axis"   → typical for handbrake, throttle, brake, clutch
  // Both directions still work (cross-type aware on the firmware), this
  // just hints to the UI which capture path is the natural one.
  preferred: "button" | "axis";
}

export const CHANNELS: ChannelInfo[] = [
  { key: "gear_R",     label: "Reverse",     group: "shifter",    preferred: "button" },
  { key: "gear_1",     label: "1st",         group: "shifter",    preferred: "button" },
  { key: "gear_2",     label: "2nd",         group: "shifter",    preferred: "button" },
  { key: "gear_3",     label: "3rd",         group: "shifter",    preferred: "button" },
  { key: "gear_4",     label: "4th",         group: "shifter",    preferred: "button" },
  { key: "gear_5",     label: "5th",         group: "shifter",    preferred: "button" },
  { key: "gear_6",     label: "6th",         group: "shifter",    preferred: "button" },
  { key: "gear_7",     label: "7th",         group: "shifter",    preferred: "button" },
  { key: "shift_up",   label: "Shift Up",    group: "sequential", preferred: "button" },
  { key: "shift_down", label: "Shift Down",  group: "sequential", preferred: "button" },
  { key: "handbrake",  label: "Handbrake",   group: "handbrake",  preferred: "axis" },
  { key: "throttle",   label: "Throttle",    group: "pedals",     preferred: "axis" },
  { key: "brake",      label: "Brake",       group: "pedals",     preferred: "axis" },
  { key: "clutch",     label: "Clutch",      group: "pedals",     preferred: "axis" },
];

export const GEAR_KEYS: GearKey[] = [
  "gear_R", "gear_1", "gear_2", "gear_3", "gear_4",
  "gear_5", "gear_6", "gear_7", "gear_N",
];

// Look up the binding array for a channel key in the Config. Always returns
// a length-MAX_BINDINGS_PER_CHANNEL array, padding with NONE_BINDING when
// the firmware sent a shorter list. Also accepts the legacy single-object
// shape (firmware v0.2.0) and wraps it as a 1-element array so the UI keeps
// rendering when the Teensy hasn't been reflashed yet.
export function getChannelBindings(config: Config, key: ChannelKey): ChannelBindings {
  if (key === "gear_N") return [];
  let raw: unknown;
  if (key.startsWith("gear_")) {
    raw = config.gear?.[key as Exclude<GearKey, "gear_N">];
  } else {
    raw = config[key as keyof Config];
  }
  let arr: InputBinding[];
  if (Array.isArray(raw)) {
    arr = raw as InputBinding[];
  } else if (raw && typeof raw === "object" && "type" in (raw as object)) {
    arr = [raw as InputBinding];
  } else {
    arr = [];
  }
  if (arr.length >= MAX_BINDINGS_PER_CHANNEL) return arr;
  const padded = arr.slice();
  while (padded.length < MAX_BINDINGS_PER_CHANNEL) padded.push({ ...NONE_BINDING });
  return padded;
}

// Convenience: primary (slot 0) binding for a channel. Returns NONE_BINDING
// for gear_N or when the channel has nothing bound yet.
export function getBinding(config: Config, key: ChannelKey, slot = 0): InputBinding {
  const arr = getChannelBindings(config, key);
  return arr[slot] ?? NONE_BINDING;
}

// First slot index whose binding is type === "none" (i.e. unused). Returns
// -1 if every slot is in use.
export function firstEmptySlot(arr: ChannelBindings): number {
  for (let i = 0; i < arr.length; i++) {
    if (arr[i].type === "none") return i;
  }
  return -1;
}
