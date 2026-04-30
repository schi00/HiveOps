import { Page, expect } from '@playwright/test';

export class BasePage {
  constructor(protected readonly page: Page) {}

  async goto(path = '') {
    const url = path || '/';
    await this.page.goto(url, { waitUntil: 'domcontentloaded' });
  }

  async clickNav(label: string) {
    await this.page.getByRole('link', { name: label }).click();
  }

  async waitForToast() {
    await this.page.waitForSelector('.toast, [data-testid="toast"]', { state: 'visible' }).catch(() => {});
  }

  async assertVisible(text: string) {
    await expect(this.page.getByText(text)).toBeVisible();
  }
}
