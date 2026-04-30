import { Page, expect } from '@playwright/test';
import { BasePage } from './base.page';

export class IncidentDetailPage extends BasePage {
  constructor(page: Page) { super(page); }

  async openIncident(id: string) {
    await this.page.locator(`[onclick="od('${id}')"]`).first().click();
    await this.page.waitForSelector('#db');
  }

  async assertAdminLogsVisible(isAdmin: boolean) {
    const selector = 'a[title="Ver logs de despliegue"], [data-testid="deploy-logs"]';
    if (isAdmin) await expect(this.page.locator(selector)).toBeVisible();
    else await expect(this.page.locator(selector)).toHaveCount(0);
  }

  async rollbackButtonState(expected: 'hidden'|'disabled'|'enabled') {
    const btn = this.page.getByRole('button', { name: /rollback/i });
    if (expected === 'hidden') await expect(btn).toHaveCount(0);
    if (expected === 'disabled') await expect(btn).toBeDisabled();
    if (expected === 'enabled') await expect(btn).toBeEnabled();
  }
}
