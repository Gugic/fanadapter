// Mirror tests for the firmware's scaleAxis() in mapping.cpp. AGENTS.md flags
// the JS/C++ pair as a cross-file invariant — bumping these tests is the
// cheapest tripwire for accidental divergence. If you change the firmware
// math, update both sides AND the expectations below in the same commit.

import { describe, expect, it } from 'vitest'

import { evalButtonJS, scaleAxisJS } from './scaleAxis'
import { NONE_BINDING } from './types'
import type { InputBinding } from './types'

// Helper: NONE_BINDING with overrides, typed as InputBinding (button channel
// shape doesn't matter here — scaleAxis only consults the numeric fields).
function b(overrides: Partial<InputBinding>): InputBinding {
  return { ...NONE_BINDING, type: 'axis', ...overrides }
}

describe('scaleAxisJS', () => {
  it('passes raw through when rawMin and rawMax are both zero', () => {
    // The "unconfigured / pre-capture" state — value flows untouched aside
    // from the deadzones (which on NONE_BINDING are 0..65535 i.e. no-op for
    // mid-range values).
    expect(scaleAxisJS(0, b({}))).toBe(0)
    expect(scaleAxisJS(12345, b({}))).toBe(12345)
    expect(scaleAxisJS(65535, b({ deadzoneHigh: 65535 }))).toBe(65535)
  })

  it('clamps to 0 below rawMin and to 65535 at/above rawMax', () => {
    const binding = b({ rawMin: 100, rawMax: 4095 })
    expect(scaleAxisJS(50, binding)).toBe(0)
    expect(scaleAxisJS(100, binding)).toBe(0)
    expect(scaleAxisJS(4095, binding)).toBe(65535)
    expect(scaleAxisJS(9999, binding)).toBe(65535)
  })

  it('scales linearly between rawMin and rawMax (integer floor)', () => {
    const binding = b({ rawMin: 0, rawMax: 100 })
    // floor((50 - 0) * 65535 / 100) = floor(32767.5) = 32767
    expect(scaleAxisJS(50, binding)).toBe(32767)
    // floor((25 - 0) * 65535 / 100) = floor(16383.75) = 16383
    expect(scaleAxisJS(25, binding)).toBe(16383)
  })

  it('inverts after scaling when invert is set (resting-high pedal pattern)', () => {
    const ascending = b({ rawMin: 0, rawMax: 100 })
    const inverted = b({ rawMin: 0, rawMax: 100, invert: true })
    // Mid-range flips around the midpoint with a 1-LSB offset from the floor.
    expect(scaleAxisJS(50, ascending)).toBe(32767)
    expect(scaleAxisJS(50, inverted)).toBe(65535 - 32767) // 32768
    // Endpoints invert cleanly: rawMin → 65535, rawMax → 0.
    expect(scaleAxisJS(0, inverted)).toBe(65535)
    expect(scaleAxisJS(100, inverted)).toBe(0)
  })

  it('floors the post-scale value to 0 when at or below deadzoneLow', () => {
    const binding = b({ rawMin: 0, rawMax: 100, deadzoneLow: 10000 })
    // raw=10 → scaled 6553, which is below the 10000 deadzoneLow → 0.
    expect(scaleAxisJS(10, binding)).toBe(0)
    // raw=20 → scaled 13107, above deadzoneLow → passes through.
    expect(scaleAxisJS(20, binding)).toBe(13107)
  })

  it('saturates the post-scale value to 65535 when at or above deadzoneHigh', () => {
    const binding = b({ rawMin: 0, rawMax: 100, deadzoneHigh: 50000 })
    // raw=90 → scaled 58981, above the 50000 ceiling → saturates.
    expect(scaleAxisJS(90, binding)).toBe(65535)
    // raw=50 → scaled 32767, below the ceiling → passes through.
    expect(scaleAxisJS(50, binding)).toBe(32767)
  })

  it('applies deadzones AFTER invert (so a resting-high pedal still releases to 0)', () => {
    // Inverted axis with a deadzone band: physical "rest" (raw=100, the high
    // side) inverts to 0, which the deadzoneLow guard clamps to 0 cleanly —
    // matches the AGENTS.md description of the descending-capture path.
    const binding = b({ rawMin: 0, rawMax: 100, invert: true, deadzoneLow: 100 })
    expect(scaleAxisJS(100, binding)).toBe(0)
    // And full press (raw=0) inverts to 65535, deadzoneHigh saturates.
    expect(scaleAxisJS(0, binding)).toBe(65535)
  })
})

describe('evalButtonJS', () => {
  it('crosses the threshold based on the post-scale value', () => {
    const binding = b({ rawMin: 0, rawMax: 100, threshold: 10000 })
    // raw=10 → scaled 6553 — below threshold
    expect(evalButtonJS(10, binding)).toBe(false)
    // raw=50 → scaled 32767 — above threshold
    expect(evalButtonJS(50, binding)).toBe(true)
  })

  it('honours deadzones when deciding pressed state', () => {
    // deadzoneHigh saturates the value to 65535, which definitely beats any
    // threshold ≤ 65535. So a deadzoneHigh trip pulls the button down too.
    const binding = b({
      rawMin: 0,
      rawMax: 100,
      deadzoneHigh: 50000,
      threshold: 64000,
    })
    expect(evalButtonJS(90, binding)).toBe(true) // saturated → 65535 ≥ 64000
  })
})
