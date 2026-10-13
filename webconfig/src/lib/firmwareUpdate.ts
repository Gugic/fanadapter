import type { VersionInfo } from './types'

export type FirmwareReconnectResult =
  | { connected: true; version: VersionInfo }
  | { connected: false; error: string | null }

/** Flash success and serial confirmation are separate outcomes. A fresh board has no serial grant. */
export async function completeFirmwareUpdate({
  flash,
  onFlashed,
  reconnect,
  timeoutMs = 15000,
}: {
  flash: () => Promise<void>
  onFlashed: () => void
  reconnect: () => Promise<VersionInfo | null>
  timeoutMs?: number
}): Promise<FirmwareReconnectResult> {
  await flash()
  onFlashed()

  let error: string | null = null
  const deadline = Date.now() + timeoutMs
  for (;;) {
    try {
      const version = await reconnect()
      if (version) return { connected: true, version }
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
    if (Date.now() >= deadline) return { connected: false, error }
    await new Promise((resolve) => setTimeout(resolve, 1000))
  }
}
