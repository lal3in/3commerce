"use client";

import { useActionState, useEffect, useState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { convertGuest, type ConvertState } from "@/lib/cart-actions";
import { MemberFields } from "@/components/account/MemberFields";

// Pending-first (components.md §2): show the saga state, then confirm via polling.
// In dev without a Stripe publishable key, a button completes a simulated test payment.
export function ConfirmationView({
  orderId,
  initialStatus,
  guestEmail,
  guestName,
  guestPhone,
  isAuthenticated,
}: {
  orderId: string;
  initialStatus: string;
  guestEmail: string;
  guestName?: string;
  guestPhone?: string;
  isAuthenticated: boolean;
}) {
  const t = useTranslations("confirmation");
  // Split the shipping name the guest typed into first/last to pre-fill the account offer.
  const parts = (guestName ?? "").trim().split(/\s+/).filter(Boolean);
  const firstName = parts[0] ?? "";
  const lastName = parts.length > 1 ? parts.slice(1).join(" ") : "";
  const [status, setStatus] = useState(initialStatus);
  const [paying, setPaying] = useState(false);
  const [convert, convertAction, converting] = useActionState<ConvertState, FormData>(convertGuest, {});

  useEffect(() => {
    if (status === "Confirmed" || status === "Cancelled") return;
    const timer = setInterval(async () => {
      const res = await fetch(`/api/order-status/${orderId}`, { cache: "no-store" });
      if (res.ok) {
        const data = (await res.json()) as { status: string };
        setStatus(data.status);
      }
    }, 2000);
    return () => clearInterval(timer);
  }, [orderId, status]);

  if (status === "Confirmed") {
    return (
      <div className="max-w-md mx-auto text-center py-12 space-y-4">
        <h1 className="text-2xl font-bold">{t("thankYou")}</h1>
        <p className="text-neutral-600">{t("confirmed")}</p>
        {/* FR-7: guests can convert after checkout; authenticated orders are attached by Ordering.UserId. */}
        {!isAuthenticated && (
          <div className="rounded-md border border-neutral-200 p-4 text-left text-sm">
            {convert.ok ? (
              <p className="text-green-700">
                {t.rich("accountCreated", {
                  link: (chunks) => (
                    <Link href="/account" className="underline">
                      {chunks}
                    </Link>
                  ),
                })}
              </p>
            ) : (
              <form action={convertAction} className="space-y-3">
                <p className="font-medium">{t("createAccountPrompt")}</p>
                {convert.error && <p className="text-red-600">{convert.error}</p>}
                <input
                  name="email"
                  type="email"
                  required
                  defaultValue={guestEmail}
                  placeholder={t("email")}
                  aria-label={t("email")}
                  title={t("tips.email")}
                  aria-describedby="convert-email-tip"
                  className="w-full rounded border border-neutral-300 px-3 py-2"
                />
                <span id="convert-email-tip" className="sr-only">{t("tips.email")}</span>
                <input
                  name="password"
                  type="password"
                  required
                  minLength={10}
                  placeholder={t("password")}
                  aria-label={t("password")}
                  title={t("tips.password")}
                  aria-describedby="convert-password-tip"
                  className="w-full rounded border border-neutral-300 px-3 py-2"
                />
                <span id="convert-password-tip" className="sr-only">{t("tips.password")}</span>
                {/* Pre-filled from what you entered at checkout so the form is not empty (mem_1). */}
                <MemberFields defaults={{ firstName, lastName, phone: guestPhone }} />
                <button
                  type="submit"
                  disabled={converting}
                  title={t("tips.createAccount")}
                  className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm disabled:opacity-50"
                >
                  {converting ? t("creating") : t("createAccount")}
                </button>
              </form>
            )}
          </div>
        )}
        {isAuthenticated && (
          <p className="rounded-md border border-green-200 bg-green-50 p-3 text-sm text-green-800">
            {t("attachedToAccount")}
          </p>
        )}
        {/* BL-5: make the order support / refund flow discoverable. */}
        <p className="text-sm text-neutral-500">
          {t.rich("problemPrompt", {
            link: (chunks) => (
              <Link href={`/orders/${orderId}/support`} className="underline">
                {chunks}
              </Link>
            ),
          })}
        </p>
      </div>
    );
  }

  if (status === "Cancelled") {
    return (
      <div className="max-w-md mx-auto text-center py-12">
        <h1 className="text-xl font-semibold">{t("cancelledTitle")}</h1>
        <Link href="/cart" className="mt-4 inline-block underline">
          {t("returnToCart")}
        </Link>
      </div>
    );
  }

  return (
    <div className="max-w-md mx-auto text-center py-12 space-y-4">
      <h1 className="text-xl font-semibold">{t("pendingTitle")}</h1>
      <p className="text-neutral-500 text-sm">{t("orderId", { orderId })}</p>
      <button
        type="button"
        disabled={paying}
        title={t("tips.testPayment")}
        onClick={async () => {
          setPaying(true);
          await fetch(`/api/dev-pay/${orderId}`, { method: "POST" });
        }}
        className="rounded-md bg-neutral-900 text-white px-4 py-2 text-sm font-medium disabled:opacity-50"
      >
        {paying ? t("processing") : t("testPayment")}
      </button>
    </div>
  );
}
