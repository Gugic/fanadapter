// A browser-boundary fixture: the production SerialClient still encodes, queues,
// decodes and dispatches real streams. No physical device is opened by these tests.
export async function installAdapter(page) {
  await page.addInitScript(() => {
    const gears = [
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
    const none = {
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
    const empty = () => Array.from({ length: 4 }, () => ({ ...none }))
    const config = {
      version: 3,
      pulseMs: 50,
      gearMode: 'hold',
      max_bindings_per_channel: 4,
      gear: Object.fromEntries(gears.map((gear) => [gear, empty()])),
      gearOut: Object.fromEntries(gears.map((gear) => [gear, { x: 2048, y: 2048 }])),
      shift_up: empty(),
      shift_down: empty(),
      handbrake: empty(),
      throttle: [
        { ...none, vid: 0x1234, pid: 1, type: 'axis', rawMax: 65535 },
        ...empty().slice(1),
      ],
      brake: empty(),
      clutch: empty(),
    }
    const devices = [
      {
        slot: 0,
        connected: true,
        vid: 0x1234,
        pid: 1,
        manufacturer: 'Test',
        product: 'Regression pedals',
        axis_count: 1,
        button_count: 2,
      },
    ]
    let incoming
    let buffer = ''
    const encoder = new TextEncoder()
    const decoder = new TextDecoder()
    const commands = []
    const emit = (message) => incoming.enqueue(encoder.encode(JSON.stringify(message) + '\n'))
    window.adapterFixture = { commands, emit }
    const port = {
      getInfo: () => ({ usbVendorId: 0x1209, usbProductId: 0xfa00 }),
      async open() {
        this.readable = new ReadableStream({
          start(controller) {
            incoming = controller
          },
        })
        this.writable = new WritableStream({
          write(chunk) {
            buffer += decoder.decode(chunk, { stream: true })
            const lines = buffer.split('\n')
            buffer = lines.pop()
            for (const line of lines) {
              if (!line.trim()) continue
              const command = JSON.parse(line)
              commands.push(command)
              switch (command.cmd) {
                case 'version':
                  emit({
                    fw: 'fanadapter-stm32',
                    ver: '0.6.1',
                    protocol: 6,
                    max_bindings_per_channel: 4,
                  })
                  break
                case 'list_devices':
                  emit({ devices })
                  break
                case 'get_config':
                  emit(config)
                  break
                case 'pedals_status':
                  emit({ state: 'STREAMING', throttle: 0, brake: 0, clutch: 0, handbrake: 0 })
                  break
                case 'stream_axes':
                  break // Protocol's only unacknowledged command.
                default:
                  emit({ ok: true })
              }
            }
          },
        })
      },
      async setSignals() {},
      async close() {},
    }
    Object.defineProperty(navigator, 'serial', {
      configurable: true,
      value: { requestPort: async () => port, getPorts: async () => [port] },
    })
    Object.defineProperty(navigator, 'usb', {
      configurable: true,
      value: {
        getDevices: async () => [],
        requestDevice: async () => {
          throw new DOMException('No test bootloader', 'NotFoundError')
        },
      },
    })
  })
}
