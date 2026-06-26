# UI screens

Every storefront and admin screen with its buttons and what each is for — captured **live in a real
browser** (Playwright) against a seeded stack, so they stay accurate to the code. Open
[screens.html](./screens.html) for the full gallery.

Regenerate after a UI change:

```bash
# stack up (gateway :8080), storefront :3000, admin :5200, catalog seeded
cd src/Storefront
GATEWAY_URL=http://localhost:8080 npm run test:e2e -- --project=storefront -g screenshots
GATEWAY_URL=http://localhost:8080 npm run test:e2e -- --project=admin -g screenshots
# images land in docs/help/assets/screenshots/
```

- **Storefront** (`:3000`): home, search, product detail, cart, checkout, sign in, register, account.
- **Admin** (`:5200`): sign in, dashboard, orders, ledger, RMA queue, entities & suppliers, imports,
  Xero sync, roles & permissions, mission control, commerce ops, catalog editor.
