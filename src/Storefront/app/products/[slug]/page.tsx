import Image from "next/image";
import { notFound } from "next/navigation";
import type { Metadata } from "next";
import { getProduct } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { AddToCartButton } from "@/components/cart/AddToCartButton";

// ISR product page (gateway uses revalidate: 300) for SEO at catalog scale (components.md §1).
export async function generateMetadata({
  params,
}: {
  params: Promise<{ slug: string }>;
}): Promise<Metadata> {
  const { slug } = await params;
  const product = await getProduct(slug);
  return product
    ? { title: product.title, description: product.description }
    : { title: "Product not found" };
}

export default async function ProductPage({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params;
  const product = await getProduct(slug);
  if (!product) {
    notFound();
  }

  const fromPrice = product.variants.length
    ? Math.min(...product.variants.map((v) => v.priceMinor))
    : 0;
  const currency = product.variants[0]?.currency ?? process.env.STORE_CURRENCY ?? "EUR";

  return (
    <div className="grid md:grid-cols-2 gap-8">
      <div className="aspect-square bg-neutral-100 relative rounded-lg overflow-hidden">
        {product.imageUrls[0] && (
          <Image
            src={product.imageUrls[0]}
            alt={product.title}
            fill
            sizes="(max-width: 768px) 100vw, 50vw"
            className="object-cover"
            priority
          />
        )}
      </div>

      <div>
        <p className="text-sm text-neutral-500">{product.brand}</p>
        <h1 className="text-2xl font-bold">{product.title}</h1>
        <p className="mt-2 text-xl font-semibold">{formatMoney(fromPrice, currency)}</p>
        <p className="mt-4 text-neutral-700">{product.description}</p>

        <h2 className="mt-6 text-sm font-semibold">Options</h2>
        <ul className="mt-2 space-y-1">
          {product.variants.map((v) => (
            <li key={v.id} className="flex justify-between text-sm border-b border-neutral-100 py-1">
              <span>{v.sku}</span>
              <span className="flex items-center gap-3">
                {formatMoney(v.priceMinor, v.currency)}
                <span className={v.inStock ? "text-green-600" : "text-neutral-400"}>
                  {v.inStock ? "In stock" : "Out of stock"}
                </span>
              </span>
            </li>
          ))}
        </ul>

        {Object.keys(product.attributes).length > 0 && (
          <dl className="mt-6 grid grid-cols-2 gap-2 text-sm">
            {Object.entries(product.attributes).map(([key, value]) => (
              <div key={key} className="flex gap-2">
                <dt className="text-neutral-500 capitalize">{key}:</dt>
                <dd>{value}</dd>
              </div>
            ))}
          </dl>
        )}

        <AddToCartButton productId={product.id} variants={product.variants} />
      </div>
    </div>
  );
}
