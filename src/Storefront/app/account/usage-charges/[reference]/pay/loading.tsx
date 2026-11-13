// Skeleton matching the final layout (title, summary rows, button) — no layout shift (components.md §5).
export default function Loading() {
  return (
    <div className="max-w-xl animate-pulse" aria-busy="true">
      <div className="mb-4 h-6 w-2/3 rounded bg-neutral-200" />
      <div className="space-y-3">
        <div className="h-8 rounded bg-neutral-100" />
        <div className="h-8 rounded bg-neutral-100" />
        <div className="h-8 rounded bg-neutral-100" />
      </div>
      <div className="mt-6 h-10 w-40 rounded bg-neutral-200" />
    </div>
  );
}
