import { afterEach, describe, expect, it, vi } from 'vitest'
import { completeFirmwareUpdate } from './firmwareUpdate'
import type { VersionInfo } from './types'

const version: VersionInfo = { fw: 'fanadapter-stm32', ver: '0.6.1', protocol: 6 }

afterEach(() => vi.useRealTimers())

describe('firmware installation and serial confirmation', () => {
  it('keeps a first installation successful when there is no serial grant', async () => {
    vi.useFakeTimers()
    const flash = vi.fn().mockResolvedValue(undefined)
    const onFlashed = vi.fn()
    const reconnect = vi.fn().mockResolvedValue(null)
    const outcome = completeFirmwareUpdate({ flash, onFlashed, reconnect, timeoutMs: 3000 })

    await vi.advanceTimersByTimeAsync(0)
    expect(onFlashed).toHaveBeenCalledOnce() // installation finishes before serial confirmation
    await vi.advanceTimersByTimeAsync(3000)

    expect(await outcome).toEqual({ connected: false, error: null })
    expect(flash).toHaveBeenCalledOnce()
  })

  it('automatically confirms an existing grant once the board re-enumerates', async () => {
    vi.useFakeTimers()
    const flash = vi.fn().mockResolvedValue(undefined)
    const reconnect = vi.fn().mockResolvedValueOnce(null).mockResolvedValue(version)
    const outcome = completeFirmwareUpdate({ flash, onFlashed: vi.fn(), reconnect })

    await vi.advanceTimersByTimeAsync(1000)

    expect(await outcome).toEqual({ connected: true, version })
    expect(flash).toHaveBeenCalledOnce()
  })

  it('does not claim an installation or try serial when the flash fails', async () => {
    const error = new Error('USB write failed')
    const flash = vi.fn().mockRejectedValue(error)
    const onFlashed = vi.fn()
    const reconnect = vi.fn()

    await expect(completeFirmwareUpdate({ flash, onFlashed, reconnect })).rejects.toBe(error)

    expect(onFlashed).not.toHaveBeenCalled()
    expect(reconnect).not.toHaveBeenCalled()
  })

  it('keeps installation success when serial bring-up fails', async () => {
    vi.useFakeTimers()
    const flash = vi.fn().mockResolvedValue(undefined)
    const onFlashed = vi.fn()
    const reconnect = vi.fn().mockRejectedValue(new Error('port is occupied'))
    const outcome = completeFirmwareUpdate({ flash, onFlashed, reconnect })

    await vi.advanceTimersByTimeAsync(15000)

    expect(await outcome).toEqual({ connected: false, error: 'port is occupied' })
    expect(onFlashed).toHaveBeenCalledOnce()
    expect(flash).toHaveBeenCalledOnce()
  })

  it('recovers transient serial errors without reflashing', async () => {
    vi.useFakeTimers()
    const flash = vi.fn().mockResolvedValue(undefined)
    const reconnect = vi
      .fn()
      .mockRejectedValueOnce(new Error('not ready'))
      .mockResolvedValue(version)
    const outcome = completeFirmwareUpdate({ flash, onFlashed: vi.fn(), reconnect })

    await vi.advanceTimersByTimeAsync(1000)

    expect(await outcome).toEqual({ connected: true, version })
    expect(flash).toHaveBeenCalledOnce()
  })
})
