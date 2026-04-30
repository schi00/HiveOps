import { Page } from '@playwright/test';
import { BasePage } from './base.page';

export class DashboardPage extends BasePage {
  constructor(page: Page) { super(page); }

  async openTab(name: 'Resumen' | 'Conversaciones' | 'Configuración' | 'Admin') {
    await this.clickNav(name);
  }

  async selectTenantByName(name: string) {
    await this.page.getByTestId('tenant-selector').click();
    await this.page.getByRole('option', { name }).click();
  }
}
