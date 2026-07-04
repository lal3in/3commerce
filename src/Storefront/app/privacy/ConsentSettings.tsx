"use client";

import { useEffect, useState } from "react";
import { readConsent, writeConsent } from "@/lib/consent";
import { clearFirstPartyIds } from "@/lib/visitor";

// Client leaf: mirrors the ConsentBanner semantics — writeConsent broadcasts the change and
// withdrawing analytics drops the first-party ids (components.md §1).
export function ConsentSettings() {
  const [analytics, setAnalytics] = useState(false);
  const [marketing, setMarketing] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    const state = readConsent();
    setAnalytics(state.analytics);
    setMarketing(state.marketing);
  }, []);

  function save() {
    writeConsent({ analytics, marketing });
    if (!analytics) clearFirstPartyIds();
    setSaved(true);
  }

  return (
    <div className="mt-6 space-y-4">
      <label className="flex items-start gap-3 rounded-md border border-neutral-200 p-4">
        <input type="checkbox" checked readOnly disabled className="mt-1" aria-label="Necessary (always on)" />
        <span>
          <span className="block text-sm font-medium text-neutral-400">Necessary — always on</span>
          <span className="block text-sm text-neutral-500">Session, cart, and checkout cookies the store cannot work without.</span>
        </span>
      </label>
      <label className="flex items-start gap-3 rounded-md border border-neutral-200 p-4">
        <input
          type="checkbox"
          checked={analytics}
          onChange={(e) => {
            setAnalytics(e.target.checked);
            setSaved(false);
          }}
          className="mt-1"
        />
        <span>
          <span className="block text-sm font-medium">Analytics</span>
          <span className="block text-sm text-neutral-500">
            First-party, batched page events — no third-party trackers, no session replay. Off by default.
          </span>
        </span>
      </label>
      <label className="flex items-start gap-3 rounded-md border border-neutral-200 p-4">
        <input
          type="checkbox"
          checked={marketing}
          onChange={(e) => {
            setMarketing(e.target.checked);
            setSaved(false);
          }}
          className="mt-1"
        />
        <span>
          <span className="block text-sm font-medium">Marketing</span>
          <span className="block text-sm text-neutral-500">Campaign attribution for offers you arrive through.</span>
        </span>
      </label>
      <button
        onClick={save}
        className="rounded-md bg-neutral-900 px-4 py-2 text-sm font-medium text-white"
      >
        Save choices
      </button>
      {saved && (
        <p role="status" className="text-sm text-green-700">
          Saved. Your choices apply immediately.
        </p>
      )}
    </div>
  );
}
