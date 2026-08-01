"use client";

import { useState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { OrderSummaryDto, OrderDetail } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { LocalTime } from "@/components/LocalTime";

// Order history: each row links to Support and expands (fetched on demand) to the full order detail —
// date, shipping address, payment, items and the amount breakdown.
export function OrderHistory({ orders }: { orders: OrderSummaryDto[] }) {
  const t = useTranslations("account");
  return (
    <ul className="mt-2 divide-y divide-neutral-100 text-sm">
      {orders.map((o) => (
        <OrderRow key={o.id} order={o} t={t} />
      ))}
    </ul>
  );
}

function OrderRow({ order, t }: { order: OrderSummaryDto; t: ReturnType<typeof useTranslations> }) {
  const [open, setOpen] = useState(false);
  const [detail, setDetail] = useState<OrderDetail | null>(null);
  const [loading, setLoading] = useState(false);

  async function toggle() {
    const next = !open;
    setOpen(next);
    if (next && detail === null && !loading) {
      setLoading(true);
      try {
        const res = await fetch(`/api/order-detail/${order.id}`, { cache: "no-store" });
        if (res.ok) setDetail((await res.json()) as OrderDetail);
      } catch {
        /* leave detail null → shows the unavailable note */
      } finally {
        setLoading(false);
      }
    }
  }

  return (
    <li className="py-2">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <button
          type="button"
          onClick={toggle}
          aria-expanded={open}
          title={open ? t("tips.hideOrderDetails") : t("tips.orderDetails")}
          className="inline-flex items-center gap-1 text-neutral-500"
        >
          <span aria-hidden>{open ? "▾" : "▸"}</span>
          <span className="font-mono text-xs">{order.id.slice(0, 8)}…</span>
        </button>
        <span>{order.status}</span>
        <span className="ml-auto">{formatMoney(order.grossMinor, order.currency)}</span>
        <button type="button" onClick={toggle} className="underline text-neutral-500" title={t("tips.orderDetails")}>
          {t("orderDetails")}
        </button>
        <Link href={`/orders/${order.id}/support`} title={t("tips.support")} className="underline text-neutral-500">
          {t("support")}
        </Link>
      </div>

      {open && (
        <div className="mt-2 rounded-md border border-neutral-200 bg-neutral-50 p-3 text-xs">
          {loading && detail === null ? (
            <p className="text-neutral-500">{t("orderDetailsLoading")}</p>
          ) : detail === null ? (
            <p className="text-amber-700">{t("orderDetailsUnavailable")}</p>
          ) : (
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-x-4 gap-y-1">
                <span className="text-neutral-500">{t("orderNumber")}</span>
                <span className="text-right font-mono">#{detail.publicOrderNumber}</span>
                <span className="text-neutral-500">{t("orderDate")}</span>
                <span className="text-right"><LocalTime iso={detail.createdAt} /></span>
                <span className="text-neutral-500">{t("orderStatus")}</span>
                <span className="text-right">{detail.status}{detail.partiallyRefunded ? ` · ${t("partiallyRefunded")}` : ""}</span>
                <span className="text-neutral-500">{t("payment")}</span>
                <span className="text-right">
                  {detail.paymentOption}{detail.paymentInstrumentSummary ? ` · ${detail.paymentInstrumentSummary}` : ""} ({detail.paymentProvider})
                </span>
              </div>

              {detail.shippingAddress && (
                <div>
                  <div className="mb-1 font-medium text-neutral-700">{t("shippingAddress")}</div>
                  <div className="text-neutral-600">
                    {detail.shippingAddress.name}<br />
                    {detail.shippingAddress.line1}<br />
                    {detail.shippingAddress.city} {detail.shippingAddress.postcode}, {detail.shippingAddress.country}
                  </div>
                </div>
              )}

              <div>
                <div className="mb-1 font-medium text-neutral-700">{t("items")}</div>
                <ul className="space-y-1">
                  {detail.lines.map((l, i) => (
                    <li key={i} className="flex items-center gap-2 text-neutral-700">
                      <span className="tabular-nums text-neutral-500">{l.quantity}×</span>
                      <span className="flex-1">{l.title}{l.variantSku ? ` (${l.variantSku})` : ""}</span>
                      <span className="text-neutral-500">{formatMoney(l.unitPriceMinor, detail.currency)}</span>
                    </li>
                  ))}
                </ul>
              </div>

              <div className="grid grid-cols-2 gap-x-4 gap-y-1 border-t border-neutral-200 pt-2">
                <span className="text-neutral-500">{t("subtotal")}</span>
                <span className="text-right">{formatMoney(detail.netMinor, detail.currency)}</span>
                {detail.discountMinor > 0 && (
                  <>
                    <span className="text-neutral-500">{t("discount")}</span>
                    <span className="text-right">−{formatMoney(detail.discountMinor, detail.currency)}</span>
                  </>
                )}
                <span className="text-neutral-500">{t("shipping")}</span>
                <span className="text-right">{formatMoney(detail.shippingMinor, detail.currency)}</span>
                <span className="text-neutral-500">{t("tax")}</span>
                <span className="text-right">{formatMoney(detail.taxMinor, detail.currency)}</span>
                <span className="font-medium text-neutral-800">{t("total")}</span>
                <span className="text-right font-medium">{formatMoney(detail.grossMinor, detail.currency)}</span>
              </div>
            </div>
          )}
        </div>
      )}
    </li>
  );
}
