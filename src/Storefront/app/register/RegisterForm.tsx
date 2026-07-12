"use client";

import Link from "next/link";
import { useActionState } from "react";
import { register, type AuthState } from "@/lib/auth-actions";
import { MemberFields } from "@/components/account/MemberFields";

export function RegisterForm() {
  const [state, formAction, pending] = useActionState<AuthState, FormData>(register, {});

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
          Password (min 10 characters)
        </label>
        <input
          id="password"
          name="password"
          type="password"
          required
          minLength={10}
          autoComplete="new-password"
          className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
        />
      </div>
      <fieldset className="border-t border-neutral-200 pt-3">
        <legend className="text-sm font-medium text-neutral-700">Your details</legend>
        <MemberFields />
      </fieldset>
      <button
        type="submit"
        disabled={pending}
        className="w-full rounded-md bg-neutral-900 text-white py-2 text-sm font-medium disabled:opacity-50"
      >
        {pending ? "Creating…" : "Create account"}
      </button>
      <p className="text-sm text-neutral-500">
        Already have an account?{" "}
        <Link href="/login" className="underline">
          Log in
        </Link>
      </p>
    </form>
  );
}
