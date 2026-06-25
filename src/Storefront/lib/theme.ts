// Storefront theming (mt5_6): a small set of design tokens a tenant can override, rendered as CSS
// custom properties. GOTCHA: tenant-supplied values are SANITIZED — no url()/expression()/script or
// rule-breaking characters — so a theme can never smuggle in arbitrary CSS/JS.

export interface ThemeTokens {
  colorPrimary: string;
  colorBg: string;
  colorText: string;
  colorMuted: string;
  fontSans: string;
  radius: string;
}

export const defaultTheme: ThemeTokens = {
  colorPrimary: "#111827",
  colorBg: "#ffffff",
  colorText: "#111827",
  colorMuted: "#6b7280",
  fontSans: "system-ui, -apple-system, sans-serif",
  radius: "0.5rem",
};

const SAFE_VALUE = /^[#a-zA-Z0-9.,()%\s_-]+$/;
const DANGEROUS = /url\(|expression|javascript:|@import|[;{}<>]/i;

export function safeTokenValue(value: string, fallback: string): string {
  const trimmed = (value ?? "").trim();
  if (!trimmed || trimmed.length > 100 || DANGEROUS.test(trimmed) || !SAFE_VALUE.test(trimmed)) {
    return fallback;
  }
  return trimmed;
}

export function mergeTheme(overrides: Partial<ThemeTokens> | null | undefined): ThemeTokens {
  const theme = { ...defaultTheme };
  if (!overrides) return theme;
  for (const key of Object.keys(defaultTheme) as (keyof ThemeTokens)[]) {
    const value = overrides[key];
    if (typeof value === "string") {
      theme[key] = safeTokenValue(value, defaultTheme[key]);
    }
  }
  return theme;
}

export function themeToCssVars(theme: ThemeTokens): string {
  return (
    ":root{" +
    `--color-primary:${theme.colorPrimary};` +
    `--color-bg:${theme.colorBg};` +
    `--color-text:${theme.colorText};` +
    `--color-muted:${theme.colorMuted};` +
    `--font-sans:${theme.fontSans};` +
    `--radius:${theme.radius};` +
    "}"
  );
}
