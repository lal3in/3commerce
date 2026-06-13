import { RegisterForm } from "./RegisterForm";

export const metadata = { title: "Register" };

export default function RegisterPage() {
  return (
    <div className="max-w-sm mx-auto">
      <h1 className="text-xl font-semibold mb-4">Create an account</h1>
      <RegisterForm />
    </div>
  );
}
