import { cookies } from "next/headers";
import { getAddresses, type AddressDto, type CartDto } from "@/lib/gateway";

/**
 * The shipping basis the cart preview scores promotions against (ADR-0051).
 *
 * A free-shipping promotion is worth exactly the shipping amount, so a preview that guesses the amount
 * can pick a different winner than checkout. The fix is to stop guessing: whenever a shipping address is
 * known — a signed-in shopper's default one, or the one a guest already entered at checkout — quote the
 * SAME Fulfillment carrier endpoint whose amount rides the checkout POST as `selectedShippingAmountMinor`,
 * and hand Ordering that rate. Ordering then evaluates the promotion contest on the amount it will charge.
 *
 * When no address is known at all, `shippingMinor` is null and Ordering returns a PROVISIONAL verdict
 * rather than committing to a winner that could flip.
 */
export type ShippingBasis = {
  /** The quoted rate for the destination, or null when no address is known / the carrier could not quote. */
  shippingMinor: number | null;
  /** ISO 3166-1 alpha-2 destination, when known. Lets Ordering apply per-country ship rules (ADR-0050). */
  shipToCountry: string | null;
};

export const UNKNOWN_SHIPPING: ShippingBasis = { shippingMinor: null, shipToCountry: null };

/** Session cookie holding the destination a guest already entered at checkout. HttpOnly; never read by JS. */
export const SHIP_TO_COOKIE = "3c_ship_to";

export type ShipDestination = {
  name: string;
  line1: string;
  city: string;
  postcode: string;
  country: string;
};

/**
 * Origin + parcel for a quote. Shared by the cart preview and the checkout rate picker ON PURPOSE: two
 * quotes for the same cart and destination must be the same request, or the preview would score against
 * a rate checkout never offers. (Both are placeholders until per-supplier origins and real parcel
 * dimensions land — mt4_11 already defaults an unmapped parcel server-side.)
 */
export const QUOTE_ORIGIN: ShipDestination = {
  name: "3commerce warehouse",
  line1: "1 Warehouse Way",
  city: "Sydney",
  postcode: "2000",
  country: "AU",
};
export const QUOTE_PARCEL = { weightGrams: 500, lengthMm: 200, widthMm: 150, heightMm: 100 };

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";
/** A quote is reused for this long. Short enough that a carrier price move surfaces on the next view. */
const QUOTE_TTL_MS = 5 * 60 * 1000;
/** Bounded so a busy store cannot grow the process cache without limit; oldest entries are evicted. */
const QUOTE_CACHE_MAX = 500;

type CacheEntry = { amountMinor: number | null; expiresAt: number };
const quoteCache = new Map<string, CacheEntry>();

export function isCompleteDestination(destination: Partial<ShipDestination> | null | undefined): destination is ShipDestination {
  return Boolean(destination?.line1 && destination.city && destination.postcode && destination.country?.length === 2);
}

/**
 * Remember the destination a shopper quoted with, so the CART preview can quote the same one on the next
 * render. A guest has no saved address — this cookie is the only way the preview learns where the parcel
 * is going, and the shipping address is required for anything that actually ships, so it is available as
 * soon as the shopper fills the checkout form. Session-scoped and HttpOnly: it disappears with the browser
 * session and script on the page can never read it.
 */
export async function rememberShipDestination(destination: ShipDestination): Promise<void> {
  const jar = await cookies();
  jar.set(SHIP_TO_COOKIE, JSON.stringify(destination), {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    path: "/",
  });
}

/** The destination a guest already entered at checkout, if any. Malformed cookies are simply ignored. */
export async function rememberedShipDestination(): Promise<ShipDestination | null> {
  const raw = (await cookies()).get(SHIP_TO_COOKIE)?.value;
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<ShipDestination>;
    return isCompleteDestination(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/** The signed-in shopper's default shipping address, if they have one. */
export function defaultShippingAddress(addresses: AddressDto[]): ShipDestination | null {
  const match =
    addresses.find((a) => a.isDefault && (a.purpose === "Shipping" || a.purpose === "Both")) ??
    addresses.find((a) => a.purpose === "Shipping" || a.purpose === "Both");
  return match && isCompleteDestination(match) ? { name: match.name, line1: match.line1, city: match.city, postcode: match.postcode, country: match.country } : null;
}

/**
 * The destination for the cart preview: whatever the shopper most recently quoted with (a guest's entered
 * address wins — it is the freshest statement of where this order is going), else their saved default.
 * Signing in is NOT a precondition: an entered address counts exactly as much as a saved one.
 */
export async function resolveShipDestination(): Promise<ShipDestination | null> {
  const remembered = await rememberedShipDestination();
  if (remembered) return remembered;
  // Only ask Identity when there is a session to ask about — a guest has no saved addresses, and the
  // cart is rendered on every page view.
  const signedIn = Boolean((await cookies()).get("3c_session")?.value);
  return signedIn ? defaultShippingAddress(await getAddresses()) : null;
}

/**
 * Signature of everything a quote depends on. The cache key includes it, so a changed cart or a changed
 * destination simply misses the cache — there is no stale rate to invalidate.
 */
function quoteKey(storefrontId: string | null, destination: ShipDestination, cart: CartDto): string {
  const contents = cart.items
    .map((i) => `${i.productId}:${i.variantId ?? ""}:${i.quantity}:${i.unitPriceMinor}`)
    .sort()
    .join("|");
  return JSON.stringify([storefrontId, destination.country, destination.postcode, destination.city, destination.line1, cart.currency, contents]);
}

/**
 * The cheapest carrier rate for this cart and destination — the same rate the checkout form preselects
 * (`rates[0]`), so the preview scores against the amount checkout will actually charge.
 *
 * Cached per (storefront, cart contents, destination) for a short window so re-rendering the cart does not
 * produce a quote storm. A carrier that cannot quote yields null rather than an error: the preview then
 * falls back to the provisional verdict and the cart still renders.
 */
export async function quoteCartShipping(
  storefrontId: string | null,
  destination: ShipDestination,
  cart: CartDto,
): Promise<number | null> {
  const key = quoteKey(storefrontId, destination, cart);
  const cached = quoteCache.get(key);
  if (cached && cached.expiresAt > Date.now()) {
    return cached.amountMinor;
  }

  let amountMinor: number | null = null;
  try {
    const response = await fetch(`${GATEWAY_URL}/api/fulfillment/shipping/quote`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        storefrontId,
        destination,
        origin: QUOTE_ORIGIN,
        parcel: QUOTE_PARCEL,
      }),
      cache: "no-store",
    });
    if (response.ok) {
      const body = (await response.json()) as { rates: { amountMinor: number }[] };
      amountMinor = body.rates[0]?.amountMinor ?? null;
    }
  } catch {
    // A carrier outage must never break the cart: fall through with no rate.
    amountMinor = null;
  }

  if (quoteCache.size >= QUOTE_CACHE_MAX) {
    const oldest = quoteCache.keys().next();
    if (!oldest.done) quoteCache.delete(oldest.value);
  }

  quoteCache.set(key, { amountMinor, expiresAt: Date.now() + QUOTE_TTL_MS });
  return amountMinor;
}


/**
 * The full resolution used by the cart and checkout pages: find a destination, quote it, and report both
 * to Ordering. No destination (a shopper who has never entered one) → nothing is asserted and Ordering
 * decides whether the outcome is settled anyway or genuinely provisional.
 */
export async function resolveShippingBasis(cart: CartDto, storefrontId: string | null): Promise<ShippingBasis> {
  if (cart.items.length === 0) return UNKNOWN_SHIPPING;
  const destination = await resolveShipDestination();
  if (!destination) return UNKNOWN_SHIPPING;
  return {
    shippingMinor: await quoteCartShipping(storefrontId, destination, cart),
    // The country stands even when the quote failed: it still lets Ordering apply per-country ship rules,
    // and a cart whose shipping is already covered pays 0 regardless of any carrier rate.
    shipToCountry: destination.country.toUpperCase(),
  };
}
