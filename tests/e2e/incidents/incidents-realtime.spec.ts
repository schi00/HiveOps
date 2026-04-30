import { test } from '@playwright/test';
import { DashboardPage } from '../../pages/dashboard.page';
import { IncidentsListPage } from '../../pages/incidents-list.page';
import { mockIncidentsSuccess, mockIncidentMessages } from '../../fixtures/routes/api-routes';
import { injectDummyAuth } from '../../utils/auth';
import { simulateLlmRetryUpdated } from '../../utils/signalr-sim';

test.describe('Incidentes: realtime LlmRetryUpdated', () => {
  test('actualiza badge de reintentos sin refrescar', async ({ page }) => {
    await mockIncidentsSuccess(page);
    const dp = new DashboardPage(page);
    const lp = new IncidentsListPage(page);
    await injectDummyAuth(page);
    await page.goto('/dashboard/index.html', { waitUntil: 'domcontentloaded' });
    await dp.openTab('Conversaciones');
    await lp.waitLoaded();

    const id = '11111111-1111-1111-1111-111111111111';
    await mockIncidentMessages(page, id, 'messages.retries.json');

    // Simular evento y validar badge
    await simulateLlmRetryUpdated(page, id, 2);
    await lp.assertRetryBadge(id, 2);
  });
});
