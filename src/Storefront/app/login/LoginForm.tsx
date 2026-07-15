"use client";

import Link from "next/link";
import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { login, type AuthState } from "@/lib/auth-actions";

// Client leaf: interactivity (form state) lives at the edge, not in the page (components.md §1).
export function LoginForm() {
  const t = useTranslations("auth");
  const [state, formAction, pending] = useActionState<AuthState, FormData>(login, {});

  return (
    <form action={formAction} className="space-y-3">
      {state.error && (
        <p role="alert" className="rounded bg-red-50 text-red-700 px-3 py-2 text-sm">
          {state.error}
        </p>
      )}
      <div>
        <label htmlFor="email" className="block text-sm font-medium" title={t("tips.email")}>
          {t("email")}
        </label>
        <input
          id="email"
          name="email"
          type="email"
          required
          autoComplete="email"
          title={t("tips.email")}
          aria-describedby="email-tip"
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
        />
        <span id="email-tip" className="sr-only">{t("tips.email")}</span>
      </div>
      <div>
        <label htmlFor="password" className="block text-sm font-medium" title={t("tips.password")}>
          {t("password")}
        </label>
        <input
          id="password"
          name="password"
          type="password"
          required
          autoComplete="current-password"
          title={t("tips.password")}
          aria-describedby="password-tip"
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
        />
        <span id="password-tip" className="sr-only">{t("tips.password")}</span>
      </div>
      {state.mfaRequired && (
        <div>
          <label htmlFor="mfaCode" className="block text-sm font-medium" title={t("tips.mfaCode")}>
            {t("mfaCode")}
          </label>
          <input
            id="mfaCode"
            name="mfaCode"
            inputMode="numeric"
            autoComplete="one-time-code"
            required
            title={t("tips.mfaCode")}
            aria-describedby="mfaCode-tip"
            className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
          />
          <span id="mfaCode-tip" className="sr-only">{t("tips.mfaCode")}</span>
        </div>
      )}
      <button
        type="submit"
        disabled={pending}
        title={t("tips.logIn")}
        className="w-full rounded-md bg-neutral-900 text-white py-2 text-sm font-medium disabled:opacity-50"
      >
        {pending ? t("loggingIn") : t("logIn")}
      </button>
      <p className="text-sm text-neutral-500">
        {t("noAccount")}{" "}
        <Link href="/register" className="underline">
          {t("register")}
        </Link>
      </p>
    </form>
  );
}
