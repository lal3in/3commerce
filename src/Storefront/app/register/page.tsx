import { getTranslations } from "next-intl/server";
import { RegisterForm } from "./RegisterForm";

export const metadata = { title: "Register" };

export default async function RegisterPage() {
  const t = await getTranslations("auth");
  return (
    <div className="max-w-sm mx-auto">
      <h1 className="text-xl font-semibold mb-4">{t("registerTitle")}</h1>
      <RegisterForm />
    </div>
  );
}
