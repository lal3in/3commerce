"use server";

import { cookies } from "next/headers";
import { revalidatePath } from "next/cache";
import { GATEWAY_URL } from "./gateway";

export type ReviewState = { error?: string; ok?: boolean };

// Submit (or update) the signed-in customer's review for a product. The gateway forwards the session;
// the Catalog service enforces "verified customer only" — this is not something the client can fake.
export async function submitReview(_prev: ReviewState, formData: FormData): Promise<ReviewState> {
  const productId = String(formData.get("productId"));
  const slug = String(formData.get("slug"));
  const rating = Number(formData.get("rating"));
  const comment = String(formData.get("comment") ?? "").trim();
  const authorName = String(formData.get("authorName") ?? "").trim();
  if (!(rating >= 1 && rating <= 5)) return { error: "Pick a star rating." };

  const store = await cookies();
  const session = store.get("3c_session");
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (session) headers["cookie"] = `3c_session=${session.value}`;

  const res = await fetch(`${GATEWAY_URL}/api/catalog/products/${productId}/reviews`, {
    method: "POST",
    headers,
    body: JSON.stringify({ rating, comment: comment || null, authorName: authorName || null }),
  });
  if (res.status === 401 || res.status === 403) return { error: "Only verified, signed-in customers can leave a review." };
  if (!res.ok) return { error: "Could not submit your review." };
  revalidatePath(`/products/${slug}`);
  return { ok: true };
}

// Post a REPLY to an existing review/comment (parentId set) or a top-level product COMMENT (no parentId).
// Neither carries a rating; both require a message. Same verified-customer gate as a review.
export async function submitReply(_prev: ReviewState, formData: FormData): Promise<ReviewState> {
  const productId = String(formData.get("productId"));
  const slug = String(formData.get("slug"));
  const parentId = String(formData.get("parentId") ?? "").trim() || null;
  const comment = String(formData.get("comment") ?? "").trim();
  const authorName = String(formData.get("authorName") ?? "").trim();
  if (!comment) return { error: parentId ? "Write a reply." : "Write a comment." };

  const store = await cookies();
  const session = store.get("3c_session");
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (session) headers["cookie"] = `3c_session=${session.value}`;

  const res = await fetch(`${GATEWAY_URL}/api/catalog/products/${productId}/reviews`, {
    method: "POST",
    headers,
    body: JSON.stringify({ rating: null, comment, authorName: authorName || null, parentId }),
  });
  if (res.status === 401 || res.status === 403) return { error: "Only verified, signed-in customers can post here." };
  if (!res.ok) return { error: parentId ? "Could not post your reply." : "Could not post your comment." };
  revalidatePath(`/products/${slug}`);
  return { ok: true };
}
