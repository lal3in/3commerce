"use client";

import { useEffect, useState } from "react";

/** Throws during a render after hydration — a browser-side error the route's error boundary must catch. */
export function ClientErrorProbe() {
  const [armed, setArmed] = useState(false);
  useEffect(() => setArmed(true), []);
  if (armed) throw new Error("Deliberate storefront error probe (browser render)");
  return <p>…</p>;
}
