declare global {
  interface Window {
    gtag?: (...args: unknown[]) => void;
    fbq?: (...args: unknown[]) => void;
  }
}

export function trackEvent(eventName: string, params?: Record<string, string | number>) {
  if (typeof window === "undefined") return;

  window.gtag?.("event", eventName, params ?? {});
  window.fbq?.("trackCustom", eventName, params ?? {});
}
