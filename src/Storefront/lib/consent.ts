// Consent-aware first-party analytics (mt5_5). Three categories; Necessary is always on, Analytics and
// Marketing are opt-in. State is stored first-party in localStorage and broadcast so the tracker reacts.

export type ConsentCategory = "necessary" | "analytics" | "marketing";

export interface ConsentState {
  necessary: true;
  analytics: boolean;
  marketing: boolean;
  decidedAt: string | null; // ISO timestamp; null => not yet decided (show the banner)
}

const STORAGE_KEY = "3c_consent";
export const CONSENT_EVENT = "3c:consent";

const DEFAULT: ConsentState = {
  necessary: true,
  analytics: false,
  marketing: false,
  decidedAt: null,
};

export function readConsent(): ConsentState {
  if (typeof window === "undefined") return DEFAULT;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULT;
    const parsed = JSON.parse(raw) as Partial<ConsentState>;
    return {
      necessary: true,
      analytics: parsed.analytics === true,
      marketing: parsed.marketing === true,
      decidedAt: typeof parsed.decidedAt === "string" ? parsed.decidedAt : null,
    };
  } catch {
    return DEFAULT;
  }
}

export function writeConsent(choice: { analytics: boolean; marketing: boolean }): ConsentState {
  const state: ConsentState = {
    necessary: true,
    analytics: choice.analytics,
    marketing: choice.marketing,
    decidedAt: new Date().toISOString(),
  };
  if (typeof window !== "undefined") {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
    window.dispatchEvent(new CustomEvent<ConsentState>(CONSENT_EVENT, { detail: state }));
  }
  return state;
}

export function hasConsent(category: ConsentCategory, state?: ConsentState): boolean {
  if (category === "necessary") return true;
  return (state ?? readConsent())[category] === true;
}

export function consentDecided(state?: ConsentState): boolean {
  return (state ?? readConsent()).decidedAt !== null;
}
