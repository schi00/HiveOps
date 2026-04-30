import { Page, expect } from '@playwright/test';
import { BasePage } from './base.page';

export class LoginPage extends BasePage {
  constructor(page: Page) { super(page); }

  async gotoLogin() {
    await this.page.goto('/dashboard');
  }

  async loginWithEnv() {
    const user = process.env.PW_ADMIN_USER ?? '';
    const pass = process.env.PW_ADMIN_PASS ?? '';
    expect(user && pass).toBeTruthy();
    await this.page.locator('#lu').fill(user);
    await this.page.locator('#lp').fill(pass);
    // Click boton "Ingresar"
    await this.page.getByRole('button', { name: /ingresar/i }).click();
    // Esperar a que desaparezca el overlay de login y aparezca el app
    await this.page.locator('#login-screen').waitFor({ state: 'hidden' });
    await this.page.locator('#app').waitFor({ state: 'visible' });
  }

  async assertTokenPersisted() {
    const token = await this.page.evaluate(() => localStorage.getItem('token'));
    expect(token).toBeTruthy();
  }
}
