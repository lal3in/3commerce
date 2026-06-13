import { LoginForm } from "./LoginForm";

export const metadata = { title: "Log in" };

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ registered?: string }>;
}) {
  const { registered } = await searchParams;
  return (
    <div className="max-w-sm mx-auto">
      <h1 className="text-xl font-semibold mb-4">Log in</h1>
      {registered && (
        <p className="mb-4 rounded bg-green-50 text-green-700 px-3 py-2 text-sm">
          Account created. Check your email to verify, then log in.
        </p>
      )}
      <LoginForm />
    </div>
  );
}
