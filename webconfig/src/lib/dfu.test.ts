import { afterEach, describe, expect, it, vi } from 'vitest'
import { fetchFirmwareImage, fetchFirmwareManifest, type FirmwareManifest } from './dfu'

const bytes = new Uint8Array([0, 0, 1, 36, 1, 1, 0, 8])
async function manifest(): Promise<FirmwareManifest> {
  const sha = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))
  return {
    version: '0.6.1',
    commit: 'abc',
    builtAt: '',
    size: bytes.length,
    sha256: Array.from(sha)
      .map((b) => b.toString(16).padStart(2, '0'))
      .join(''),
    releaseUrl: 'https://github.com/Gugic/fanadapter/releases/tag/firmware-v0.6.1',
    downloadUrl:
      'https://github.com/Gugic/fanadapter/releases/download/firmware-v0.6.1/fanadapter-stm32.bin',
  }
}
afterEach(() => vi.unstubAllGlobals())

describe('published firmware integrity', () => {
  it('accepts the exact bytes described by the selected release', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(bytes)))
    expect(await fetchFirmwareImage(await manifest())).toEqual(bytes)
  })
  it('rejects changed or corrupt images before flashing', async () => {
    const selected = await manifest()
    const changed = bytes.slice()
    changed[0] = 1
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(changed)))
    await expect(fetchFirmwareImage(selected)).rejects.toThrow('corrupt')
  })
  it('rejects truncated images', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(bytes.slice(0, 7))))
    await expect(fetchFirmwareImage(await manifest())).rejects.toThrow('corrupt')
  })
  it('treats manifests without a SHA-256 as unavailable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ version: '0.6.1', size: 8 })))
    expect(await fetchFirmwareManifest()).toBeNull()
  })
})
