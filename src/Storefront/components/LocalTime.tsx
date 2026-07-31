"use client";

import { useEffect, useState } from "react";

// Renders a timestamp in the viewer's locale/timezone WITHOUT a hydration mismatch: the server and the
// first client render both emit the stable ISO fallback, then the effect swaps in the localized string
// after mount (client-only). Avoids the classic `new Date().toLocaleString()` SSR≠client error.
export function LocalTime({ iso, dateOnly = false }: { iso: string; dateOnly?: boolean }) {
  const [text, setText] = useState(() => iso.slice(0, dateOnly ? 10 : 16).replace("T", " "));
  useEffect(() => {
    const d = new Date(iso);
    setText(dateOnly ? d.toLocaleDateString() : d.toLocaleString());
  }, [iso, dateOnly]);
  return (
    <time dateTime={iso} suppressHydrationWarning>
      {text}
    </time>
  );
}
