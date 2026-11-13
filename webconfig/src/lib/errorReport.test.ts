import { afterEach, describe, expect, it, vi } from 'vitest'

import { createErrorReport } from './errorReport'
import type { ErrorReport } from './errorReport'
import { FirmwareCommandError, SerialConnectionError } from './serial'
import type { VersionInfo } from './types'

function issueFor(report: ErrorReport): URL {
  expect(report.issueUrl).not.toBeNull()
  if (!report.issueUrl) throw new Error('Expected an issue link')
  return new URL(report.issueUrl)
}

function context() {
  return {
    firmware: { fw: 'fanadapter-stm32', ver: '0.6.4', protocol: 6 },
    tab: 'mappings',
    browser: 'Example Chromium/140.0',
    page: 'https://gugic.github.io/fanadapter/',
  }
}

afterEach(() => vi.useRealTimers())

describe('error reports', () => {
  it('captures the rejected mapping and environment without unrelated device or config data', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-12T17:00:00Z'))
    const report = createErrorReport(
      new FirmwareCommandError('bad_json', {
        cmd: 'set_binding',
        channel: 'throttle',
        slot: 0,
        binding: {
          vid: 0x0eb7,
          pid: 0x183b,
          type: 'axis',
          index: 2,
          invert: false,
          serial: 'secret',
        },
        config: { password: 'secret' },
        logs: ['secret'],
      }),
      context(),
    )

    expect(report.title).toBe('The mapping was rejected')
    expect(issueFor(report).searchParams.get('title')).toBe(
      '[Webconfig] set_binding failed: bad_json',
    )
    expect(report.occurredAt).toBe('2026-11-12T17:00:00.000Z')
    expect(JSON.parse(report.details)).toEqual({
      occurredAt: report.occurredAt,
      error: {
        name: 'FirmwareCommandError',
        message: 'Firmware error: bad_json',
        code: 'bad_json',
      },
      rejectedCommand: {
        cmd: 'set_binding',
        channel: 'throttle',
        slot: 0,
        binding: { vid: 0x0eb7, pid: 0x183b, type: 'axis', index: 2, invert: false },
      },
      firmware: context().firmware,
      browser: context().browser,
      tab: 'mappings',
      deployment: context().page,
    })
    expect(report.details).not.toContain('secret')
  })

  it('distinguishes Save rejection from an unconfirmed timeout', () => {
    const save = createErrorReport(
      new FirmwareCommandError('flash_save_failed', { cmd: 'save_config' }),
      context(),
    )
    expect(save.title).toBe('The configuration could not be saved')
    expect(save.details).toContain('flash_save_failed')
    const timeout = createErrorReport(new Error('command timeout: save_config'), context())
    expect(timeout.message).toContain('may have reached the adapter')
    expect(timeout.details).not.toContain('rejectedCommand')
    const other = createErrorReport(
      new FirmwareCommandError('bad_value', { cmd: 'set_pulse_ms', value: 0 }),
      context(),
    )
    expect(other.title).toBe('The adapter rejected the request')
  })

  it('prefills an editable GitHub issue with correctly encoded diagnostics and reproduction prompts', () => {
    const report = createErrorReport(
      new Error('Unexpected café + pedal & brake = "失敗" #1'),
      context(),
    )
    const url = issueFor(report)
    expect(url.origin + url.pathname).toBe('https://github.com/Gugic/fanadapter/issues/new')
    expect(url.searchParams.get('title')).toBe(`[Webconfig] ${report.title}`)
    const body = url.searchParams.get('body')
    expect(body).toContain(report.details)
    expect(body).toContain('### Steps to reproduce')
    expect(body).toContain('### Expected behavior')
    expect(body).toContain('### Actual behavior')
    expect(url.hash).toBe('')
  })

  it('removes credentials, query strings and fragments from deployment and error URLs', () => {
    const report = createErrorReport(
      new Error(
        'Fetch failed: https://user:password@example.com/firmware.json?token=secret#private',
      ),
      {
        ...context(),
        page: 'https://name:credential@example.com/fanadapter/?access=hidden#session',
      },
    )
    expect(report.details).toContain('https://example.com/fanadapter/')
    expect(report.details).toContain('https://example.com/firmware.json')
    for (const secret of [
      'password',
      'credential',
      'token=',
      'secret',
      'private',
      'access=',
      'hidden',
      'session',
    ]) {
      expect(report.details).not.toContain(secret)
      expect(decodeURIComponent(issueFor(report).href)).not.toContain(secret)
    }
  })

  it('keeps the report stable when the supplied command or environment changes', () => {
    const command = { cmd: 'set_binding', channel: 'throttle', binding: { rawMax: 4095 } }
    const environment = context()
    const report = createErrorReport(new FirmwareCommandError('bad_binding', command), environment)
    const captured = { ...report }
    command.binding.rawMax = 1
    environment.firmware.ver = 'changed'
    environment.tab = 'outputs'
    expect(report).toEqual(captured)
    expect(report.details).toContain('4095')
    expect(report.details).toContain('0.6.4')
  })

  it('bounds Unicode diagnostics and falls back to explicit copy instructions for a long URL', () => {
    const report = createErrorReport(new Error('🛞'.repeat(10000)), {
      ...context(),
      browser: '界'.repeat(10000),
      tab: 'x'.repeat(10000),
      firmware: { fw: '名'.repeat(10000), ver: '版'.repeat(10000), protocol: 6 },
    })
    expect(report.details.length).toBeLessThan(5000)
    expect(report.details).toContain('[truncated]')
    expect(() => JSON.parse(report.details) as unknown).not.toThrow()
    expect(issueFor(report).href.length).toBeLessThanOrEqual(7500)
    expect(issueFor(report).searchParams.get('body')).toContain('Use Copy details in webconfig')
    expect(report.details).not.toContain('\uFFFD')
  })

  it('bounds diagnostic issue titles and removes line breaks and control characters', () => {
    const report = createErrorReport(
      new FirmwareCommandError('bad\r\ncode\u0000\u2028\u2029\u202E' + 'x'.repeat(1000), {
        cmd: 'set_binding',
      }),
      context(),
    )
    const title = issueFor(report).searchParams.get('title')
    expect(title).toContain('[Webconfig] set_binding failed: bad  code ')
    expect(title?.length).toBeLessThanOrEqual(200)
    expect(title).not.toMatch(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u)
    expect(issueFor(report).href.length).toBeLessThanOrEqual(7500)
  })

  it('handles unknown errors without serializing arbitrary objects or invoking toString', () => {
    const unknownError = {
      config: 'private',
      toString: () => {
        throw new Error('must not run')
      },
    }
    for (const error of [undefined, null, 42, Symbol('private'), unknownError]) {
      const report = createErrorReport(error, { ...context(), firmware: null })
      expect(report.details).toContain('An unexpected error occurred.')
      expect(report.details).not.toContain('private')
      expect(report.details).toContain('"firmware": null')
    }
    expect(createErrorReport('Port closed', context()).details).toContain('Port closed')
  })

  it('omits invalid or non-web deployment URLs without copying local paths', () => {
    for (const page of ['file:///Users/private/fanadapter', 'not-a-url?token=secret#private']) {
      const report = createErrorReport(new Error('Disconnected'), { ...context(), page })
      expect(report.details).toContain('[URL omitted]')
      expect(report.details).not.toContain('private')
      expect(report.details).not.toContain('secret')
    }
  })

  it.each([
    ['open-failed', 'Could not open the adapter', 'Close other browser tabs'],
    [
      'permission-denied',
      'Access to the adapter was denied',
      "Check this site's serial-port permission",
    ],
    [
      'unsupported',
      'This browser cannot connect to the adapter',
      'current desktop version of Chrome',
    ],
  ] as const)('offers troubleshooting without an issue link for %s', (reason, title, step) => {
    const report = createErrorReport(
      new SerialConnectionError(reason, 'Original browser message'),
      context(),
    )
    expect(report.title).toBe(title)
    expect(report.troubleshootingSteps.join('\n')).toContain(step)
    expect(report.issueUrl).toBeNull()
    expect(report.details).toContain('Original browser message')
    expect(report.details).toContain(`"reason": "${reason}"`)
    expect(() => JSON.parse(report.details) as unknown).not.toThrow()
  })

  it('keeps issue reporting for firmware errors and unexpected errors with similar messages', () => {
    for (const error of [
      new FirmwareCommandError('bad_json', { cmd: 'set_binding' }),
      new FirmwareCommandError('flash_save_failed', { cmd: 'save_config' }),
      new DOMException('Failed to open serial port', 'NetworkError'),
      new DOMException('Permission denied', 'SecurityError'),
      new Error('WebSerial is not supported'),
    ]) {
      const report = createErrorReport(error, context())
      expect(issueFor(report).origin).toBe('https://github.com')
      expect(report.troubleshootingSteps).toEqual([])
    }
  })

  it('handles missing or malformed firmware fields without serializing unexpected data', () => {
    const invalidFields = [
      {},
      { fw: 1, ver: false, protocol: '6' },
      { fw: { private: 'secret' }, ver: ['secret'], protocol: Number.NaN },
      { protocol: Number.POSITIVE_INFINITY },
      { protocol: 6n },
    ]
    for (const firmware of invalidFields) {
      const report = createErrorReport(new Error('Failed'), {
        ...context(),
        firmware: firmware as unknown as VersionInfo,
      })
      expect(JSON.parse(report.details)).toMatchObject({
        firmware: { fw: null, ver: null, protocol: null },
      })
      expect(report.details).not.toContain('secret')
    }
    for (const firmware of [null, undefined, false, 'secret', []]) {
      const report = createErrorReport(new Error('Failed'), {
        ...context(),
        firmware: firmware as unknown as VersionInfo,
      })
      expect(JSON.parse(report.details)).toMatchObject({ firmware: null })
    }
    const partial = createErrorReport(new Error('Failed'), {
      ...context(),
      firmware: { ver: '0.6.4', protocol: 6 } as VersionInfo,
    })
    expect(JSON.parse(partial.details)).toMatchObject({
      firmware: { fw: null, ver: '0.6.4', protocol: 6 },
    })
  })
})
