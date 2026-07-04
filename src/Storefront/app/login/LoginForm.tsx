"use client";

import Link from "next/link";
import { useActionState } from "react";
import { login, type AuthState } from "@/lib/auth-actions";

// Client leaf: interactivity (form state) lives at the edge, not in the page (components.md §1).
export function LoginForm() {
  const [state, formAction, pending] = useActionState<AuthState, FormData>(login, {});

  return (
    <form action={formAction} className="space-y-3">
      {state.error && (
        <p role="alert" className="rounded bg-red-50 text-red-700 px-3 py-2 text-sm">
          {state.error}
        </p>
      )}
      <div>
        <label htmlFor="email" className="block text-sm font-medium">
          Email
        </label>
        <input
          id="email"
          name="email"
          type="email"
          required
          autoComplete="email"
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
        />
      </div>
      <div>
        <label htmlFor="password" className="block text-sm font-medium">
          Password
        </label>
        <input
          id="password"
          name="password"
          type="password"
          required
          autoComplete="current-password"
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
        />
      </div>
      {state.mfaRequired && (
        <div>
          <label htmlFor="mfaCode" className="block text-sm font-medium">
            Authenticator code
          </label>
          <input
            id="mfaCode"
            name="mfaCode"
            inputMode="numeric"
            autoComplete="one-time-code"
            required
            className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
          />
        </div>
      )}
      <button
        type="submit"
        disabled={pending}
        className="w-full rounded-md bg-neutral-900 text-white py-2 text-sm font-medium disabled:opacity-50"
      >
        {pending ? "Logging in…" : "Log in"}
      </button>
      <p className="text-sm text-neutral-500">
        No account?{" "}
        <Link href="/register" className="underline">
          Register
        </Link>
      </p>
    </form>
  );
}
