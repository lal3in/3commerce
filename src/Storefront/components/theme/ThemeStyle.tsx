import { themeToCssVars, type ThemeTokens } from "@/lib/theme";

// Server component (mt5_6): emits the resolved theme tokens as :root CSS variables. Values are already
// sanitized by mergeTheme, so this dangerouslySetInnerHTML carries only safe tokens.
export function ThemeStyle({ theme }: { theme: ThemeTokens }) {
  return <style dangerouslySetInnerHTML={{ __html: themeToCssVars(theme) }} />;
}
