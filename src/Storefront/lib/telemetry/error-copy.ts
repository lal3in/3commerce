// Error-page copy for app/global-error.tsx, in every shipped locale.
//
// global-error replaces the ROOT layout, so it renders outside NextIntlClientProvider and cannot load a message
// catalog (and must not pull six full catalogs into the client bundle). These strings are therefore embedded here;
// they are the SAME as the `errorPage` namespace in messages/<code>.json that app/error.tsx uses — the unit spec
// (e2e/telemetry-scrub.spec.ts) fails if the two drift or a locale is missing.

export interface ErrorCopy {
  title: string;
  body: string;
  reference: string;
  retry: string;
  home: string;
}

export const ERROR_COPY: Record<string, ErrorCopy> = {
  en: {
    title: "Something went wrong",
    body: "We could not show this page. Please try again. If it keeps happening, contact support and quote the reference below.",
    reference: "Support reference",
    retry: "Try again",
    home: "Go to the home page",
  },
  zh: {
    title: "出错了",
    body: "无法显示此页面。请重试。如果问题持续出现，请联系客服并提供下面的参考编号。",
    reference: "客服参考编号",
    retry: "重试",
    home: "返回首页",
  },
  yue: {
    title: "出咗問題",
    body: "我哋顯示唔到呢個頁面。請再試一次。如果問題持續，請聯絡客服並提供下面嘅參考編號。",
    reference: "客服參考編號",
    retry: "再試一次",
    home: "返回主頁",
  },
  de: {
    title: "Etwas ist schiefgelaufen",
    body: "Diese Seite konnte nicht angezeigt werden. Bitte versuchen Sie es erneut. Wenn das Problem weiter besteht, wenden Sie sich an den Support und nennen Sie die Referenz unten.",
    reference: "Support-Referenz",
    retry: "Erneut versuchen",
    home: "Zur Startseite",
  },
  fr: {
    title: "Une erreur est survenue",
    body: "Impossible d’afficher cette page. Veuillez réessayer. Si le problème persiste, contactez le support en indiquant la référence ci-dessous.",
    reference: "Référence support",
    retry: "Réessayer",
    home: "Aller à l’accueil",
  },
  es: {
    title: "Algo salió mal",
    body: "No pudimos mostrar esta página. Inténtalo de nuevo. Si sigue ocurriendo, contacta con soporte e indica la referencia de abajo.",
    reference: "Referencia de soporte",
    retry: "Reintentar",
    home: "Ir a la página de inicio",
  },
};

/** Copy for a BCP-47 tag (exact, then primary subtag), English otherwise. */
export function errorCopyFor(tag: string | null | undefined): ErrorCopy {
  const normalized = (tag ?? "").trim().toLowerCase();
  return ERROR_COPY[normalized] ?? ERROR_COPY[normalized.split("-")[0]] ?? ERROR_COPY.en;
}
