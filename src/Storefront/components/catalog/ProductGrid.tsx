import type { ProductHit } from "@/lib/gateway";
import { ProductCard } from "./ProductCard";

export function ProductGrid({ products }: { products: ProductHit[] }) {
  if (products.length === 0) {
    return <p className="text-neutral-500">No products found.</p>;
  }

  return (
    <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
      {products.map((p) => (
        <ProductCard key={p.id} product={p} />
      ))}
    </div>
  );
}
