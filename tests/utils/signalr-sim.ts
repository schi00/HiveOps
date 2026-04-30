import { Page } from '@playwright/test';

// Simula recepción de evento LlmRetryUpdated actualizando el badge específico
export async function simulateLlmRetryUpdated(page: Page, incidentId: string, attempt: number) {
  await page.evaluate(([id, n]) => {
    const slot = document.getElementById(`rt-${id}`);
    // Usa renderRetryBadge si está global, sino genera html mínimo
    // @ts-ignore
    const html = typeof window.renderRetryBadge === 'function' ? window.renderRetryBadge(n) : `<span class="px-1.5 py-0.5 rounded text-[10px] bg-amber-500 text-white">Reintentos LLM: ${n}/3</span>`;
    if (slot){ slot.innerHTML = html; slot.classList.add('status-pulse'); setTimeout(()=>slot.classList.remove('status-pulse'), 1200); }
  }, [incidentId, attempt]);
}
