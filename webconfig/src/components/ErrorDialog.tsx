import { useEffect, useId, useRef, useState } from 'react'
import { Content } from '@radix-ui/react-dialog'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogOverlay,
  DialogPortal,
  DialogTitle,
} from '@/components/ui/dialog'
import type { ErrorReport } from '@/lib/errorReport'

export function ErrorDialog({
  report,
  onClose,
}: {
  report: ErrorReport | null
  onClose: () => void
}) {
  const detailsId = useId()
  const closeButton = useRef<HTMLButtonElement>(null)
  const previousFocus = useRef<HTMLElement | null>(null)
  const lastFocusedControl = useRef<HTMLElement | null>(null)
  const [view, setView] = useState({
    report,
    detailsOpen: false,
    copying: false,
    copied: false,
    copyFailed: false,
  })

  // Listen becomes disabled during capture, which can move browser focus to BODY
  // before its asynchronous failure arrives. Retain its identity for dismissal.
  useEffect(() => {
    if (report) return
    const rememberFocus = () => {
      const target = document.activeElement
      if (
        target instanceof HTMLElement &&
        target !== document.body &&
        target !== document.documentElement &&
        target.tabIndex >= 0
      ) {
        lastFocusedControl.current = target
      }
    }
    rememberFocus()
    document.addEventListener('focusin', rememberFocus)
    return () => document.removeEventListener('focusin', rememberFocus)
  }, [report])

  // A second occurrence is a new report even when its message is identical.
  if (view.report !== report) {
    setView({ report, detailsOpen: false, copying: false, copied: false, copyFailed: false })
  }

  async function copyDetails() {
    if (!report) return
    const currentReport = report
    setView((current) => ({ ...current, copying: true, copied: false, copyFailed: false }))
    try {
      await navigator.clipboard.writeText(currentReport.details)
      setView((current) =>
        current.report === currentReport ? { ...current, copying: false, copied: true } : current,
      )
    } catch {
      setView((current) =>
        current.report === currentReport
          ? { ...current, copying: false, copyFailed: true, detailsOpen: true }
          : current,
      )
    }
  }

  if (!report) return null

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose()
      }}
    >
      <DialogPortal>
        {/* Keep this modal above an active setup wizard or firmware dialog. */}
        <DialogOverlay className="z-[60]" />
        <Content
          className="fixed left-1/2 top-1/2 z-[70] grid max-h-[calc(100vh-2rem)] w-[calc(100%-2rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 gap-4 overflow-y-auto rounded-lg border bg-background p-6 text-foreground shadow-lg"
          onOpenAutoFocus={(event) => {
            const active = document.activeElement
            previousFocus.current =
              active instanceof HTMLElement &&
              active !== document.body &&
              active !== document.documentElement &&
              active.tabIndex >= 0 &&
              !active.matches(':disabled')
                ? active
                : lastFocusedControl.current
            event.preventDefault()
            closeButton.current?.focus({ preventScroll: true })
          }}
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            const target = previousFocus.current
            if (
              target?.isConnected &&
              target.tabIndex >= 0 &&
              !target.matches(':disabled') &&
              !target.closest('[inert], [aria-hidden="true"]') &&
              target.getClientRects().length > 0
            ) {
              target.focus({ preventScroll: true })
            }
          }}
        >
          <DialogHeader>
            <DialogTitle className="break-words leading-snug">{report.title}</DialogTitle>
            <DialogDescription className="break-words">{report.message}</DialogDescription>
          </DialogHeader>

          {report.troubleshootingSteps.length > 0 && (
            <ol
              aria-label="Connection troubleshooting"
              className="list-decimal space-y-2 pl-5 text-sm"
            >
              {report.troubleshootingSteps.map((step) => (
                <li key={step} className="break-words">
                  {step}
                </li>
              ))}
            </ol>
          )}

          <details
            open={view.detailsOpen}
            onToggle={(event) => {
              const open = event.currentTarget.open
              setView((current) =>
                current.detailsOpen === open ? current : { ...current, detailsOpen: open },
              )
            }}
            className="min-w-0"
          >
            <summary className="cursor-pointer text-sm font-medium">Diagnostic details</summary>
            <label htmlFor={detailsId} className="sr-only">
              Error report details
            </label>
            <textarea
              id={detailsId}
              readOnly
              value={report.details}
              rows={10}
              spellCheck={false}
              className="mt-2 block max-h-64 w-full rounded-md border bg-muted/30 p-3 font-mono text-xs"
            />
          </details>

          <div role="status" aria-live="polite" className="text-sm text-muted-foreground">
            {view.copied && 'Details copied.'}
            {view.copyFailed && 'Clipboard access failed. Select and copy the details above.'}
          </div>

          <DialogFooter className="gap-2 sm:space-x-0">
            <Button variant="outline" onClick={copyDetails} disabled={view.copying}>
              Copy details
            </Button>
            {report.issueUrl && (
              <Button variant="outline" asChild>
                <a href={report.issueUrl} target="_blank" rel="noopener noreferrer">
                  Report issue
                </a>
              </Button>
            )}
            <Button ref={closeButton} onClick={onClose}>
              Close
            </Button>
          </DialogFooter>
          {report.issueUrl && (
            <p className="text-xs text-muted-foreground">
              Report issue opens an editable draft on GitHub.
            </p>
          )}
        </Content>
      </DialogPortal>
    </Dialog>
  )
}
