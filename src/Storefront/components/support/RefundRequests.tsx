"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import type { CustomerRma } from "@/lib/gateway";
import { LocalTime } from "@/components/LocalTime";
import { formatMoney } from "@/lib/money";

// RMA saga state → { i18n status key, badge colour }. A distinct colour per lifecycle stage so the
// customer can see at a glance where their refund is. Unknown states fall back to "Requested".
const STATUS: Record<string, { key: string; cls: string }> = {
  Requested: { key: "status.requested", cls: "border-amber-200 bg-amber-50 text-amber-700" },
  AwaitingReturn: { key: "status.awaitingReturn", cls: "border-sky-200 bg-sky-50 text-sky-700" },
  RefundPending: { key: "status.processing", cls: "border-indigo-200 bg-indigo-50 text-indigo-700" },
  Denied: { key: "status.denied", cls: "border-red-200 bg-red-50 text-red-700" },
  RefundIssued: { key: "status.refunded", cls: "border-green-200 bg-green-50 text-green-700" },
};

// The customer's refund/return requests for this order — a collapsed list; click a row's chevron to
// expand its lines. Polls so status changes (approved, denied, refunded) appear without a refresh.
export function RefundRequests({ orderId, refunds: initial }: { orderId: string; refunds: CustomerRma[] }) {
  const t = useTranslations("refunds");
  const [refunds, setRefunds] = useState(initial);
  const [openId, setOpenId] = useState<string | null>(null);

  useEffect(() => setRefunds(initial), [initial]);

  useEffect(() => {
    let alive = true;
    const poll = async () => {
      if (document.visibilityState !== "visible") return;
      try {
        const res = await fetch(`/api/order-refunds/${orderId}`, { cache: "no-store" });
        if (res.ok && alive) setRefunds((await res.json()) as CustomerRma[]);
      } catch {
        /* transient — try again next tick */
      }
    };
    const id = setInterval(poll, 12000);
    return () => {
      alive = false;
      clearInterval(id);
    };
  }, [orderId]);

  if (refunds.length === 0) return null;
  return (
    <section className="space-y-3 rounded-md border border-neutral-200 p-4">
      <h2 className="font-medium">{t("title")}</h2>
      <ul className="space-y-2">
        {refunds.map((r) => {
          const s = STATUS[r.state] ?? STATUS.Requested;
          const open = openId === r.id;
          return (
            <li key={r.id} className="rounded-md border border-neutral-100 bg-neutral-50">
              <button
                type="button"
                onClick={() => setOpenId(open ? null : r.id)}
                aria-expanded={open}
                title={open ? t("collapse") : t("expand")}
                className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm"
              >
                <span aria-hidden className="text-neutral-400">{open ? "▾" : "▸"}</span>
                <span className="font-medium">{formatMoney(r.amountMinor, r.currency)}</span>
                <span className={`inline-block rounded-full border px-2 py-0.5 text-xs font-medium ${s.cls}`}>
                  {t(s.key)}
                </span>
                <span className="ml-auto text-xs text-neutral-400">
                  <LocalTime iso={r.createdAt} dateOnly />
                </span>
              </button>
              {open && (
                <div className="space-y-2 border-t border-neutral-100 px-3 py-2 text-sm">
                  <ul className="space-y-1">
                    {r.lines.map((l) => (
                      <li key={l.productId} className="flex items-center gap-2 text-neutral-700">
                        <span className="tabular-nums text-neutral-500">{l.quantity}×</span>
                        <span className="flex-1">{l.title}</span>
                        <span className="text-neutral-500">{formatMoney(l.unitPriceMinor, r.currency)}</span>
                      </li>
                    ))}
                  </ul>
                  <p className="text-xs text-neutral-500">
                    {t("reasonLabel")}: <span className="text-neutral-700">{r.reason}</span>
                  </p>
                </div>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
