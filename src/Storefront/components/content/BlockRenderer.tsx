import Link from "next/link";
import type { ContentBlock } from "@/lib/content-blocks";

// Renders configured content blocks (mt5_6). Each block maps to a fixed component from the closed
// registry; an unknown type renders nothing. Text fields are rendered as text (React escapes them),
// so a tenant can never inject markup/script (GOTCHA).
export function BlockRenderer({ blocks }: { blocks: ContentBlock[] }) {
  return (
    <>
      {blocks.map((block, index) => (
        <Block key={index} block={block} />
      ))}
    </>
  );
}

function Block({ block }: { block: ContentBlock }) {
  switch (block.type) {
    case "hero":
      return (
        <section className="rounded-[var(--radius)] bg-[var(--color-bg)] px-6 py-12 text-center">
          <h2 className="text-3xl font-bold" style={{ color: "var(--color-text)" }}>
            {block.heading}
          </h2>
          {block.subheading && <p className="mt-2" style={{ color: "var(--color-muted)" }}>{block.subheading}</p>}
          {block.ctaLabel && block.ctaHref && (
            <Link
              href={block.ctaHref}
              className="mt-4 inline-block rounded-[var(--radius)] px-4 py-2 text-white"
              style={{ background: "var(--color-primary)" }}
            >
              {block.ctaLabel}
            </Link>
          )}
        </section>
      );
    case "richText":
      return <p className="whitespace-pre-line">{block.text}</p>;
    case "banner":
      return (
        <div className="rounded-[var(--radius)] px-4 py-2 text-sm text-white" style={{ background: "var(--color-primary)" }}>
          {block.text}
        </div>
      );
    case "productGrid":
      return (
        <section>
          {block.title && <h2 className="text-lg font-semibold">{block.title}</h2>}
          <p style={{ color: "var(--color-muted)" }}>Product grid{block.category ? ` — ${block.category}` : ""}</p>
        </section>
      );
    default:
      return null; // unknown block type: render nothing (closed registry)
  }
}
