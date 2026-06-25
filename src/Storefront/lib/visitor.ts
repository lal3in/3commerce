// First-party visitor/session identifiers (mt5_5). Visitor id persists (localStorage), session id is
// per-tab/session (sessionStorage). No third-party cookies. Cleared when analytics consent is withdrawn.

function newId(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }
  return `${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
}

export function visitorId(): string {
  if (typeof window === "undefined") return "ssr";
  let id = window.localStorage.getItem("3c_vid");
  if (!id) {
    id = newId();
    window.localStorage.setItem("3c_vid", id);
  }
  return id;
}

export function sessionId(): string {
  if (typeof window === "undefined") return "ssr";
  let id = window.sessionStorage.getItem("3c_sid");
  if (!id) {
    id = newId();
    window.sessionStorage.setItem("3c_sid", id);
  }
  return id;
}

export function clearFirstPartyIds(): void {
  if (typeof window === "undefined") return;
  window.localStorage.removeItem("3c_vid");
  window.sessionStorage.removeItem("3c_sid");
}
