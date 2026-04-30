import { Page, APIRequestContext } from '@playwright/test';
import path from 'path';
import fs from 'fs';

function loadFixture<T>(file: string): T {
  const p = path.resolve(__dirname, '..', 'test-data', file);
  return JSON.parse(fs.readFileSync(p, 'utf8')) as T;
}

export async function mockIncidentsSuccess(page: Page) {
  const body = loadFixture<any>('incidents.success.json');
  await page.route(/\/api\/support\/incidents(\?.*)?$/, route => route.fulfill({ status: 200, body: JSON.stringify(body), headers: { 'content-type': 'application/json' } }));
}

export async function mockIncidentsError500(page: Page) {
  await page.route(/\/api\/support\/incidents(\?.*)?$/, route => route.fulfill({ status: 500, body: 'server error' }));
}

export async function mockIncidentDetail(page: Page, id: string, fixture: string) {
  const body = loadFixture<any>(fixture);
  await page.route(new RegExp(`/api/support/incidents/${id}$`), r => r.fulfill({ status: 200, body: JSON.stringify(body) }));
}

export async function mockIncidentMessages(page: Page, id: string, fixture: string) {
  const body = loadFixture<any>(fixture);
  await page.route(new RegExp(`/api/support/incidents/${id}/messages$`), r => r.fulfill({ status: 200, body: JSON.stringify(body) }));
}

export async function mockTenants(page: Page, fixture = 'tenants.json') {
  const body = loadFixture<any>(fixture);
  await page.route(/\/api\/admin\/tenants$/, r => r.fulfill({ status: 200, body: JSON.stringify(body) }));
}

export async function mockRollbackForbidden(page: Page, id: string) {
  await page.route(new RegExp(`/api/support/incidents/${id}/rollback$`), r => r.fulfill({ status: 403, body: 'forbidden' }));
}
