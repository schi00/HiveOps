import { Page, expect } from '@playwright/test';
import { BasePage } from './base.page';

export class IncidentsListPage extends BasePage {
  constructor(page: Page) { super(page); }

  async waitLoaded() { await this.page.waitForSelector('#l-all'); }

  async filterByStatus(status: string) {
    await this.page.locator('#fs').selectOption(status);
  }

  async filterBySeverity(sev: string) {
    await this.page.locator('#fv').selectOption(sev);
  }

  async assertBadgeForStatusPresent(status: 'SUCCESS'|'FAILED'|'DEPLOYING') {
    const cls = status === 'SUCCESS' ? 'bg-emerald-500' : status === 'FAILED' ? 'bg-red-500' : 'bg-blue-500';
    await expect(this.page.locator(`.font-medium >> .${cls}`)).toBeVisible();
  }

  async assertRetryBadge(incidentId: string, attempt: number) {
    const slot = this.page.locator(`#rt-${incidentId}`);
    await expect(slot).toContainText(`${attempt}/3`);
  }
}
