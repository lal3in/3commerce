import { getTranslations } from "next-intl/server";
import type { ProductHit } from "@/lib/gateway";
import { ProductCard } from "./ProductCard";

export async function ProductGrid({ products }: { products: ProductHit[] }) {
  if (products.length === 0) {
    const t = await getTranslations("search");
    return <p className="text-neutral-500">{t("empty")}</p>;
  }

  return (
    <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
      {products.map((p) => (
        <ProductCard key={p.id} product={p} />
      ))}
    </div>
  );
}
