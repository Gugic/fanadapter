// WebSerial client for the fanadapter firmware.
//
// Handles the byte stream → JSON-line conversion, request/response queueing,
// and async event fan-out. The page subscribes to events via on() and sends
// commands via send().

import type { Config, DeviceSlot, OutputsEvent, VersionInfo } from './types'

// USB vendor IDs of every adapter platform variant we want to surface in
// the browser's port chooser. The native USB CDC port of the Teensy 4.1 is
// kept for backward compatibility with the original (v0.6.0 and earlier)
// build; everything else is a USB-to-serial bridge sitting on the ESP32-S3
// dev board's UART USB-C port. Adding a new VID here only widens the list
// the chooser offers — it doesn't change anything the firmware sees.
const ADAPTER_VIDS = [
  0x16c0, // Teensy 4.1 native USB CDC (legacy)
  0x1a86, // WCH CH340 / CH341 / CH343 (most common bridge on ESP32-S3 dev boards)
  0x10c4, // Silicon Labs CP2102 / CP2104
  0x0403, // FTDI FT232 (some boards)
  0x303a, // Espressif native USB Serial/JTAG (if anyone later does an OTG-device build)
] as const

export type ProtocolEvent =
  | { type: 'log'; line: string }
  | {
      type: 'device_attached'
      slot: number
      vid: number
      pid: number
      manufacturer?: string
      product?: string
      axis_count: number
      button_count: number
      has_hat?: boolean
      has_keyboard?: boolean
    }
  | { type: 'device_detached'; slot: number }
  | {
      type: 'live'
      slot: number
      buttons: number
      axes: number[]
      hat?: number | null
      keys?: number[]
    }
  | { type: 'outputs'; outputs: OutputsEvent }

export type EventListener = (event: ProtocolEvent) => void

interface PendingRequest {
  resolve: (msg: Record<string, unknown>) => void
  reject: (err: Error) => void
  timer: ReturnType<typeof setTimeout>
}

// Line-splitting transform applied to the decoded text stream.
const lineSplitter = () => {
  let buf = ''
  return new TransformStream<string, string>({
    transform(chunk, controller) {
      buf += chunk
      const lines = buf.split('\n')
      buf = lines.pop() ?? ''
      for (const line of lines) {
        const trimmed = line.replace(/\r$/, '').trim()
        if (trimmed.length > 0) controller.enqueue(trimmed)
      }
    },
    flush(controller) {
      const trimmed = buf.replace(/\r$/, '').trim()
      if (trimmed.length > 0) controller.enqueue(trimmed)
    },
  })
}

export class SerialClient {
  private port: SerialPort | null = null
  private writer: WritableStreamDefaultWriter<string> | null = null
  private reader: ReadableStreamDefaultReader<string> | null = null
  private readableClosed: Promise<void> | null = null
  private writableClosed: Promise<void> | null = null
  private listeners = new Set<EventListener>()
  private pending: PendingRequest[] = []

  isSupported(): boolean {
    return typeof navigator !== 'undefined' && 'serial' in navigator
  }

  isOpen(): boolean {
    return !!this.port && !!this.writer
  }

  on(listener: EventListener): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  private emit(event: ProtocolEvent) {
    for (const l of this.listeners) l(event)
  }

  async connect(): Promise<void> {
    if (!this.isSupported()) {
      throw new Error(
        'WebSerial is not supported in this browser. Use Chrome, Edge, or Brave on desktop.',
      )
    }
    const port = await navigator.serial.requestPort({
      filters: ADAPTER_VIDS.map((usbVendorId) => ({ usbVendorId })),
    })
    await port.open({ baudRate: 115200 })
    this.port = port

    // WebSerial spec: writable / readable are non-null once open() resolves,
    // but the types stay `WritableStream | null` / `ReadableStream | null`.
    // Capture once with an explicit guard so the rest of this method can use
    // them without non-null assertions.
    const writable = port.writable
    const readable = port.readable
    if (!writable || !readable) {
      throw new Error('serial port opened without readable/writable streams')
    }

    // Outgoing: encoder → port.writable
    const encoder = new TextEncoderStream()
    this.writableClosed = encoder.readable.pipeTo(writable).catch(() => {})
    this.writer = encoder.writable.getWriter()

    // Incoming: port.readable → decoder → line splitter → reader
    const decoder = new TextDecoderStream()
    this.readableClosed = readable.pipeTo(decoder.writable).catch(() => {})
    const lineStream = decoder.readable.pipeThrough(lineSplitter())
    this.reader = lineStream.getReader()

    this.readLoop().catch((err) => {
      console.error('read loop error', err)
    })
  }

  async disconnect(): Promise<void> {
    // Cancel reader → propagates to the line splitter and decoder.
    try {
      await this.reader?.cancel()
    } catch {
      /* ignore */
    }
    try {
      await this.writer?.close()
    } catch {
      /* ignore */
    }
    try {
      await this.readableClosed
    } catch {
      /* ignore */
    }
    try {
      await this.writableClosed
    } catch {
      /* ignore */
    }
    try {
      await this.port?.close()
    } catch {
      /* ignore */
    }
    this.reader = null
    this.writer = null
    this.port = null
    // Reject any pending requests
    for (const p of this.pending) {
      clearTimeout(p.timer)
      p.reject(new Error('disconnected'))
    }
    this.pending = []
  }

  private async readLoop(): Promise<void> {
    if (!this.reader) return
    // `for (;;)` avoids no-unnecessary-condition flagging the literal `true`.
    for (;;) {
      let chunk: ReadableStreamReadResult<string>
      try {
        chunk = await this.reader.read()
      } catch (err) {
        console.error('reader.read failed', err)
        break
      }
      if (chunk.done) break
      const line = chunk.value
      this.handleLine(line)
    }
  }

  private handleLine(line: string): void {
    if (!line.startsWith('{')) {
      this.emit({ type: 'log', line })
      return
    }
    let msg: Record<string, unknown>
    try {
      msg = JSON.parse(line) as Record<string, unknown>
    } catch {
      this.emit({ type: 'log', line })
      return
    }

    // Events have an `event` key. Otherwise, treat as a response to the
    // oldest pending request.
    if (typeof msg.event === 'string') {
      this.dispatchEvent(msg)
      return
    }

    const pend = this.pending.shift()
    if (pend) {
      clearTimeout(pend.timer)
      pend.resolve(msg)
    } else {
      // Unsolicited response (e.g. error from a malformed command we
      // didn't enqueue). Surface as a log.
      this.emit({ type: 'log', line })
    }
  }

  private dispatchEvent(msg: Record<string, unknown>): void {
    const ev = msg.event
    switch (ev) {
      case 'device_attached':
        this.emit({
          type: 'device_attached',
          slot: msg.slot as number,
          vid: msg.vid as number,
          pid: msg.pid as number,
          manufacturer: msg.manufacturer as string | undefined,
          product: msg.product as string | undefined,
          axis_count: msg.axis_count as number,
          button_count: msg.button_count as number,
          has_hat: msg.has_hat as boolean | undefined,
          has_keyboard: msg.has_keyboard as boolean | undefined,
        })
        break
      case 'device_detached':
        this.emit({ type: 'device_detached', slot: msg.slot as number })
        break
      case 'live': {
        // Wire shape (firmware ≥ 0.4.0): hat field omitted for devices
        // with no hat; -1 = released; 0..7 = direction.
        const rawHat = msg.hat
        let hat: number | null | undefined
        if (typeof rawHat !== 'number') {
          hat = undefined
        } else if (rawHat >= 0 && rawHat <= 7) {
          hat = rawHat
        } else {
          hat = null
        }
        // Wire shape (firmware ≥ 0.5.0): keys is a zero-padded array of
        // HID scancodes; field omitted for non-keyboard devices.
        const rawKeys = msg.keys
        const keys = Array.isArray(rawKeys) ? (rawKeys as number[]) : undefined
        this.emit({
          type: 'live',
          slot: msg.slot as number,
          buttons: msg.buttons as number,
          axes: Array.isArray(msg.axes) ? (msg.axes as number[]) : [],
          hat,
          keys,
        })
        break
      }
      case 'outputs':
        this.emit({ type: 'outputs', outputs: msg as unknown as OutputsEvent })
        break
      default:
        // unknown event — surface as log
        this.emit({ type: 'log', line: JSON.stringify(msg) })
    }
  }

  // Generic send. Returns the firmware's response.
  send<T extends Record<string, unknown> = Record<string, unknown>>(
    cmd: Record<string, unknown>,
    timeoutMs = 3000,
  ): Promise<T> {
    const writer = this.writer
    if (!writer) return Promise.reject(new Error('not connected'))
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        const idx = this.pending.findIndex((p) => p.timer === timer)
        if (idx >= 0) this.pending.splice(idx, 1)
        reject(new Error(`command timeout: ${String(cmd.cmd)}`))
      }, timeoutMs)
      this.pending.push({
        resolve: (msg) => resolve(msg as T),
        reject,
        timer,
      })
      writer.write(JSON.stringify(cmd) + '\n').catch((err: unknown) => {
        clearTimeout(timer)
        const idx = this.pending.findIndex((p) => p.timer === timer)
        if (idx >= 0) this.pending.splice(idx, 1)
        reject(err instanceof Error ? err : new Error(String(err)))
      })
    })
  }

  // ---------- Typed convenience wrappers ----------

  async version(): Promise<VersionInfo> {
    return this.send<VersionInfo & Record<string, unknown>>({ cmd: 'version' })
  }

  async listDevices(): Promise<DeviceSlot[]> {
    // `devices` is typed as optional because some firmware error paths return
    // an empty object — keep the `?? []` defensive fallback meaningful.
    const r = await this.send<{ devices?: DeviceSlot[] }>({ cmd: 'list_devices' })
    return r.devices ?? []
  }

  async getConfig(): Promise<Config> {
    return this.send<Config & Record<string, unknown>>({ cmd: 'get_config' }, 6000)
  }

  async setBinding(channel: string, slot: number, binding: Record<string, unknown>): Promise<void> {
    await this.send({ cmd: 'set_binding', channel, slot, binding })
  }

  async setGearDac(channel: string, x: number, y: number): Promise<void> {
    await this.send({ cmd: 'set_gear_dac', channel, x, y })
  }

  async setPulseMs(value: number): Promise<void> {
    await this.send({ cmd: 'set_pulse_ms', value })
  }

  async setGearMode(value: 'hold' | 'latch'): Promise<void> {
    await this.send({ cmd: 'set_gear_mode', value })
  }

  async saveConfig(): Promise<void> {
    await this.send({ cmd: 'save_config' }, 5000)
  }

  async resetConfig(): Promise<void> {
    await this.send({ cmd: 'reset_config' })
  }

  async setLiveInputs(on: boolean): Promise<void> {
    await this.send({ cmd: 'live_inputs', on })
  }

  async setLiveOutputs(on: boolean): Promise<void> {
    await this.send({ cmd: 'live_outputs', on })
  }

  async testAxis(channel: string, value: number): Promise<void> {
    await this.send({ cmd: 'test_axis', channel, value })
  }

  async testPulse(direction: 'up' | 'down'): Promise<void> {
    await this.send({ cmd: 'test_pulse', direction })
  }

  async testGear(channel: string): Promise<void> {
    await this.send({ cmd: 'test_gear', channel })
  }

  // Triggers a soft-reset of the adapter. The firmware sends `ok` and then
  // immediately drops USB, so the port disappears within ~100ms — callers
  // should disconnect() right after this resolves.
  async reboot(): Promise<void> {
    await this.send({ cmd: 'reboot' }, 1500)
  }

  // Re-arm the CSL Elite pedals UART handshake (back to Step 0 / 250000 baud).
  async resetPedals(): Promise<void> {
    await this.send({ cmd: 'reset_pedals' })
  }

  async pedalsStatus(): Promise<PedalsStatus> {
    return this.send<PedalsStatus & Record<string, unknown>>({ cmd: 'pedals_status' })
  }
}

export interface PedalsStatus {
  state: string
  throttle: number
  brake: number
  clutch: number
  handbrake: number
}

// ---------- WebSerial type stubs ----------
// Browsers that don't ship WebSerial types are handled at runtime via
// isSupported(); but TypeScript still needs a minimal declaration.
declare global {
  interface SerialPort {
    open(options: { baudRate: number }): Promise<void>
    close(): Promise<void>
    readable: ReadableStream<Uint8Array> | null
    writable: WritableStream<Uint8Array> | null
  }
  interface Serial {
    requestPort(options?: {
      filters?: { usbVendorId?: number; usbProductId?: number }[]
    }): Promise<SerialPort>
    getPorts(): Promise<SerialPort[]>
  }
  interface Navigator {
    serial: Serial
  }
}
