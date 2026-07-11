# UI screens

Every storefront and admin screen with its buttons and what each is for — captured **live in a real
browser** (Playwright) against a seeded stack, so they stay accurate to the code. Open
[screens.html](./screens.html) for the full gallery. For audience-specific demo narration,
connect these screenshots to [Selling information](./selling-information.md).

Regenerate after a UI change:

```bash
# stack up (gateway :8080), storefront :3000, admin :5200, catalog seeded
cd src/Storefront
GATEWAY_URL=http://localhost:8080 npm run test:e2e -- --project=storefront -g screenshots
GATEWAY_URL=http://localhost:8080 npm run test:e2e -- --project=admin -g screenshots
# images land in docs/help/assets/screenshots/
```

- **Storefront** (`:3000`): home, search, product detail, cart, checkout (with selectable payment
  methods), sign in, register, account, privacy/consent settings.
- **Admin** (`:5200`): sign in, dashboard, orders, ledger, RMA queue, entities & suppliers, imports,
  Xero sync &amp; mappings, roles & permissions, operator users, mission control (with the live activity
  timeline &amp; message-bus stats), commerce ops, catalog editor, offers, payment accounts, supplier
  payouts, and **security** (MFA enrollment, tenant MFA policy, webhook signing secrets).
