import { FirmwareCommandError, SerialConnectionError } from './serial'
import type { VersionInfo } from './types'

export interface ErrorReport {
  title: string
  message: string
  troubleshootingSteps: string[]
  details: string
  issueUrl: string | null
  occurredAt: string
}

interface ErrorContext {
  firmware: VersionInfo | null
  tab: string
  browser: string
  page: string
}

const ISSUE_URL = 'https://github.com/Gugic/fanadapter/issues/new'
const MAX_ISSUE_URL = 7500
const COMMAND_FIELDS = [
  'cmd',
  'channel',
  'slot',
  'x',
  'y',
  'value',
  'on',
  'direction',
  'gear',
  'throttle',
  'brake',
  'clutch',
  'handbrake',
] as const
const BINDING_FIELDS = [
  'vid',
  'pid',
  'type',
  'index',
  'threshold',
  'rawMin',
  'rawMax',
  'deadzoneLow',
  'deadzoneHigh',
  'invert',
] as const

function truncate(value: string, limit: number): string {
  if (value.length <= limit) return value
  const suffix = '… [truncated]'
  // Do not split a surrogate pair when shortening a Unicode message.
  return value.slice(0, limit - suffix.length).replace(/[\uD800-\uDBFF]$/, '') + suffix
}

function deploymentUrl(value: string): string {
  try {
    const url = new URL(value)
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return '[URL omitted]'
    // origin excludes username/password; search and hash may contain access tokens.
    return truncate(url.origin + url.pathname, 512)
  } catch {
    return '[URL omitted]'
  }
}

function text(value: string, limit: number): string {
  // Bound work before scanning. An incomplete URL is omitted rather than copied verbatim.
  const bounded = truncate(value, limit * 4)
  return truncate(
    bounded
      .replace(/\b[a-z][a-z\d+.-]*:\/\/[^\s<>"'`]+/gi, deploymentUrl)
      .replace(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/gu, ' ')
      .trim(),
    limit,
  )
}

function pickFields(source: Record<string, unknown>, fields: readonly string[]) {
  const result: Record<string, string | number | boolean> = {}
  for (const field of fields) {
    const value = source[field]
    if (typeof value === 'string') result[field] = text(value, 128)
    else if (typeof value === 'boolean' || (typeof value === 'number' && Number.isFinite(value))) {
      result[field] = value
    }
  }
  return result
}

function commandSnapshot(command: Record<string, unknown>): Record<string, unknown> {
  const result: Record<string, unknown> = pickFields(command, COMMAND_FIELDS)
  if (command.cmd === 'set_binding') {
    const binding = command.binding
    if (binding && typeof binding === 'object' && !Array.isArray(binding)) {
      result.binding = pickFields(binding as Record<string, unknown>, BINDING_FIELDS)
    }
  }
  return result
}

function firmwareSnapshot(value: unknown) {
  // version() receives JSON at runtime; its TypeScript return type does not validate it.
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const firmware = value as Record<string, unknown>
  return {
    fw: typeof firmware.fw === 'string' ? text(firmware.fw, 128) : null,
    ver: typeof firmware.ver === 'string' ? text(firmware.ver, 128) : null,
    protocol:
      typeof firmware.protocol === 'number' && Number.isFinite(firmware.protocol)
        ? firmware.protocol
        : null,
  }
}

function connectionHelp(
  error: unknown,
): Pick<ErrorReport, 'title' | 'message' | 'troubleshootingSteps'> | null {
  if (!(error instanceof SerialConnectionError)) return null
  switch (error.reason) {
    case 'open-failed':
      return {
        title: 'Could not open the adapter',
        message:
          'The browser could not open the selected serial port. Another app may be using it, or the device may be unavailable.',
        troubleshootingSteps: [
          'Close other browser tabs connected to the adapter, serial monitors and SimHub, then try Connect again.',
          'Check that the adapter is connected by USB and running Fanadapter firmware, rather than the STM32 bootloader.',
          'Reconnect the USB cable, then select the adapter again.',
        ],
      }
    case 'permission-denied':
      return {
        title: 'Access to the adapter was denied',
        message: 'The browser or operating system did not allow access to the serial port.',
        troubleshootingSteps: [
          "Check this site's serial-port permission in the browser settings, then try Connect again.",
          'Check operating-system device permissions or managed-browser restrictions.',
          'Use Connect to grant access to the adapter when the browser asks.',
        ],
      }
    case 'unsupported':
      return {
        title: 'This browser cannot connect to the adapter',
        message: 'WebSerial is not available in this browser.',
        troubleshootingSteps: [
          'Open this page in a current desktop version of Chrome, Edge or Brave.',
          'Use HTTPS or a local development URL such as http://localhost.',
          'Connect again and select the adapter when prompted.',
        ],
      }
    default:
      return null
  }
}

function presentation(error: unknown, rawMessage: string): Pick<ErrorReport, 'title' | 'message'> {
  if (error instanceof FirmwareCommandError) {
    if (error.command.cmd === 'set_binding') {
      return {
        title: 'The mapping was rejected',
        message:
          'The adapter could not accept this mapping. Review the details before trying again.',
      }
    }
    if (error.command.cmd === 'save_config') {
      return {
        title: 'The configuration could not be saved',
        message:
          'The adapter reported a save failure. Keep the current session open and try Save again.',
      }
    }
    return {
      title: 'The adapter rejected the request',
      message: 'The adapter reported an error. Review the details before trying again.',
    }
  }
  if (/timeout|timed out/i.test(rawMessage)) {
    return {
      title: 'The operation timed out',
      message:
        'No confirmation was received. The operation may have reached the adapter; reconnect and check its state before retrying.',
    }
  }
  return {
    title: 'The operation could not be completed',
    message: 'Review the error details and check the connection before trying again.',
  }
}

function issueLink(title: string, message: string, details: string, occurredAt: string): string {
  const introduction = [
    '### What happened',
    message,
    '',
    '### Steps to reproduce',
    '1. [Describe what you were doing before the error.]',
    '',
    '### Expected behavior',
    '[Describe what you expected to happen.]',
    '',
    '### Actual behavior',
    '[Describe what happened, including whether it happens again.]',
    '',
    '### Diagnostics',
    'Review these details before submitting the issue.',
    '',
  ].join('\n')
  const url = new URL(ISSUE_URL)
  url.searchParams.set('title', title)
  url.searchParams.set('body', introduction + '\n```json\n' + details + '\n```')
  if (url.href.length > MAX_ISSUE_URL) {
    url.searchParams.set(
      'body',
      introduction +
        `\nError time: ${occurredAt}\n\n` +
        'The diagnostic details are too large for this link. Use Copy details in webconfig, then paste them below before submitting.\n\n' +
        '```json\n[Paste copied details here.]\n```',
    )
  }
  return url.href
}

/** Capture only the failed request and relevant environment, never logs or a full configuration. */
export function createErrorReport(error: unknown, context: ErrorContext): ErrorReport {
  const occurredAt = new Date().toISOString()
  const rawMessage = error instanceof Error ? error.message : typeof error === 'string' ? error : ''
  const errorMessage = text(rawMessage, 1024)
  const help = connectionHelp(error)
  const { title, message } = help ?? presentation(error, errorMessage)
  const issueTitle =
    error instanceof FirmwareCommandError
      ? truncate(
          `[Webconfig] ${text(typeof error.command.cmd === 'string' ? error.command.cmd : 'request', 64)} failed: ${text(error.code, 128)}`,
          200,
        )
      : `[Webconfig] ${title}`
  const details = JSON.stringify(
    {
      occurredAt,
      error: {
        name: error instanceof Error ? text(error.name, 64) : 'UnknownError',
        message: errorMessage || 'An unexpected error occurred.',
        ...(error instanceof FirmwareCommandError ? { code: text(error.code, 128) } : {}),
        ...(error instanceof SerialConnectionError ? { reason: text(error.reason, 128) } : {}),
      },
      ...(error instanceof FirmwareCommandError
        ? { rejectedCommand: commandSnapshot(error.command) }
        : {}),
      firmware: firmwareSnapshot(context.firmware),
      browser: text(context.browser, 512),
      tab: text(context.tab, 64),
      deployment: deploymentUrl(context.page),
    },
    null,
    2,
  )
  return {
    title,
    message,
    troubleshootingSteps: help?.troubleshootingSteps ?? [],
    details,
    occurredAt,
    issueUrl: help ? null : issueLink(issueTitle, message, details, occurredAt),
  }
}
