import { Page, expect } from '@playwright/test';
import { BasePage } from './base.page';

export class SettingsPage extends BasePage {
  constructor(page: Page) { super(page); }

  async setWhatsApp(number: string) {
    await this.page.getByLabel(/whatsapp/i).fill(number);
    await this.page.getByRole('button', { name: /guardar/i }).click();
  }

  async setApiKeys(obj: Record<string,string>) {
    for(const [k,v] of Object.entries(obj)){
      await this.page.getByLabel(new RegExp(k,'i')).fill(v);
    }
    await this.page.getByRole('button', { name: /guardar/i }).click();
  }

  async goToStripePortal() {
    const [page] = await Promise.all([
      this.page.context().waitForEvent('page'),
      this.page.getByRole('link', { name: /stripe/i }).click(),
    ]);
    await page.waitForLoadState('domcontentloaded');
    return page;
  }
}
