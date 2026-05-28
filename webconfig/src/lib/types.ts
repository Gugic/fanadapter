// Mirrors the firmware Config schema (mapping.h). Field names match the
// JSON encoding emitted by the firmware's protocol.cpp.

export type InputType = 'none' | 'button' | 'axis' | 'hat' | 'key'

// Hat direction labels indexed by the HID Usage Tables convention:
// 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW. A binding with type "hat"
// uses `index` to select which direction it matches.
export const HAT_DIRECTION_LABELS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const

// Pretty-print a HID Keyboard/Keypad scancode (USB HID Usage Tables §10).
// Returns "Key 0x<hex>" for codes we don't have a friendly name for.
export function keyName(scancode: number): string {
  if (scancode >= 0x04 && scancode <= 0x1d) return String.fromCharCode(0x41 + (scancode - 0x04)) // A..Z
  if (scancode >= 0x1e && scancode <= 0x26) return String.fromCharCode(0x31 + (scancode - 0x1e)) // 1..9
  if (scancode === 0x27) return '0'
  if (scancode >= 0x3a && scancode <= 0x45) return `F${scancode - 0x3a + 1}` // F1..F12
  if (scancode >= 0x59 && scancode <= 0x61) return `Kp${scancode - 0x59 + 1}` // Keypad 1..9
  if (scancode === 0x62) return 'Kp0'
  switch (scancode) {
    case 0x28:
      return 'Enter'
    case 0x29:
      return 'Esc'
    case 0x2a:
      return 'Backspace'
    case 0x2b:
      return 'Tab'
    case 0x2c:
      return 'Space'
    case 0x2d:
      return '-'
    case 0x2e:
      return '='
    case 0x2f:
      return '['
    case 0x30:
      return ']'
    case 0x31:
      return '\\'
    case 0x33:
      return ';'
    case 0x34:
      return "'"
    case 0x35:
      return '`'
    case 0x36:
      return ','
    case 0x37:
      return '.'
    case 0x38:
      return '/'
    case 0x39:
      return 'CapsLock'
    case 0x4f:
      return '→'
    case 0x50:
      return '←'
    case 0x51:
      return '↓'
    case 0x52:
      return '↑'
    case 0xe0:
      return 'LCtrl'
    case 0xe1:
      return 'LShift'
    case 0xe2:
      return 'LAlt'
    case 0xe3:
      return 'LGUI'
    case 0xe4:
      return 'RCtrl'
    case 0xe5:
      return 'RShift'
    case 0xe6:
      return 'RAlt'
    case 0xe7:
      return 'RGUI'
  }
  return `Key 0x${scancode.toString(16).toUpperCase().padStart(2, '0')}`
}

export interface InputBinding {
  vid: number
  pid: number
  type: InputType
  index: number
  threshold: number // 0..65535, post-scale
  rawMin: number // 0..65535
  rawMax: number // 0..65535
  deadzoneLow: number // 0..65535
  deadzoneHigh: number // 0..65535
  invert: boolean
}

export interface GearDac {
  x: number
  y: number
}

export type GearKey =
  | 'gear_R'
  | 'gear_1'
  | 'gear_2'
  | 'gear_3'
  | 'gear_4'
  | 'gear_5'
  | 'gear_6'
  | 'gear_7'
  | 'gear_N'

// Each channel carries up to MAX_BINDINGS_PER_CHANNEL bindings. Buttons OR
// together, axes MAX together. Empty slots have type === "none".
export const MAX_BINDINGS_PER_CHANNEL = 4
export type ChannelBindings = InputBinding[] // length === MAX_BINDINGS_PER_CHANNEL

// H-pattern shifter mode (firmware ≥ 0.6.0 / config v3):
//   "hold"  — gear engaged only while binding is active (real H-shifter).
//   "latch" — rising-edge switches gear, stays until next edge moves it
//             (keyboard / gamepad friendly).
export type GearMode = 'hold' | 'latch'

export interface Config {
  version: number
  pulseMs: number
  gearMode?: GearMode
  max_bindings_per_channel?: number
  gear: Record<GearKey, ChannelBindings>
  gearOut: Record<GearKey, GearDac>
  shift_up: ChannelBindings
  shift_down: ChannelBindings
  handbrake: ChannelBindings
  throttle: ChannelBindings
  brake: ChannelBindings
  clutch: ChannelBindings
}

export type ChannelKey =
  | GearKey
  | 'shift_up'
  | 'shift_down'
  | 'handbrake'
  | 'throttle'
  | 'brake'
  | 'clutch'

export interface DeviceSlot {
  slot: number
  connected: boolean
  vid: number
  pid: number
  manufacturer?: string // USB manufacturer string descriptor ("" if none)
  product?: string // USB product string descriptor ("" if none)
  axis_count: number
  button_count: number
  has_hat?: boolean
  has_keyboard?: boolean
}

export interface LiveSlot {
  slot: number
  buttons: number
  axes: number[]
  // Hat Switch / D-pad direction. 0..7 = active direction, null = released,
  // undefined = device has no hat reporting at all.
  hat?: number | null
  // Currently-pressed keyboard scancodes. Up to MAX_KEYS_PRESSED (6) entries;
  // empty slots are 0. undefined if device isn't a keyboard.
  keys?: number[]
}

export interface OutputsEvent {
  gear: GearKey
  shift_up: boolean
  shift_down: boolean
  throttle: number
  brake: number
  clutch: number
  handbrake: number
}

export interface VersionInfo {
  fw: string
  ver: string
  protocol: number
  max_bindings_per_channel?: number
}

// ---------- Default binding (used when creating a new mapping) ----------

// rawMin === rawMax === 0 triggers the firmware's pass-through path in
// scaleAxis(), so a freshly-bound (uncalibrated) axis emits its raw value
// directly instead of being scaled against a wrong upper bound. The
// capture-on-bind flow replaces these zeros with real calibration values;
// manual edits that touch rawMin/rawMax disable pass-through deliberately.
export const NONE_BINDING: InputBinding = {
  vid: 0,
  pid: 0,
  type: 'none',
  index: 0,
  threshold: 32768,
  rawMin: 0,
  rawMax: 0,
  deadzoneLow: 0,
  deadzoneHigh: 65535,
  invert: false,
}

// ---------- Channel metadata ----------

export interface ChannelInfo {
  key: ChannelKey
  label: string
  group: 'shifter' | 'sequential' | 'handbrake' | 'pedals'
  // Preferred input shape for the firmware semantics:
  //   - "button" → typical for gear positions, shift up/down
  //   - "axis"   → typical for handbrake, throttle, brake, clutch
  // Both directions still work (cross-type aware on the firmware), this
  // just hints to the UI which capture path is the natural one.
  preferred: 'button' | 'axis'
}

export const CHANNELS: ChannelInfo[] = [
  { key: 'gear_R', label: 'Reverse', group: 'shifter', preferred: 'button' },
  { key: 'gear_1', label: '1st', group: 'shifter', preferred: 'button' },
  { key: 'gear_2', label: '2nd', group: 'shifter', preferred: 'button' },
  { key: 'gear_3', label: '3rd', group: 'shifter', preferred: 'button' },
  { key: 'gear_4', label: '4th', group: 'shifter', preferred: 'button' },
  { key: 'gear_5', label: '5th', group: 'shifter', preferred: 'button' },
  { key: 'gear_6', label: '6th', group: 'shifter', preferred: 'button' },
  { key: 'gear_7', label: '7th', group: 'shifter', preferred: 'button' },
  { key: 'gear_N', label: 'Neutral', group: 'shifter', preferred: 'button' },
  { key: 'shift_up', label: 'Shift Up', group: 'sequential', preferred: 'button' },
  { key: 'shift_down', label: 'Shift Down', group: 'sequential', preferred: 'button' },
  { key: 'handbrake', label: 'Handbrake', group: 'handbrake', preferred: 'axis' },
  { key: 'throttle', label: 'Throttle', group: 'pedals', preferred: 'axis' },
  { key: 'brake', label: 'Brake', group: 'pedals', preferred: 'axis' },
  { key: 'clutch', label: 'Clutch', group: 'pedals', preferred: 'axis' },
]

export const GEAR_KEYS: GearKey[] = [
  'gear_R',
  'gear_1',
  'gear_2',
  'gear_3',
  'gear_4',
  'gear_5',
  'gear_6',
  'gear_7',
  'gear_N',
]

// Look up the binding array for a channel key in the Config. Always returns
// a length-MAX_BINDINGS_PER_CHANNEL array, padding with NONE_BINDING when
// the firmware sent a shorter list. Also accepts the legacy single-object
// shape (firmware v0.2.0) and wraps it as a 1-element array so the UI keeps
// rendering when the Teensy hasn't been reflashed yet.
export function getChannelBindings(config: Config, key: ChannelKey): ChannelBindings {
  let raw: unknown
  if (key.startsWith('gear_')) {
    raw = config.gear[key as GearKey]
  } else {
    raw = config[key as keyof Config]
  }
  let arr: InputBinding[]
  if (Array.isArray(raw)) {
    arr = raw as InputBinding[]
  } else if (raw && typeof raw === 'object' && 'type' in raw) {
    arr = [raw as InputBinding]
  } else {
    arr = []
  }
  if (arr.length >= MAX_BINDINGS_PER_CHANNEL) return arr
  const padded = arr.slice()
  while (padded.length < MAX_BINDINGS_PER_CHANNEL) padded.push({ ...NONE_BINDING })
  return padded
}

// Convenience: primary (slot 0) binding for a channel. Returns NONE_BINDING
// for gear_N or when the channel has nothing bound yet.
export function getBinding(config: Config, key: ChannelKey, slot = 0): InputBinding {
  const arr = getChannelBindings(config, key)
  return arr[slot] ?? NONE_BINDING
}

// First slot index whose binding is type === "none" (i.e. unused). Returns
// -1 if every slot is in use.
export function firstEmptySlot(arr: ChannelBindings): number {
  for (let i = 0; i < arr.length; i++) {
    if (arr[i]?.type === 'none') return i
  }
  return -1
}
