import { afterEach, describe, expect, it, vi } from 'vitest'

import { FirmwareCommandError, SerialClient, SerialConnectionError } from './serial'

const connectedClients: SerialClient[] = []

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

afterEach(async () => {
  for (const client of connectedClients.splice(0)) await client.disconnect()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.useRealTimers()
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

function failConnectionAt(stage: 'requestPort' | 'getPorts' | 'open', error: unknown) {
  const adapter = {
    ...port(0x1209, 0xfa00),
    open: vi.fn().mockRejectedValue(error),
  } as SerialPort
  vi.stubGlobal('navigator', {
    serial: {
      requestPort:
        stage === 'requestPort'
          ? vi.fn().mockRejectedValue(error)
          : vi.fn().mockResolvedValue(adapter),
      getPorts:
        stage === 'getPorts'
          ? vi.fn().mockRejectedValue(error)
          : vi.fn().mockResolvedValue([adapter]),
    },
  })
  const client = new SerialClient()
  return stage === 'getPorts' ? client.connectGranted() : client.connect()
}

describe('expected serial connection failures', () => {
  it('classifies a port-open NetworkError and preserves its message and cause', async () => {
    const cause = new DOMException('Failed to open serial port.', 'NetworkError')
    const error = await failConnectionAt('open', cause).catch((failure: unknown) => failure)
    expect(error).toBeInstanceOf(SerialConnectionError)
    expect(error).toMatchObject({
      name: 'SerialConnectionError',
      reason: 'open-failed',
      message: cause.message,
      cause,
    })
  })

  it.each(['requestPort', 'getPorts', 'open'] as const)(
    'classifies browser permission failures from %s only',
    async (stage) => {
      for (const name of ['SecurityError', 'NotAllowedError']) {
        const cause = new DOMException('Permission denied by the browser.', name)
        const error = await failConnectionAt(stage, cause).catch((failure: unknown) => failure)
        expect(error).toBeInstanceOf(SerialConnectionError)
        expect(error).toMatchObject({ reason: 'permission-denied', message: cause.message, cause })
      }
    },
  )

  it.each(['requestPort', 'getPorts', 'open'] as const)(
    'preserves unexpected failures from %s without reclassification',
    async (stage) => {
      const errors = [
        new TypeError('Failed to open serial port.'),
        new DOMException('Invalid state.', 'InvalidStateError'),
        new DOMException('No port selected.', 'NotFoundError'),
        Object.assign(new Error('Failed to open serial port.'), { name: 'NetworkError' }),
      ]
      if (stage !== 'open')
        errors.push(new DOMException('Unexpected network error.', 'NetworkError'))
      for (const error of errors) {
        await expect(failConnectionAt(stage, error)).rejects.toBe(error)
      }
    },
  )

  it('does not classify permission-shaped errors outside the WebSerial calls', async () => {
    const error = new DOMException('Unexpected adapter inspection failure.', 'SecurityError')
    const adapter: SerialPort = {
      ...port(0x1209, 0xfa00),
      getInfo: () => {
        throw error
      },
    }
    vi.stubGlobal('navigator', { serial: { requestPort: vi.fn().mockResolvedValue(adapter) } })
    await expect(new SerialClient().connect()).rejects.toBe(error)
  })

  it('reports unsupported Connect with a typed error and preserves granted reconnect behavior', async () => {
    vi.stubGlobal('navigator', {})
    const client = new SerialClient()
    await expect(client.connect()).rejects.toMatchObject({
      name: 'SerialConnectionError',
      reason: 'unsupported',
      message: 'WebSerial is not supported in this browser. Use Chrome, Edge, or Brave on desktop.',
    })
    expect(await client.connectGranted()).toBe(false)
  })
})

async function connectStreams() {
  vi.useFakeTimers()
  const encoder = new TextEncoder()
  const decoder = new TextDecoder()
  const writes: string[] = []
  let incoming: ReadableStreamDefaultController<Uint8Array<ArrayBuffer>> | undefined
  const serialPort: SerialPort = {
    getInfo: () => ({ usbVendorId: 0x1209, usbProductId: 0xfa00 }),
    open: vi.fn().mockResolvedValue(undefined),
    setSignals: vi.fn().mockResolvedValue(undefined),
    close: vi.fn().mockResolvedValue(undefined),
    readable: new ReadableStream<Uint8Array<ArrayBuffer>>({
      start(controller) {
        incoming = controller
      },
    }),
    writable: new WritableStream<Uint8Array<ArrayBuffer>>({
      write(chunk) {
        writes.push(decoder.decode(chunk, { stream: true }))
      },
    }),
  }
  vi.stubGlobal('navigator', {
    serial: { requestPort: vi.fn().mockResolvedValue(serialPort) },
  })
  const client = new SerialClient()
  connectedClients.push(client)
  const connecting = client.connect()
  await vi.advanceTimersByTimeAsync(2000)
  await connecting
  return {
    client,
    writes,
    receive: (text: string) => {
      if (!incoming) throw new Error('Serial input stream is not open')
      incoming.enqueue(encoder.encode(text))
    },
  }
}

describe('firmware command replies', () => {
  it('captures the exact submitted JSON in a typed error despite later caller mutations', async () => {
    const { client, receive, writes } = await connectStreams()
    const command = {
      cmd: 'set_binding',
      channel: 'throttle',
      slot: 0,
      binding: { vid: 3767, pid: 6203, type: 'axis', index: 0, rawMin: 63, rawMax: 4095 },
      omitted: undefined,
    }
    const serialized = JSON.stringify(command)
    const failure = client.send(command).catch((error: unknown) => error)
    command.cmd = 'get_config'
    command.channel = 'brake'
    command.binding.index = 1
    command.binding.rawMin = 0
    await vi.advanceTimersByTimeAsync(0)
    expect(writes.join('')).toBe(serialized + '\n')

    receive('{"err":"bad_json"}\n')
    const error = await failure
    expect(error).toBeInstanceOf(FirmwareCommandError)
    if (!(error instanceof FirmwareCommandError)) throw new Error('Expected firmware error')
    expect(error.name).toBe('FirmwareCommandError')
    expect(error.message).toBe('Firmware error: bad_json')
    expect(error.code).toBe('bad_json')
    expect(error.command).toEqual(JSON.parse(serialized))
    expect(error.command).not.toHaveProperty('omitted')
    expect(error.command).not.toBe(command)
    expect(error.command.binding).not.toBe(command.binding)
    command.binding.rawMax = 1234
    expect(error.command).toEqual(JSON.parse(serialized))
    expect(vi.getTimerCount()).toBe(0)
  })

  it('rejects a failed binding while preserving events and the next queued reply', async () => {
    const { client, receive, writes } = await connectStreams()
    const onEvent = vi.fn()
    client.on(onEvent)
    const binding = {
      vid: 3767,
      pid: 6203,
      type: 'axis',
      index: 0,
      threshold: 32768,
      rawMin: 63,
      rawMax: 4095,
      deadzoneLow: 0,
      deadzoneHigh: 65535,
      invert: false,
    }
    const rejected = expect(client.setBinding('throttle', 0, binding)).rejects.toThrow(
      'Firmware error: bad_json',
    )
    const version = client.version()
    await vi.advanceTimersByTimeAsync(0)
    expect(writes.join('')).toBe(
      JSON.stringify({ cmd: 'set_binding', channel: 'throttle', slot: 0, binding }) +
        '\n{"cmd":"version"}\n',
    )

    receive('{"event":"live","slot":0,"buttons":0,"axes":[2048]}\r\n{"err":"bad_')
    receive('json"}\r\n')
    await rejected
    expect(onEvent).toHaveBeenCalledExactlyOnceWith({
      type: 'live',
      slot: 0,
      buttons: 0,
      axes: [2048],
      hat: undefined,
      keys: undefined,
    })
    expect(vi.getTimerCount()).toBe(1)

    const reply = { fw: 'fanadapter-stm32', ver: '0.6.3', protocol: 6 }
    receive(JSON.stringify(reply) + '\n')
    await expect(version).resolves.toEqual(reply)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('rejects a failed save and accepts a successful retry on the same session', async () => {
    const { client, receive } = await connectStreams()
    const rejected = expect(client.saveConfig()).rejects.toThrow(
      'Firmware error: flash_save_failed',
    )
    receive('{"err":"flash_save_failed"}\n')
    await rejected
    expect(vi.getTimerCount()).toBe(0)

    const retry = client.saveConfig()
    receive('{"ok":true}\n')
    await expect(retry).resolves.toBeUndefined()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('keeps unsolicited errors as logs and event errors out of the reply queue', async () => {
    const { client, receive } = await connectStreams()
    const onEvent = vi.fn()
    client.on(onEvent)
    receive('{"err":"unknown_cmd"}\n')
    await vi.advanceTimersByTimeAsync(0)
    expect(onEvent).toHaveBeenCalledExactlyOnceWith({
      type: 'log',
      line: '{"err":"unknown_cmd"}',
    })

    const response = client.send({ cmd: 'live_inputs', on: true })
    receive('{"event":"diagnostic","err":"device_notice"}\n')
    await vi.advanceTimersByTimeAsync(0)
    expect(onEvent).toHaveBeenLastCalledWith({
      type: 'log',
      line: '{"event":"diagnostic","err":"device_notice"}',
    })
    expect(vi.getTimerCount()).toBe(1)
    receive('{"ok":true}\n')
    await expect(response).resolves.toEqual({ ok: true })
    expect(vi.getTimerCount()).toBe(0)
  })
})
