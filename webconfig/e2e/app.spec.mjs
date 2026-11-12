import { expect, test } from '@playwright/test'
import { installAdapter } from './adapter.mjs'

test.beforeEach(async ({ page }) => {
  // Firmware downloads are outside this UI regression suite. Never flash a board.
  await page.route('**/firmware/fanadapter-stm32.json', (route) =>
    route.fulfill({ status: 404, body: 'No published firmware in this test' }),
  )
  const errors = []
  page.on('pageerror', (error) => errors.push(error.message))
  page.on('response', (response) => {
    if (response.status() >= 400 && !response.url().endsWith('/firmware/fanadapter-stm32.json')) {
      errors.push(`${response.status()} ${response.url()}`)
    }
  })
  page.on('console', (message) => {
    if (message.type() === 'error' && !message.text().includes('404')) errors.push(message.text())
  })
  // Assertions in teardown also catch errors raised by a late React render.
  page.uiErrors = errors
})

test.afterEach(async ({ page }) => {
  expect(page.uiErrors).toEqual([])
})

async function connect(page) {
  await installAdapter(page)
  await page.goto('./')
  await page.getByRole('banner').getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('tab', { name: 'Devices (1)' })).toBeVisible()
  await expect(page.getByText('Test Regression pedals', { exact: true })).toBeVisible()
}

async function expectCommand(page, command) {
  await expect
    .poll(() => page.evaluate(() => window.adapterFixture.commands))
    .toContainEqual(command)
}

test('production assets, theme and firmware dialog work under the Pages base path', async ({
  page,
}) => {
  await installAdapter(page)
  const failedAssets = []
  page.on('response', (response) => {
    if (response.url().includes('/assets/') && !response.ok()) failedAssets.push(response.url())
  })
  await page.goto('./')
  const flash = page.getByRole('button', { name: 'Flash', exact: true })
  await expect(flash).toHaveCSS('cursor', 'pointer')
  await expect(flash).toHaveCSS('border-radius', '6px')
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(255, 255, 255)')
  await expect(page.locator('main')).toHaveCSS('max-width', '1024px')
  await expect(flash.locator('svg')).toHaveCount(1)
  await flash.click()
  const dialog = page.getByRole('dialog', { name: 'Firmware update' })
  await expect(dialog).toBeVisible()
  await expect(dialog).toHaveCSS('border-radius', '8px')
  await expect(dialog.locator(':focus')).toHaveCount(1)
  await page.keyboard.press('Escape')
  await expect(dialog).not.toBeVisible()
  await flash.click()
  await expect(dialog).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(dialog).not.toBeVisible()
  await page.evaluate(() => document.documentElement.classList.add('dark'))
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(2, 8, 23)')
  expect(failedAssets).toEqual([])
})

test('serial session, keyboard tabs, blur commits, save and disconnect still work', async ({
  page,
}) => {
  await connect(page)
  await page.getByRole('tab', { name: 'Outputs', exact: true }).click()
  // Tailwind 4 space-y uses bottom margins; inline labels lost their old gaps.
  await expect(page.locator('#pulse-ms')).toHaveCSS('height', '40px')
  expect(
    await page.locator('#pulse-ms').evaluate((input) => {
      const label = input.previousElementSibling
      return input.getBoundingClientRect().top - label.getBoundingClientRect().bottom
    }),
  ).toBe(4)
  await page.locator('#pulse-ms').fill('75')
  await page.getByRole('tab', { name: 'Mappings', exact: true }).click()
  await expectCommand(page, { cmd: 'set_pulse_ms', value: 75 })
  await page.keyboard.press('ArrowRight')
  await expect(page.getByRole('tab', { name: 'Outputs', exact: true })).toHaveAttribute(
    'aria-selected',
    'true',
  )
  await expect(page.locator('#pulse-ms')).toHaveValue('75')
  await page.getByRole('button', { name: /^Save/ }).click()
  await expectCommand(page, { cmd: 'save_config' })
  await expect(page.getByRole('button', { name: /^Save/ })).toBeDisabled()
  await page.getByRole('button', { name: 'Disconnect', exact: true }).click()
  await expect(page.getByText('Disconnected', { exact: true })).toBeVisible()
})

test('Radix switch and range slider commit mapping values through serial', async ({ page }) => {
  await connect(page)
  await page.getByRole('tab', { name: 'Mappings', exact: true }).click()
  const invert = page.getByRole('switch', { name: 'Invert' })
  await expect(page.getByText('rawMin', { exact: true })).toHaveCSS('line-height', '24px')
  await expect(invert).not.toBeChecked()
  const thumb = invert.locator('span')
  const uncheckedX = await thumb.evaluate((element) => element.getBoundingClientRect().x)
  await invert.click()
  await expect(invert).toBeChecked()
  await expect
    .poll(() => thumb.evaluate((element) => element.getBoundingClientRect().x))
    .toBeGreaterThan(uncheckedX + 15)
  await expect
    .poll(() =>
      page.evaluate(() =>
        window.adapterFixture.commands.filter((c) => c.cmd === 'set_binding').at(-1),
      ),
    )
    .toMatchObject({ cmd: 'set_binding', channel: 'throttle', slot: 0, binding: { invert: true } })
  const low = page.getByRole('slider').first()
  await low.focus()
  await low.press('ArrowRight')
  await expect(low).toHaveAttribute('aria-valuenow', '64')
  await expect
    .poll(() =>
      page.evaluate(() =>
        window.adapterFixture.commands.filter((c) => c.cmd === 'set_binding').at(-1),
      ),
    )
    .toMatchObject({
      cmd: 'set_binding',
      channel: 'throttle',
      slot: 0,
      binding: { deadzoneLow: 64, invert: true },
    })
  const high = page.getByRole('slider').nth(1)
  await high.focus()
  await high.press('ArrowLeft')
  await expect(high).toHaveAttribute('aria-valuenow', '65472')
  await expect
    .poll(() =>
      page.evaluate(() =>
        window.adapterFixture.commands.filter((c) => c.cmd === 'set_binding').at(-1),
      ),
    )
    .toMatchObject({
      channel: 'throttle',
      binding: { deadzoneLow: 64, deadzoneHigh: 65472, invert: true },
    })
})

test('unsupported browsers keep connection disabled', async ({ page }) => {
  await page.addInitScript(() => {
    delete Navigator.prototype.serial
  })
  await page.goto('./')
  await expect(page.getByRole('button', { name: 'Connect', exact: true })).toHaveCount(2)
  for (const connectButton of await page
    .getByRole('button', { name: 'Connect', exact: true })
    .all()) {
    await expect(connectButton).toBeDisabled()
  }
})
