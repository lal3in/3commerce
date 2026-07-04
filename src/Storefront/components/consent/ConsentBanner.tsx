"use client";

import { useEffect, useState } from "react";
import { consentDecided, readConsent, writeConsent } from "@/lib/consent";
import { clearFirstPartyIds } from "@/lib/visitor";

export default function ConsentBanner() {
  const [show, setShow] = useState(false);
  const [analytics, setAnalytics] = useState(false);
  const [marketing, setMarketing] = useState(false);

  useEffect(() => {
    const state = readConsent();
    setAnalytics(state.analytics);
    setMarketing(state.marketing);
    setShow(!consentDecided(state));
  }, []);

  if (!show) return null;

  function save(nextAnalytics: boolean, nextMarketing: boolean) {
    writeConsent({ analytics: nextAnalytics, marketing: nextMarketing });
    if (!nextAnalytics) clearFirstPartyIds(); // withdrawing analytics drops first-party ids
    setShow(false);
  }

  return (
    <div
      role="dialog"
      aria-label="Cookie consent"
      className="fixed inset-x-0 bottom-0 z-50 border-t border-neutral-200 bg-white p-4 shadow-lg"
    >
      <div className="mx-auto flex max-w-6xl flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div className="text-sm text-neutral-700">
          <p className="font-medium">We use cookies</p>
          <p className="text-neutral-500">
            Necessary cookies keep the store working. Analytics and marketing are optional and off until you allow them.
          </p>
          <div className="mt-2 flex flex-wrap gap-4">
            <label className="flex items-center gap-1 text-neutral-400">
              <input type="checkbox" checked readOnly disabled aria-label="Necessary (always on)" /> Necessary
            </label>
            <label className="flex items-center gap-1">
              <input type="checkbox" checked={analytics} onChange={(e) => setAnalytics(e.target.checked)} /> Analytics
            </label>
            <label className="flex items-center gap-1">
              <input type="checkbox" checked={marketing} onChange={(e) => setMarketing(e.target.checked)} /> Marketing
            </label>
            <a href="/privacy" className="text-neutral-500 underline">
              Privacy settings
            </a>
          </div>
        </div>
        <div className="flex shrink-0 flex-wrap gap-2">
          <button
            type="button"
            onClick={() => save(false, false)}
            className="rounded-md border border-neutral-300 px-3 py-2 text-sm hover:bg-neutral-50"
          >
            Reject non-essential
          </button>
          <button
            type="button"
            onClick={() => save(analytics, marketing)}
            className="rounded-md border border-neutral-300 px-3 py-2 text-sm hover:bg-neutral-50"
          >
            Save choices
          </button>
          <button
            type="button"
            onClick={() => save(true, true)}
            className="rounded-md bg-neutral-900 px-3 py-2 text-sm text-white hover:bg-neutral-700"
          >
            Accept all
          </button>
        </div>
      </div>
    </div>
  );
}
