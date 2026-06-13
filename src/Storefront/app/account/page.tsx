import { redirect } from "next/navigation";
import { getProfile } from "@/lib/gateway";
import { logout } from "@/lib/auth-actions";

export const metadata = { title: "Account" };

// Dynamic, cookie-dependent page — never cached (components.md §1 rendering table).
export default async function AccountPage() {
  const profile = await getProfile();
  if (!profile) {
    redirect("/login");
  }

  return (
    <div className="max-w-md">
      <h1 className="text-xl font-semibold mb-4">Your account</h1>
      <dl className="space-y-2 text-sm">
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">Email</dt>
          <dd>{profile.email}</dd>
        </div>
        <div className="flex justify-between border-b border-neutral-100 py-2">
          <dt className="text-neutral-500">Email verified</dt>
          <dd>{profile.emailVerified ? "Yes" : "Pending"}</dd>
        </div>
      </dl>

      <p className="mt-6 text-sm text-neutral-500">
        Order history and addresses appear here as later phases land.
      </p>

      <form action={logout} className="mt-6">
        <button type="submit" className="rounded-md border border-neutral-300 px-4 py-2 text-sm">
          Log out
        </button>
      </form>
    </div>
  );
}
