"use client";

import Link from "next/link";
import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { register, type AuthState } from "@/lib/auth-actions";
import { MemberFields } from "@/components/account/MemberFields";

export function RegisterForm() {
  const t = useTranslations("auth");
  const [state, formAction, pending] = useActionState<AuthState, FormData>(register, {});

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
        <label htmlFor="password" className="block text-sm font-medium" title={t("tips.passwordMin")}>
          {t("passwordMin")}
        </label>
        <input
          id="password"
          name="password"
          type="password"
          required
          minLength={10}
          autoComplete="new-password"
          title={t("tips.passwordMin")}
          aria-describedby="password-tip"
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
        />
        <span id="password-tip" className="sr-only">{t("tips.passwordMin")}</span>
      </div>
      <fieldset className="border-t border-neutral-200 pt-3">
        <legend className="text-sm font-medium text-neutral-700">{t("yourDetails")}</legend>
        <MemberFields />
      </fieldset>
      <button
        type="submit"
        disabled={pending}
        title={t("tips.createAccount")}
        className="w-full rounded-md bg-neutral-900 text-white py-2 text-sm font-medium disabled:opacity-50"
      >
        {pending ? t("creating") : t("createAccount")}
      </button>
      <p className="text-sm text-neutral-500">
        {t("haveAccount")}{" "}
        <Link href="/login" className="underline">
          {t("logIn")}
        </Link>
      </p>
    </form>
  );
}
