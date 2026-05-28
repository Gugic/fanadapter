// Client-side mirror of firmware's scaleAxis() in mapping.cpp.
// Used to show a live "processed value" preview while the user tweaks the
// rawMin / rawMax / deadzone / invert sliders. The firmware applies the
// real value; this is just a visual aid.

import type { InputBinding } from './types'

export function scaleAxisJS(raw: number, b: InputBinding): number {
  let scaled: number
  if (b.rawMin === 0 && b.rawMax === 0) {
    scaled = raw
  } else if (raw <= b.rawMin) {
    scaled = 0
  } else if (raw >= b.rawMax) {
    scaled = 65535
  } else {
    scaled = Math.floor(((raw - b.rawMin) * 65535) / (b.rawMax - b.rawMin))
  }
  if (b.invert) scaled = 65535 - scaled
  if (scaled <= b.deadzoneLow) return 0
  if (scaled >= b.deadzoneHigh) return 65535
  return scaled
}

// Evaluator used to detect "input changed enough to be a binding candidate"
// during capture. Returns true if the post-scale value crosses the threshold.
export function evalButtonJS(raw: number, b: InputBinding): boolean {
  return scaleAxisJS(raw, b) >= b.threshold
}
