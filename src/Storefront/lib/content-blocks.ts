// Content blocks + the bespoke-module seam (mt5_6). A page template is an ordered list of blocks chosen
// from a CLOSED registry of typed components. GOTCHA: a tenant configures blocks + data, never code —
// unknown block types are dropped, and text is rendered as text (never as raw HTML), so there is no
// arbitrary code/markup execution.

export type ContentBlock =
  | { type: "hero"; heading: string; subheading?: string; ctaLabel?: string; ctaHref?: string }
  | { type: "richText"; text: string }
  | { type: "banner"; text: string }
  | { type: "productGrid"; title?: string; category?: string };

export type BlockType = ContentBlock["type"];

export const ALLOWED_BLOCK_TYPES: readonly BlockType[] = ["hero", "richText", "banner", "productGrid"];

export interface Template {
  blocks: ContentBlock[];
}

export function isAllowedBlock(value: unknown): value is ContentBlock {
  return (
    typeof value === "object" &&
    value !== null &&
    "type" in value &&
    typeof (value as { type: unknown }).type === "string" &&
    (ALLOWED_BLOCK_TYPES as readonly string[]).includes((value as { type: string }).type)
  );
}

/** Filter raw (e.g. tenant-config) data down to the blocks our registry knows — the seam is closed. */
export function safeBlocks(raw: unknown): ContentBlock[] {
  if (!Array.isArray(raw)) return [];
  return raw.filter(isAllowedBlock);
}
