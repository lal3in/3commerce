import Link from "next/link";
import type { ProductHit } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { SafeImage } from "@/components/SafeImage";
import { productTypeClasses, productTypeInfo } from "@/lib/product-type";

// Server Component (default): no interactivity, just renders props (components.md §1).
export function ProductCard({ product }: { product: ProductHit }) {
  return (
    <Link
      href={`/products/${product.slug}`}
      className="group block rounded-lg border border-neutral-200 overflow-hidden hover:shadow-md transition-shadow"
    >
      <div className="aspect-square bg-neutral-100 relative">
        {product.imageUrl && (
          <SafeImage
            src={product.imageUrl}
            alt={product.title}
            fill
            sizes="(max-width: 768px) 50vw, 25vw"
            className="object-cover"
          />
        )}
        <span
          className={`absolute left-2 top-2 rounded px-1.5 py-0.5 text-[11px] font-medium ${productTypeClasses(product.productType)}`}
        >
          {productTypeInfo(product.productType).badge}
        </span>
      </div>
      <div className="p-3">
        <p className="text-xs text-neutral-500">{product.brand}</p>
        <h3 className="text-sm font-medium line-clamp-2 group-hover:underline">{product.title}</h3>
        <p className="mt-1 text-sm font-semibold">
          {formatMoney(product.minPriceMinor, product.currency)}
        </p>
      </div>
    </Link>
  );
}
