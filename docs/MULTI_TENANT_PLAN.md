# Multi-establishment (multi-tenant) plan

**Status:** proposal / architecture baseline
**Scope:** turn the current single-shop app (Queen of the South Café) into one codebase that
serves many establishments, where everything that differs between clients is **configuration or
data, never a code branch**.

> **Assumption carried in from the prior session:** development stays *combined* — one repo, one
> Docker image, one deployment pipeline. Per-client differences are parameterised. There are no
> per-client forks, per-client branches, or `if (client == "x")` blocks anywhere. Every rule below
> follows from that.

---

## 1. Where the code is today

| Area | File | State |
|---|---|---|
| API + routing + SQL | `Program.cs` (~1,020 lines, 30 inline endpoints) | single-tenant throughout |
| Google Wallet | `GoogleWalletService.cs` | one fixed class ID, hardcoded colour + logo path |
| Customer join page | `wwwroot/join.html` | brand colours, copy, imagery, privacy policy all inline |
| Customer card | `wwwroot/card.html` | same |
| Till app | `wwwroot/shop/index.html` | `:root` palette inline |
| Admin | `wwwroot/admin/index.html` | `:root` palette inline, brand in `<title>`/header |
| PWA | `manifest.json`, `shop/manifest.json`, `sw.js`, `shop/sw.js` | brand name, colours, cache keys hardcoded |
| Data | SQLite at `AppContext.BaseDirectory/data/loyalty.db` | one file, one shop, flat `settings` KV table |
| Deploy | `Dockerfile`, `docker-compose.yml`, GH Actions → ghcr.io | one container, one `loyalty_data` volume |

Seven settings are already parameterised in the DB (`shop_name`, `wallet_program_name`,
`points_per_pound`, `staff_pin`, `admin_pin`, `google_wallet_issuer_id`,
`google_wallet_service_account_json`, `public_base_url`). That is the seed of the right idea — the
plan extends it to everything else and gives it a tenant dimension.

---

## 2. Recommended architecture

### 2.1 Tenant resolution — by host, with a path fallback

```
qosfc.loyalty.app        -> tenant "qosfc"
cafe-nero-w1.loyalty.app -> tenant "cafenero-w1"
loyalty.thebakery.co.uk  -> tenant "thebakery"   (custom domain, CNAME)
```

Middleware resolves `Host` → tenant once per request and puts the resolved `Tenant` on
`HttpContext.Items`. **Endpoints never accept a tenant identifier from the client** — not from a
query string, not from a header, not from the body. That single rule is what prevents cross-tenant
access.

Why host-based rather than `/t/{slug}/...`:

- PWA scope and `start_url` are per-origin. Path-based tenancy puts two clients' service workers
  and caches on the same origin, and a stale SW from tenant A can serve tenant B's shell.
- `localStorage` (`myCardToken`) is per-origin — path tenancy would let one customer's card token
  leak across shops on the same device.
- Custom client domains are a sellable feature and only work host-based.

Keep a `DEFAULT_TENANT_SLUG` env var so local dev and any unrecognised host resolve to one tenant.
This is what lets the existing QOSFC deployment keep working unchanged through the whole migration.

### 2.2 Data isolation — one SQLite file per tenant, plus a small catalogue

```
/app/data/
  platform.db                       # catalogue: tenants, hosts, platform admins, audit
  tenants/
    qosfc/
      qosfc.db                      # full existing schema, unchanged
      assets/{logo.png,hero.jpg,icon-192.png,icon-512.png}
    thebakery/
      thebakery.db
      assets/...
```

**Recommended over a shared-table `tenant_id` design**, for this codebase specifically:

- **Zero SQL rewrites.** Every existing query stays exactly as written. The change is mechanical:
  `Open()` → `Open(tenant)` at 31 call sites. A shared-table design means auditing all ~50 SQL
  statements, and one missed `WHERE tenant_id = ?` is a data breach.
- **Isolation is physical, not logical.** No query can leak across tenants because the connection
  cannot see the other file.
- **SQLite write locking is per-file.** A busy shop at 8am does not block another shop's till.
- **GDPR and offboarding are file operations.** Export a client = send one file. Delete a client =
  delete one file. Back up a client = copy one file.
- **Per-client restore.** One shop can be rolled back without touching anyone else.

Cost: schema migrations must loop over every tenant DB at startup (easy, and the runner is needed
anyway), and cross-tenant platform reporting needs a fan-out query. There is no cross-tenant
reporting today, and when it comes it is a small aggregate job.

If the tenant count ever exceeds a few hundred active files, or genuine cross-tenant analytics
becomes a core feature, revisit — moving file-per-tenant → Postgres row-level-security later is a
well-trodden path, and it is a far easier migration than untangling a leaky shared-table design.

### 2.3 Deployment shape

One image, one container, many tenants. Wildcard DNS `*.loyalty.example.com` plus CNAMEs for
custom domains; TLS terminated at Caddy/Traefik/Cloudflare in front. `docker-compose.yml` keeps its
single `loyalty_data` volume — it just holds more files.

Onboarding a new establishment becomes a **form, not a deploy**: create tenant → pick a theme
preset → upload logo and hero image → set programme rules, currency and timezone → set PINs. No
code change, no image rebuild, no restart.

---

## 3. Parameterisation inventory

Everything below is currently hardcoded and must become tenant configuration.

### 3.1 Colour and theme

| Token (proposed) | Current value | Used by |
|---|---|---|
| `--brand-primary` | `#094582` | `theme-color` meta ×3, admin header, till background, `--brown` in both `:root` blocks, Wallet `hexBackgroundColor` |
| `--brand-primary-deep` | `#052038` | join page gradient stop, `manifest.json` `background_color` |
| `--brand-primary-mid` | `#08315b` | join gradient, photo fade |
| `--brand-primary-light` | `#0d5396` | join radial gradient |
| `--brand-surface-dark` | `#061d33` | `card.html` body, dynamic manifest `background_color` |
| `--brand-card-1/2/3` | `#12588f` / `#0a3e6c` / `#062b4e` | card QR hero gradient |
| `--brand-accent` | `#009fff` | primary buttons, install bar, checkbox accent |
| `--brand-accent-hover` | `#33b3ff` | button hover |
| `--brand-accent-soft` | `#5fc4ff` | headline accent word, focus ring, arrows |
| `--brand-accent-tint` | `#7fd0ff` | pill text, modal headings |
| `--brand-on-accent` | `#04243f` | text on accent buttons |
| `--surface` | `#f0f4fa` (`--cream`) | admin page background |
| `--surface-alt` | `#eaf1fb` | table headers, reward buttons |
| `--accent-ui` | `#1a6abf` | admin nav underline, till points figure |
| `--success` | `#2e8b57` / `#34d399` / `#bbf7d0` | ok states, "welcome back" banner |
| `--danger` | `#c0392b` / `#ff8fa3` / `#fdecea` | error states |
| `--nav-dark` | `#062f5a` | admin nav bar |
| `--modal-surface` | `#0d2d4a` | privacy sheet |
| `--install-bar-bg` | `#0a2e50` | install prompt |

Also parameterise: `--font-display` (`Space Grotesk`), `--font-body` (`Manrope`), the Google Fonts
`<link>` URL built from those two, and `--radius` (currently 10/12/14/16/22/26px, ad-hoc).

**Delivery:** a single `GET /theme.css` endpoint emits `:root { ... }` for the resolved tenant,
with an `ETag` and long cache + version query string. Every page links it and every rule uses
`var(--token)` only. No literal hex values survive in the HTML. `theme-color` meta tags are filled
by the same templating pass as §3.2 (a `<meta>` cannot read a CSS var).

Ship 4–6 **theme presets** (Deep Blue = current, Warm Brown, Forest, Charcoal, Blush) so a new
client starts one click from presentable, then overrides individual tokens.

### 3.2 Copy and text

| String | Location |
|---|---|
| `Join — Queen of the South Café` | `join.html:10` |
| `Queen of the South` (brand line) | `join.html:126–127` |
| `Loyalty club` (pill) | `join.html:131` |
| `Coffee / fit for / royalty.` (headline + accent word) | `join.html:132` |
| `Collect a stamp with every cup…` (subcopy) | `join.html:133` |
| Marketing consent sentence naming the café | `join.html:163` |
| Entire privacy policy body + `hello@queensofthecafe.co.uk` | `join.html:184–197` |
| `My QOSFC Loyalty Card` | `card.html:9` |
| `Welcome back` / `Scan to earn & redeem` / install-bar copy / home-screen tip | `card.html` |
| `Till — QOSFC Loyalty`, `Till` | `shop/index.html:8,62` |
| `Queen of the South Café — Admin` ×2 | `admin/index.html:6,61` |
| `QOSFC Loyalty Card` / `My Card` | `Program.cs:1004–1005` (dynamic manifest) |
| `Queen of the South Café Loyalty`, `QOSFC Loyalty`, description | `manifest.json` |
| `QOSFC Loyalty Till` | `shop/manifest.json` |
| `e.g. Free regular coffee`, `e.g. coffee`, `collect N coffees` | `admin/index.html:189,196,197` |
| `Purchase £{x}` / `Redeemed: {x}` / `Stamped: N items` transaction descriptions | `Program.cs:537,578,614` |
| `Amount must be between £0.01 and £1000` | `Program.cs:520` |

**Delivery:** keep the pages as plain `.html` with `{{brand.headline}}`-style placeholders and run
them through a substitution middleware that caches the compiled page per tenant in memory,
invalidated when settings are saved. No Razor, no build step, files stay directly editable, and
there is no flash-of-unbranded-content (which a client-side hydrate would cause).

The privacy policy needs to be a **rich-text field** (markdown) per tenant, not a set of slots —
different jurisdictions and different data controllers need genuinely different text. Seed new
tenants from a UK-GDPR template with `{{shop_name}}` / `{{contact_email}}` filled in.

### 3.3 Imagery and assets

| Asset | Current | Notes |
|---|---|---|
| `/logo.png` | 387 KB | used as watermark, brand mark, PWA icon at both 192 and 512 |
| `/logo.jpg` | 82 KB | used by till + admin headers **and** by the Wallet pass `programLogo` |
| `/latte.png` | 746 KB | join-page hero photo |
| `/shop/icon.svg` | inline `☕` on `#4b2e2b` | till PWA icon, brown — doesn't even match the current blue theme |

Two different logo files for the same logo is an existing inconsistency. Consolidate to one
per-tenant asset set, resolved through `/brand/{name}` against
`data/tenants/{slug}/assets/`, falling back to a platform default when a tenant hasn't uploaded one:

- `logo` (square, transparent) — header, watermark, install bar
- `icon-192`, `icon-512`, `icon-maskable` — generated on upload, not hand-supplied
- `hero` — join page photo
- `wallet-logo` — must be a publicly reachable absolute URL for Google
- `favicon`

On upload: re-encode, strip EXIF, resize to the needed variants, cap dimensions. 746 KB on the
critical path of the join page is a conversion problem today and gets worse when clients upload
phone photos.

The hero treatment on `join.html:139–141` is four stacked gradient tints hardcoded to the blue
palette. Rewrite them in terms of the tokens so a warm-brown tenant's photo doesn't come out blue.

### 3.4 Programme rules, currency and locale

| Item | Current | Becomes |
|---|---|---|
| Points per £1 | `points_per_pound` setting | keep, rename `points_per_currency_unit` |
| Currency | `£` hardcoded in 6 places, `amount_pence` column name | `currency_code` + symbol + minor-unit digits; format server-side |
| Quick-spend buttons | `£5 / £10 / £20` hardcoded (`shop/index.html:93–95`) | configurable array — a bakery wants £2/£3/£5 |
| Quick-stamp buttons | `1–5` | configurable |
| Max transaction | `£1000` | per-tenant cap |
| Stamp icon | 🏆 in Wallet passes (`Program.cs:218`), ⚽ in activity tags (`card.html:264`), a cup SVG in the grid (`card.html:182,184`) | one configurable icon (emoji or SVG ref) used in all three places — these currently disagree with each other |
| Stamp noun | `item` default, `coffee` example | already per-reward (`rewards.item_name`), keep |
| Rounding | floor, per transaction | make the rule explicit and configurable (floor/nearest) |
| Timezone | `DateTime.UtcNow.Date` (`Program.cs:653`) | per-tenant IANA timezone |
| Week start | Monday hardcoded (`Program.cs:655`, weekday table `:732`) | per-tenant |
| Date format | `ddd d MMM`, `yyyy-MM-dd` | per-tenant culture |
| Language | `lang="en"`, `en-GB` in Wallet | per-tenant locale |

**Timezone is a correctness bug, not just a nicety.** Transactions are stamped `datetime('now')`
(UTC) and the dashboard buckets by UTC calendar date. An evening sale at 23:30 BST already lands on
the next day's bar. Store UTC (correct), convert to tenant-local for all reporting.

### 3.5 PWA and service workers

- `sw.js` cache key `qosfc-loyalty-v1` and `shop/sw.js` key `till-v1` must become
  `{slug}-{surface}-v{n}` and the version must bump on deploy, or clients get stale shells.
- `sw.js` precaches `/logo.png`, `/latte.png` — must precache the tenant-resolved brand URLs.
- Both manifests become endpoints (`/manifest.json`, `/shop/manifest.json`) rendered per tenant,
  matching the existing `/api/manifest/{token}` pattern.
- The dynamic manifest at `Program.cs:1000–1016` hardcodes name, both colours and both icons.

### 3.6 Google Wallet

| Item | Current | Change |
|---|---|---|
| Class ID | `{issuerId}.loyalty_card` — one per issuer (`GoogleWalletService.cs:29`) | `{issuerId}.loyalty_{slug}` — one class per tenant |
| Object ID | `{issuerId}.loyalty_{token}` | `{issuerId}.loyalty_{slug}_{token}` — prevents collision when tenants share a platform issuer |
| `hexBackgroundColor` | `#094582` hardcoded (`GoogleWalletService.cs:118`) | tenant `--brand-primary` |
| `programLogo` | `{baseUrl}/logo.jpg` | tenant `wallet-logo` asset URL |
| `language` | `en-GB` | tenant locale |
| Issuer credentials | per-tenant only | **support both**: platform issuer (default — client does nothing) and BYO tenant issuer (enterprise clients who want their own) |
| Access token | fetched on every call (`GoogleWalletService.cs:84`) | cache per service account until expiry |
| `GoogleWalletService` | `new` per request, parses the key and creates an `RSA` each time | cache one instance per tenant |

A **platform issuer should be the default**. The current 8-step Google Cloud setup in the admin
help text is a hard stop for a café owner; with a platform issuer the client gets wallet passes on
day one and never sees it.

### 3.7 Auth and PINs

Today: `staff_pin` and `admin_pin` stored in plaintext, compared with `pin == expected`
(`Program.cs:258–264`); the client keeps the PIN in `sessionStorage` and sends it as an `X-Pin`
header on every request; there is no rate limiting. A 4-digit PIN with unlimited attempts is
brute-forceable in minutes — acceptable-ish for one café on an obscure URL, not acceptable for a
platform holding many clients' customer lists.

Changes (see §5): hash PINs, rate-limit, exchange the PIN for a short-lived signed session token,
add a platform super-admin that is separate from any tenant admin.

---

## 4. Database changes

### 4.1 New: `platform.db` (catalogue)

```sql
CREATE TABLE tenants (
  id            INTEGER PRIMARY KEY,
  slug          TEXT NOT NULL UNIQUE,       -- url-safe, immutable, names the db file + asset dir
  name          TEXT NOT NULL,
  status        TEXT NOT NULL DEFAULT 'active',  -- active | suspended | archived
  timezone      TEXT NOT NULL DEFAULT 'Europe/London',
  locale        TEXT NOT NULL DEFAULT 'en-GB',
  currency_code TEXT NOT NULL DEFAULT 'GBP',
  created_at    TEXT NOT NULL DEFAULT (datetime('now')),
  archived_at   TEXT
);

CREATE TABLE tenant_hosts (
  host       TEXT PRIMARY KEY,              -- lower-cased, no port
  tenant_id  INTEGER NOT NULL REFERENCES tenants(id),
  is_primary INTEGER NOT NULL DEFAULT 0     -- the one used to build absolute URLs
);

CREATE TABLE platform_admins (
  id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE platform_audit (
  id INTEGER PRIMARY KEY, actor TEXT, tenant_id INTEGER, action TEXT NOT NULL,
  detail TEXT, created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
```

Branding, programme settings and wallet config stay **inside each tenant's own DB**, so one file
remains a complete, portable client.

### 4.2 Per-tenant schema: additions

```sql
-- replaces the try/catch ALTER TABLE block at Program.cs:68-76
CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);

-- multi-site establishments (chains, franchises) — add now, use later
CREATE TABLE locations (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1
);

-- real staff accounts, replacing the single shared staff_pin
CREATE TABLE staff (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, pin_hash TEXT NOT NULL,
  role TEXT NOT NULL DEFAULT 'staff',   -- staff | manager | admin
  location_id INTEGER REFERENCES locations(id),
  active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

ALTER TABLE transactions ADD COLUMN location_id INTEGER REFERENCES locations(id);
ALTER TABLE transactions ADD COLUMN staff_id    INTEGER REFERENCES staff(id);
ALTER TABLE transactions ADD COLUMN client_ref  TEXT;   -- idempotency key from the till
ALTER TABLE transactions ADD COLUMN voided_at   TEXT;   -- soft void instead of delete
CREATE UNIQUE INDEX idx_tx_client_ref ON transactions(client_ref) WHERE client_ref IS NOT NULL;

ALTER TABLE customers ADD COLUMN consent_at     TEXT;   -- GDPR: proof of consent
ALTER TABLE customers ADD COLUMN consent_source TEXT;
ALTER TABLE customers ADD COLUMN withdrawn_at   TEXT;
ALTER TABLE customers ADD COLUMN updated_at     TEXT;
ALTER TABLE customers ADD COLUMN deleted_at     TEXT;   -- soft delete

CREATE INDEX idx_tx_customer  ON transactions(customer_id, id);
CREATE INDEX idx_tx_type_date ON transactions(type, created_at);
CREATE INDEX idx_cust_created ON customers(created_at);
```

### 4.3 Per-tenant settings: keep the KV table, add typing and namespacing

The flat `settings(key, value)` table is fine and cheap — don't replace it with columns. Do:

- **Namespace keys**: `brand.*`, `copy.*`, `theme.*`, `programme.*`, `wallet.*`, `locale.*`,
  `auth.*`. Values that are structured (quick-spend buttons, theme token map) stored as JSON.
- **Bind to a typed `TenantConfig` record** on read, with defaults in code. Every consumer goes
  through the typed object, so a missing key can never render as an empty string in the UI. Cache
  the object per tenant; invalidate on save.
- **Never return secrets.** `GET /api/admin/settings` already does this correctly for the service
  account JSON (returns a boolean) — apply the same discipline to every new secret.

### 4.4 Other DB findings worth fixing during this work

1. **Migrations are unversioned** — `Program.cs:68–76` runs `ALTER TABLE` in a `try { } catch { }`
   that silently swallows *every* error, not just "column exists". Replace with a versioned runner
   that executes per tenant DB at startup and on tenant creation, and that fails loudly.
2. **Hard delete destroys audit trail** — `DELETE /api/admin/customer/{id}` (`Program.cs:884`)
   deletes the customer's transactions. Soft-delete instead; keep a genuine hard-delete path
   behind a separate "GDPR erasure" action that also logs to `platform_audit`.
3. **No idempotency at the till** — a double-tap on "Add" creates two earns. `client_ref` above
   fixes it; the till generates a UUID per action.
4. **Balance is a `SUM` over all transactions** on every card load, every SSE push, and every till
   lookup (`PointsBalance`, `Program.cs:114`). Fine at current volume, and the new index makes it
   cheaper. Revisit with a cached balance column only if a tenant's ledger gets long.
5. **`amount_pence` is a GBP-shaped name** for what should be "minor currency units". Rename to
   `amount_minor` during the migration while the data is still small.
6. **Secrets in plaintext** — `google_wallet_service_account_json` is a private key sitting in a
   readable SQLite file. Encrypt at rest (AES-GCM via ASP.NET Data Protection, key from env).
7. **No `PRAGMA` tuning** — set `journal_mode=WAL` and `busy_timeout` on open. WAL matters more
   once several tills and many card pages hit one file concurrently.
8. **SSE subscribers are keyed by token alone** (`Program.cs:185`). Key by `(tenantId, token)`.
   Also note: SSE only reaches the instance holding the connection, so horizontal scaling later
   needs sticky sessions or a small pub/sub bus.

---

## 5. Security and isolation checklist

- Tenant comes from `Host`, resolved in middleware, never from client input.
- A test that asserts tenant A's admin PIN cannot read tenant B's customers — the single most
  important test in the suite.
- PINs hashed (PBKDF2 or bcrypt, per-tenant salt); no plaintext PIN in the DB.
- Rate-limit `/api/*/login` and any PIN-guarded endpoint per IP and per tenant; lock out after N
  failures.
- Replace the `X-Pin` header with a short-lived signed session token issued by `/login`, so the
  PIN crosses the wire once rather than on every request.
- Encrypt wallet service-account keys at rest.
- `AllowedHosts: "*"` → the resolved tenant host set.
- Platform super-admin lives on a reserved host (`admin.loyalty.app`) with its own credentials,
  separate from tenant admins.
- Per-tenant asset uploads: validate content type by sniffing, re-encode, cap size, never serve
  from a path built out of user input.

---

## 6. Code structure

`Program.cs` at 1,020 lines with 30 inline endpoints is already at its limit, and tenant plumbing
touches nearly all of it. Split before Phase 2, keeping minimal-API style:

```
src/
  Tenancy/       TenantResolutionMiddleware, TenantCatalogue, TenantContext, TenantConfig
  Data/          Db.Open(tenant), MigrationRunner, Migrations/001_..., queries
  Branding/      ThemeCss endpoint, PageTemplate substitution, AssetResolver, presets
  Wallet/        GoogleWalletService (per-tenant), token cache, class/object naming
  Endpoints/     Public, Card, Shop, Admin, PlatformAdmin
  Realtime/      SseHub (keyed by tenant+token)
tests/
  Tenancy.Tests  isolation, host resolution, migration-per-tenant
```

There are currently **no tests at all**. The isolation tests are non-negotiable before a second
client's data is on the box.

---

## 7. Phased delivery

Each phase ships independently and leaves the live QOSFC deployment working.

### Phase 0 — Foundations (no behaviour change)
Versioned migration runner replacing the silent `ALTER` block; typed `TenantConfig` bound over the
existing `settings` table; split `Program.cs` into the structure above; add the test project and a
smoke test. **Nothing visible changes.**

### Phase 1 — Parameterise branding (still one tenant)
`/theme.css` + token rewrite of all four pages; placeholder substitution for every string in §3.2;
asset resolution via `/brand/*` with fallbacks; manifests and service workers become rendered
endpoints; 4–6 theme presets; new **Branding** tab in admin (colours, logo/hero upload, copy,
privacy policy editor, live preview). Programme/locale settings from §3.4.
**Milestone: a new client can be re-skinned end-to-end without touching code.**

### Phase 2 — Tenant plumbing
`platform.db` catalogue; host resolution middleware; per-tenant DB files; `Open()` → `Open(tenant)`;
SSE keyed by tenant; migration runner loops all tenants; one-shot startup migration that moves the
existing `loyalty.db` to `tenants/qosfc/qosfc.db`, registers the catalogue row and maps the current
host. `DEFAULT_TENANT_SLUG` keeps dev and unrecognised hosts working.
**Milestone: two establishments on one container, fully isolated.**

### Phase 3 — Platform admin and self-serve onboarding
Super-admin surface on a reserved host: tenant CRUD, host/domain mapping, suspend/archive, theme
preset picker, per-tenant backup/export/erase, audit log. Wallet moves to a platform issuer by
default with per-tenant classes, BYO issuer still supported.
**Milestone: onboarding a client is a 10-minute form.**

### Phase 4 — Hardening
PIN hashing + rate limiting + session tokens; secret encryption; staff accounts replacing the
shared staff PIN; idempotency keys; indices and WAL; soft delete + GDPR erasure path; tenant-local
timezone across all reporting; asset pipeline (resize, strip, generate icon variants).

### Phase 5 — Optional / commercial
Locations for multi-site chains; Apple Wallet (needs an Apple Developer account and a Pass Type ID
certificate — currently a 501 stub at `Program.cs:482`); marketing-list export honouring
`marketing_ok` + `consent_at`; per-tenant automated backups; cross-tenant platform analytics;
billing/plan limits.

---

## 8. Risks and open questions

| Risk | Mitigation |
|---|---|
| Migrating the live QOSFC DB | One-shot, idempotent, at startup; back up the volume first; the file is only moved, never rewritten |
| Stale service workers after the SW rewrite | Version cache keys, `skipWaiting` + `clients.claim` (both already present), bump on deploy |
| Google Wallet class review per tenant | `reviewStatus: UNDER_REVIEW` today; confirm Google's review posture for many classes under one platform issuer before committing to platform-issuer-by-default |
| Theme presets that produce unreadable text | Ship presets as vetted pairs with a contrast check on custom overrides |
| One volume holding every client's data | Per-tenant backup job from Phase 3; the file-per-tenant layout makes this trivial |
| Cross-tenant leak via a missed check | Host-only resolution + isolation tests; file-per-tenant makes a leak structurally hard rather than merely forbidden |

**Open questions for you:**

1. Wildcard subdomain + custom domains — is DNS/TLS in front (Cloudflare? Caddy?) yours to
   configure, or does each client bring their own domain?
2. Platform Google Wallet issuer, or does each establishment set up their own?
3. Do any target clients have multiple sites? If yes, `locations` moves from Phase 5 to Phase 2.
4. Any non-UK / non-GBP clients in the near term? That moves currency and locale work earlier.
5. Is one container for all tenants right, or do some clients need their own container (data
   residency, a contractual isolation requirement)? The file-per-tenant design supports both —
   the same image with `DEFAULT_TENANT_SLUG` set runs a dedicated single-client instance.
