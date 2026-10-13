import { afterEach, describe, expect, it, vi } from 'vitest'

import { SerialClient } from './serial'

function port(usbVendorId?: number, usbProductId?: number): SerialPort {
  return { getInfo: () => ({ usbVendorId, usbProductId }) } as SerialPort
}

function setup(ports: SerialPort[] = []) {
  const requestPort = vi.fn()
  const getPorts = vi.fn().mockResolvedValue(ports)
  vi.stubGlobal('navigator', { serial: { requestPort, getPorts } })
  const client = new SerialClient()
  // Exercise port selection without creating streams or opening a physical device.
  const openPort = vi
    .spyOn(client as unknown as { openPort(port: SerialPort): Promise<void> }, 'openPort')
    .mockResolvedValue(undefined)
  return { client, requestPort, openPort }
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Fanadapter port selection', () => {
  it('filters the browser picker to the exact native USB identity', async () => {
    const adapter = port(0x1209, 0xfa00)
    const { client, requestPort, openPort } = setup()
    requestPort.mockResolvedValue(adapter)

    await client.connect()

    expect(requestPort).toHaveBeenCalledWith({
      filters: [{ usbVendorId: 0x1209, usbProductId: 0xfa00 }],
    })
    expect(openPort).toHaveBeenCalledWith(adapter)
  })

  it('rejects an unexpected picker result before opening it', async () => {
    const { client, requestPort, openPort } = setup()
    requestPort.mockResolvedValue(port(0x1a86, 0x7522))

    await expect(client.connect()).rejects.toThrow('not a Fanadapter')
    expect(openPort).not.toHaveBeenCalled()
  })

  it.each([
    ['UART bridge', 0x1a86, 0x7522],
    ['another product from the same vendor', 0x1209, 0x0001],
    ['another vendor', 0x0403, 0xfa00],
    ['unknown identity', undefined, undefined],
  ])('never reconnects to a sole granted %s', async (_name, vid, pid) => {
    const { client, openPort } = setup([port(vid, pid)])

    expect(await client.connectGranted()).toBe(false)
    expect(openPort).not.toHaveBeenCalled()
  })

  it('finds a granted adapter among unrelated devices without relying on its label', async () => {
    const adapter = port(0x1209, 0xfa00)
    const { client, openPort } = setup([port(0x1a86, 0x7522), adapter, port()])

    expect(await client.connectGranted()).toBe(true)
    expect(openPort).toHaveBeenCalledExactlyOnceWith(adapter)
  })

  it('waits for the adapter when no ports are granted', async () => {
    const { client, openPort } = setup()

    expect(await client.connectGranted()).toBe(false)
    expect(openPort).not.toHaveBeenCalled()
  })
})
