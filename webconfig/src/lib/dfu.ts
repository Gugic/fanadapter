// WebUSB DfuSe driver for the STM32 ROM bootloader (0483:df11).
//
// Scope: exactly one device — the STM32H7 system-memory bootloader that the firmware's
// {"cmd":"dfu"} reboot lands in. This is deliberately NOT a general DFU implementation: the ROM's
// fixed properties are relied on (DfuSe 1.1a command set per AN3156, wTransferSize = 1024, alt 0 =
// Internal Flash) instead of discovered from descriptors. The block-transfer pattern mirrors the
// proven webdfu approach: Set Address Pointer before every data block and always send wBlockNum = 2,
// so the write offset is exactly the address pointer and no block-counter arithmetic exists to get
// wrong.
//
// What "leave" means here: after the last data block, set the address pointer back to the app base
// and send a zero-length DNLOAD. The ROM enters dfuMANIFEST and jumps to the app — same behaviour
// as dfu-util's ":leave" suffix, which is how the hands-free flash cycle avoids a reset button.

// ---------- WebUSB type stubs ----------
// Same approach as serial.ts takes for WebSerial: the project doesn't pull in @types/w3c-web-usb,
// so declare the minimal surface we touch.
declare global {
  interface USBDevice {
    vendorId: number
    productId: number
    open(): Promise<void>
    close(): Promise<void>
    selectConfiguration(value: number): Promise<void>
    claimInterface(num: number): Promise<void>
    selectAlternateInterface(intf: number, alt: number): Promise<void>
    controlTransferIn(
      setup: USBControlTransferSetup,
      length: number,
    ): Promise<{ data?: DataView; status: string }>
    controlTransferOut(
      setup: USBControlTransferSetup,
      data?: BufferSource,
    ): Promise<{ bytesWritten: number; status: string }>
    configuration?: {
      interfaces: {
        interfaceNumber: number
        alternates: { alternateSetting: number; interfaceName?: string }[]
      }[]
    }
  }
  interface USBControlTransferSetup {
    requestType: 'standard' | 'class' | 'vendor'
    recipient: 'device' | 'interface' | 'endpoint' | 'other'
    request: number
    value: number
    index: number
  }
  interface USB {
    requestDevice(options: {
      filters: { vendorId?: number; productId?: number }[]
    }): Promise<USBDevice>
    getDevices(): Promise<USBDevice[]>
  }
  interface Navigator {
    usb: USB
  }
}

export const DFU_VID = 0x0483
export const DFU_PID = 0xdf11

/** Where the application lives; erase and write both start here. */
export const APP_BASE = 0x08000000

// DFU 1.1 bRequest values
const DFU_DNLOAD = 1
const DFU_GETSTATUS = 3
const DFU_CLRSTATUS = 4
const DFU_ABORT = 6

// DFU device states (the ones we branch on)
const STATE_DFU_IDLE = 2
const STATE_DNBUSY = 4
const STATE_DNLOAD_IDLE = 5
const STATE_MANIFEST = 7
const STATE_ERROR = 10

// DfuSe command bytes (DNLOAD with wValue = 0)
const CMD_SET_ADDRESS = 0x21
const CMD_ERASE = 0x41

// The ST ROM's fixed transfer size (AN3156). Reading it from the DFU functional descriptor would
// only re-discover this constant with extra failure modes.
const TRANSFER_SIZE = 1024

export interface FlashProgress {
  /** erase counts sectors; write counts bytes. */
  phase: 'erase' | 'write' | 'leave'
  done: number
  total: number
}

export function isWebUsbSupported(): boolean {
  return typeof navigator !== 'undefined' && 'usb' in navigator
}

/**
 * Prompt the user to pick the bootloader. Must be called from a user gesture (Chromium requires
 * transient activation for requestDevice) — which is why the picker lives on its own button.
 * Needed exactly ONCE per machine+origin: the grant persists, and every later update finds the
 * device silently via waitForDfuDevice().
 */
export async function requestDfuDevice(): Promise<USBDevice> {
  return navigator.usb.requestDevice({ filters: [{ vendorId: DFU_VID, productId: DFU_PID }] })
}

/**
 * Wait for a previously-granted bootloader to appear on the bus, polling getDevices(). Resolves
 * null on timeout — either the board didn't make it into the bootloader, or this machine has
 * never granted it (first update: fall back to requestDfuDevice from a fresh gesture).
 */
export async function waitForDfuDevice(timeoutMs: number): Promise<USBDevice | null> {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const devs = await navigator.usb.getDevices()
    const dfu = devs.find((d) => d.vendorId === DFU_VID && d.productId === DFU_PID)
    if (dfu) return dfu
    if (Date.now() >= deadline) return null
    await delay(500)
  }
}

// ---------- deployed-firmware manifest ----------

/** Written next to fanadapter-stm32.bin by the Pages deploy workflow. */
export interface FirmwareManifest {
  version: string
  commit: string
  builtAt: string
  size: number
}

/**
 * The GitHub Pages deploy builds the STM32 firmware and publishes it beside the app, so "latest"
 * is a same-origin fetch away. Returns null when absent — a dev server or a stale deploy — in
 * which case the dialog offers only the local-file path. The Vite dev server SPA-fallbacks
 * unknown paths to index.html with a 200, hence parse-failure also means null.
 */
export async function fetchFirmwareManifest(): Promise<FirmwareManifest | null> {
  try {
    const r = await fetch(`${import.meta.env.BASE_URL}firmware/fanadapter-stm32.json`, {
      cache: 'no-store',
    })
    if (!r.ok) return null
    const m = (await r.json()) as Partial<FirmwareManifest>
    return typeof m.version === 'string' && typeof m.size === 'number'
      ? (m as FirmwareManifest)
      : null
  } catch {
    return null
  }
}

export async function fetchFirmwareImage(): Promise<Uint8Array> {
  const r = await fetch(`${import.meta.env.BASE_URL}firmware/fanadapter-stm32.bin`, {
    cache: 'no-store',
  })
  if (!r.ok) throw new Error(`firmware download failed: HTTP ${r.status}`)
  return new Uint8Array(await r.arrayBuffer())
}

/**
 * PlatformIO appends a 16-byte DFU suffix to firmware.bin (for dfu-util's device matching); raw
 * objcopy output has none. The suffix is metadata, not code — strip it so it never lands in flash.
 * Layout (DFU 1.1 appendix B): ... ucDfuSignature = "UFD" at [len-8..len-6], bLength = 16 at
 * [len-5], dwCRC at [len-4..len-1].
 */
export function stripDfuSuffix(image: Uint8Array): Uint8Array {
  if (image.length < 16) return image
  const n = image.length
  if (image[n - 8] === 0x55 && image[n - 7] === 0x46 && image[n - 6] === 0x44) {
    const suffixLen = image[n - 5] ?? 0
    if (suffixLen >= 16 && suffixLen <= n) return image.subarray(0, n - suffixLen)
  }
  return image
}

/**
 * Cheap plausibility check before erasing anything: a Cortex-M image starts with the initial stack
 * pointer (must land in some RAM alias) and the reset vector (must land in flash, thumb bit set).
 * Catches "picked the wrong file" — not a substitute for picking the right build.
 */
export function looksLikeFirmware(image: Uint8Array): boolean {
  if (image.length < 8 || image.length > 0x100000) return false // > bank 1 would eat the config bank
  const dv = new DataView(image.buffer, image.byteOffset, image.byteLength)
  const sp = dv.getUint32(0, true)
  const reset = dv.getUint32(4, true)
  const spOk = [0x20000000, 0x24000000, 0x30000000, 0x38000000].some(
    (base) => sp >= base && sp <= base + 0x100000,
  )
  const resetOk = (reset & 0xff000000) === 0x08000000 && (reset & 1) === 1
  return spOk && resetOk
}

interface DfuStatus {
  status: number
  pollTimeout: number
  state: number
}

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

export class DfuseFlasher {
  private device: USBDevice

  /** 128 KB on every H743 sector; parsed from the interface string when present, so a different
   *  ROM (H750 etc.) still erases on its real boundaries. */
  private sectorSize = 128 * 1024

  constructor(device: USBDevice) {
    this.device = device
  }

  async open(): Promise<void> {
    await this.device.open()
    try {
      await this.device.selectConfiguration(1)
    } catch {
      /* already configured */
    }
    await this.device.claimInterface(0)
    await this.device.selectAlternateInterface(0, 0) // alt 0 = "@Internal Flash"
    this.parseSectorSize()
  }

  async close(): Promise<void> {
    try {
      await this.device.close()
    } catch {
      /* device usually vanishes after leave — expected */
    }
  }

  /** DfuSe alt-0 name: "@Internal Flash   /0x08000000/16*128Kg". Only the sector size is needed —
   *  the start is fixed at APP_BASE and the image length decides how many sectors to erase. */
  private parseSectorSize(): void {
    const name = this.device.configuration?.interfaces
      .find((i) => i.interfaceNumber === 0)
      ?.alternates.find((a) => a.alternateSetting === 0)?.interfaceName
    const m = name ? /\/0x[0-9a-fA-F]+\/\d+\*(\d+)([KM])/.exec(name) : null
    if (m?.[1]) this.sectorSize = parseInt(m[1], 10) * (m[2] === 'M' ? 1024 * 1024 : 1024)
  }

  async flash(imageIn: Uint8Array, onProgress: (p: FlashProgress) => void): Promise<void> {
    const image = stripDfuSuffix(imageIn)

    await this.ensureIdle()

    // Erase exactly the sectors the image covers. Everything above — including the config in
    // flash bank 2 — is untouched, which is what makes a firmware update non-destructive.
    const sectors: number[] = []
    for (let a = APP_BASE; a < APP_BASE + image.length; a += this.sectorSize) sectors.push(a)
    let erased = 0
    for (const sector of sectors) {
      onProgress({ phase: 'erase', done: erased++, total: sectors.length })
      await this.dfuseCommand(CMD_ERASE, sector)
    }
    onProgress({ phase: 'erase', done: sectors.length, total: sectors.length })

    // Write. Address pointer is re-set before every block (see header comment).
    for (let offset = 0; offset < image.length; offset += TRANSFER_SIZE) {
      onProgress({ phase: 'write', done: offset, total: image.length })
      const chunk = image.subarray(offset, Math.min(offset + TRANSFER_SIZE, image.length))
      await this.dfuseCommand(CMD_SET_ADDRESS, APP_BASE + offset)
      await this.download(2, chunk)
      await this.pollUntil(STATE_DNLOAD_IDLE)
    }
    onProgress({ phase: 'write', done: image.length, total: image.length })

    // Leave: point at the app, zero-length download, final GETSTATUS kicks the manifest. The ROM
    // jumps to the app and drops off the bus mid-transfer — errors past this point mean success.
    onProgress({ phase: 'leave', done: 0, total: 1 })
    await this.dfuseCommand(CMD_SET_ADDRESS, APP_BASE)
    try {
      await this.download(2, new Uint8Array(0))
      const st = await this.getStatus()
      if (st.state !== STATE_MANIFEST && st.status !== 0) {
        throw new Error(`unexpected manifest state ${st.state}/${st.status}`)
      }
    } catch {
      /* device already rebooted into the app */
    }
    onProgress({ phase: 'leave', done: 1, total: 1 })
  }

  // ---------- protocol plumbing ----------

  private async ensureIdle(): Promise<void> {
    let st = await this.getStatus()
    if (st.state === STATE_ERROR) {
      await this.control(DFU_CLRSTATUS, 0)
      st = await this.getStatus()
    }
    if (st.state !== STATE_DFU_IDLE) {
      await this.control(DFU_ABORT, 0)
      st = await this.getStatus()
    }
    if (st.state !== STATE_DFU_IDLE) {
      throw new Error(`bootloader stuck in DFU state ${st.state} (status ${st.status})`)
    }
  }

  /** DNLOAD with wValue = 0 is a DfuSe command block: opcode + little-endian address. */
  private async dfuseCommand(op: number, address: number): Promise<void> {
    const buf = new Uint8Array(5)
    buf[0] = op
    new DataView(buf.buffer).setUint32(1, address, true)
    await this.download(0, buf)
    // Commands report completion through GETSTATUS: dfuDNBUSY with a real bwPollTimeout (a 128K
    // sector erase runs seconds), then dfuDNLOAD_IDLE.
    await this.pollUntil(STATE_DNLOAD_IDLE)
  }

  private async download(blockNum: number, data: Uint8Array): Promise<void> {
    const r = await this.device.controlTransferOut(
      {
        requestType: 'class',
        recipient: 'interface',
        request: DFU_DNLOAD,
        value: blockNum,
        index: 0,
      },
      // Pass an ArrayBuffer-backed copy: controlTransferOut with a zero-length view is fine, but
      // subarray views carry offsets some Chromium versions mishandle.
      data.byteLength ? data.slice() : undefined,
    )
    if (r.status !== 'ok') throw new Error(`DNLOAD failed: ${r.status}`)
  }

  private async getStatus(): Promise<DfuStatus> {
    const r = await this.device.controlTransferIn(
      { requestType: 'class', recipient: 'interface', request: DFU_GETSTATUS, value: 0, index: 0 },
      6,
    )
    if (r.status !== 'ok' || !r.data || r.data.byteLength < 6) {
      throw new Error(`GETSTATUS failed: ${r.status}`)
    }
    return {
      status: r.data.getUint8(0),
      pollTimeout: r.data.getUint8(1) | (r.data.getUint8(2) << 8) | (r.data.getUint8(3) << 16),
      state: r.data.getUint8(4),
    }
  }

  private async pollUntil(wanted: number): Promise<void> {
    let st = await this.getStatus()
    while (st.state === STATE_DNBUSY) {
      await delay(st.pollTimeout || 5)
      st = await this.getStatus()
    }
    if (st.status !== 0) {
      throw new Error(`DFU error: status ${st.status}, state ${st.state}`)
    }
    if (st.state !== wanted) {
      throw new Error(`unexpected DFU state ${st.state} (wanted ${wanted})`)
    }
  }

  private async control(request: number, value: number): Promise<void> {
    const r = await this.device.controlTransferOut({
      requestType: 'class',
      recipient: 'interface',
      request,
      value,
      index: 0,
    })
    if (r.status !== 'ok') throw new Error(`DFU request ${request} failed: ${r.status}`)
  }
}
