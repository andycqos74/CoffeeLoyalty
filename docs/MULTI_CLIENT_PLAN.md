# Multi-establishment plan

**Status:** proposal / architecture baseline
**Scope:** turn the current single-shop app (Queen of the South Café) into one codebase that
serves many establishments, where everything that differs between clients is **configuration or
data, never a code branch**.

**Recommendation in one line:** parameterise everything (§3), then run **one container per
establishment** from the same image with its config mounted from git — not a shared multi-tenant
instance. The reasoning is in §2; the short version is that the parameterisation work is needed
either way, and instance-per-client removes the cross-client data-leak bug class by construction
instead of defending against it forever.

> **Assumption carried in from the prior session:** development stays *combined* — one repo, one
> Docker image, one deployment pipeline. Per-client differences are parameterised. There are no
> per-client forks, per-client branches, or `if (client == "x")` blocks anywhere. Every rule below
> follows from that. Note that "combined dev" is about the *codebase*, not the *process count* —
> running N identical containers is still one codebase.

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

## 2. Deployment model — the decision

Two ways to serve many establishments from one codebase:

- **Option A — instance per client.** One container per establishment, all running the same
  image, each with its own SQLite file and its own mounted config. The app stays single-tenant.
- **Option B — one shared multi-tenant instance.** One container serves every establishment,
  resolving the tenant from the request host.

**Recommendation: Option A.** Reasons in §2.4. Option B is documented in §2.5 because the design
below deliberately keeps the door open to it.

### 2.1 What both options need anyway — this is the bulk of the work

The important thing to see before choosing: **the deployment model is not what makes this
multi-client.** Everything in §3 (colours, copy, imagery, manifests, currency, locale, stamp
iconography, Wallet branding) is hardcoded today and has to become configuration in *either*
model — otherwise "a version for a different establishment" means editing HTML per client, which
is a fork by another name and exactly what the combined-dev constraint rules out.

Likewise almost everything in §4.4 and §5 — versioned migrations, timezone correctness, PIN
hashing and rate limiting, secret handling, idempotency, indices, soft delete — is needed in both.

So the choice is narrow. It is only:

> Do you build tenant plumbing (a catalogue DB, host resolution, `Open(tenant)` at 31 call sites,
> tenant-keyed SSE, and an isolation test suite), or do you build fleet ops (a provisioning
> script, a reverse proxy, and a one-command upgrade across N containers)?

Both are a few days. One of them adds permanent complexity to every feature you ever write after
it; the other doesn't.

### 2.2 Option A in detail — instance per client

**Client configuration lives in git, not in a database.** Each establishment gets a directory:

```
clients/
  qosfc/
    client.json            # name, theme tokens, copy, programme rules, locale, wallet config
    assets/{logo.png,hero.jpg,icon-192.png,icon-512.png}
  thebakery/
    client.json
    assets/...
```

mounted read-only into that client's container at `/app/config`. At startup the app loads
`client.json` into its typed config object; `assets/` is served through `/brand/*` with fallback
to platform defaults. Secrets (the Wallet service-account key) are mounted as a separate file or
injected as env, **not** stored in the DB — which sidesteps the at-rest encryption problem in §4.4
entirely rather than solving it.

This gives you something the multi-tenant model does not: **client configuration is reviewable,
diffable and version-controlled.** You can see in a PR that a client's palette changed, roll it
back, and reproduce any client exactly from a fresh container. Admin-UI edits write back to the
DB as an override layer, and a `GET /api/admin/config-export` dumps the merged result so you can
commit it back.

**One compose file, N services.** Do not create N separate compose stacks — that is where the ops
cost people fear actually comes from. Generate a single file:

```yaml
services:
  qosfc:
    image: ghcr.io/andycqos74/coffeeloyalty:latest
    restart: unless-stopped
    environment: [ASPNETCORE_ENVIRONMENT=Production, DOTNET_gcServer=0]
    volumes:
      - ./clients/qosfc:/app/config:ro
      - qosfc_data:/app/data
    labels:
      - traefik.http.routers.qosfc.rule=Host(`qosfc.loyalty.app`)
      - traefik.http.routers.qosfc.tls.certresolver=le
  thebakery:
    # ...identical block, different name/host/volume
```

Consequences:

- **No published ports.** Traefik (or Caddy) routes by `Host` on an internal network and handles
  TLS automatically. Drop the current `3002:8080` mapping.
- **Upgrades are one command** for the whole fleet: `docker compose pull && docker compose up -d`.
  This is the fact that kills most of the "ops overhead" objection.
- **Per-client version pinning is free.** If an upgrade breaks one client, pin that one service to
  the previous image tag while you fix it. Option B cannot do this at all.
- **Per-client resource limits are free** — a runaway client cannot starve the others.
- **Adding a client** = generate the service block, create `clients/{slug}/`, add a DNS record.
  Scriptable to a single `./provision.sh thebakery "The Bakery"` plus one DNS entry.

**Watch the memory.** The Web SDK defaults `ServerGarbageCollection=true`, which allocates per-core
GC heaps — wasteful when you run many small instances. `CoffeeLoyalty.csproj` does not override it,
so every container is currently running Server GC. Set `DOTNET_gcServer=0` (or
`<ServerGarbageCollection>false</ServerGarbageCollection>`) and expect roughly 80–150 MB RSS per
idle container; measure it on your own box before sizing. Also set `InvariantGlobalization=false`
deliberately — per-client timezones and locales (§3.4) need the ICU data.

### 2.3 What Option A does *not* get you for free

Three things still need fixing even with one container per client:

1. **The Google Wallet class ID.** `ClassSuffix` is hardcoded to `loyalty_card`
   (`GoogleWalletService.cs:29`), so the class is `{issuerId}.loyalty_card`. If your clients share
   one platform Google issuer account, **every container fights over the same LoyaltyClass** —
   each one's `EnsureClassExists` does a full `PUT` that overwrites the previous client's issuer
   name, programme name, logo and background colour. Separate containers do not help; the
   collision is on Google's servers. The class ID must include the client slug, and the object ID
   must too. This is a genuine bug the moment there is a second client, unless every client brings
   their own issuer account.
2. **PIN brute-forcing.** Blast radius shrinks to one client, but a 4-digit PIN with no rate
   limiting still leaks that client's entire customer list. Still must be fixed.
3. **Config bootstrap.** A fresh container currently seeds `settings` with QOSFC's name and PINs
   `1234`/`9999` (`Program.cs:56–63`). Without the `client.json` load, every new client ships with
   the wrong name and the same default PINs.

### 2.4 Why Option A wins here

| | Option A (instance per client) | Option B (shared multi-tenant) |
|---|---|---|
| Cross-client data leak | structurally impossible | one missed `WHERE` away, forever |
| Code change for tenancy | none | catalogue, host resolution, 31 `Open()` sites, SSE keying |
| Ongoing complexity | none — every feature stays single-tenant | every future query, endpoint and test carries tenancy |
| Isolation test suite | not needed | mandatory before client #2 |
| Per-client rollback / version pinning | free | impossible |
| Per-client backup / GDPR erase | a volume | a file |
| Onboarding | script + DNS, ~10 min | a form, ~10 min |
| Cost per client | ~80–150 MB RAM | ~0 |
| Self-serve signup | awkward | natural |
| Cross-client analytics | needs fan-out | a query |

The deciding factor is not the table, it's the risk asymmetry. A cross-tenant leak is
reputationally fatal for a small vendor selling to local businesses, and Option B carries that
risk permanently in every future change. Option A removes the entire bug class by construction,
and its costs are predictable, bounded and mostly one-off scripting.

For a realistic first year — somewhere between 2 and 30 establishments — Option A is cheaper in
total effort, lower risk, and gets a second client live sooner.

### 2.5 When to revisit

Move to Option B if any of these become true:

- **More than ~30–50 active clients.** RAM cost becomes real and the generated compose file gets
  unwieldy.
- **Self-serve signup** becomes the product shape — a signup form that provisions containers is
  more machinery than multi-tenancy.
- **Cross-client analytics** becomes a feature you sell, rather than something you occasionally
  want.
- **You move to a per-instance-billed platform** (Fly, Render, App Service) where idle containers
  cost money rather than just RAM.

The migration path stays open because of one design choice: keep the app reading its identity from
a single typed config object resolved once per process. Option B is then "resolve that object per
request from a catalogue instead of once at startup from a file" — plus the isolation work. Nothing
in §3 or §4.2 is wasted either way.

## 3. Parameterisation inventory

Everything below is currently hardcoded and must become client configuration.

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

**Delivery:** a single `GET /theme.css` endpoint emits `:root { ... }` for the resolved client,
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
them through a substitution middleware that caches the compiled page per client in memory,
invalidated when settings are saved. No Razor, no build step, files stay directly editable, and
there is no flash-of-unbranded-content (which a client-side hydrate would cause).

The privacy policy needs to be a **rich-text field** (markdown) per client, not a set of slots —
different jurisdictions and different data controllers need genuinely different text. Seed new
clients from a UK-GDPR template with `{{shop_name}}` / `{{contact_email}}` filled in.

### 3.3 Imagery and assets

| Asset | Current | Notes |
|---|---|---|
| `/logo.png` | 387 KB | used as watermark, brand mark, PWA icon at both 192 and 512 |
| `/logo.jpg` | 82 KB | used by till + admin headers **and** by the Wallet pass `programLogo` |
| `/latte.png` | 746 KB | join-page hero photo |
| `/shop/icon.svg` | inline `☕` on `#4b2e2b` | till PWA icon, brown — doesn't even match the current blue theme |

Two different logo files for the same logo is an existing inconsistency. Consolidate to one
per-client asset set, resolved through `/brand/{name}` against
`/app/config/assets/` (Option A) or `data/tenants/{slug}/assets/` (Option B), falling back to a platform default when a client has not uploaded one:

- `logo` (square, transparent) — header, watermark, install bar
- `icon-192`, `icon-512`, `icon-maskable` — generated on upload, not hand-supplied
- `hero` — join page photo
- `wallet-logo` — must be a publicly reachable absolute URL for Google
- `favicon`

On upload: re-encode, strip EXIF, resize to the needed variants, cap dimensions. 746 KB on the
critical path of the join page is a conversion problem today and gets worse when clients upload
phone photos.

The hero treatment on `join.html:139–141` is four stacked gradient tints hardcoded to the blue
palette. Rewrite them in terms of the tokens so a warm-brown client's photo doesn't come out blue.

### 3.4 Programme rules, currency and locale

| Item | Current | Becomes |
|---|---|---|
| Points per £1 | `points_per_pound` setting | keep, rename `points_per_currency_unit` |
| Currency | `£` hardcoded in 6 places, `amount_pence` column name | `currency_code` + symbol + minor-unit digits; format server-side |
| Quick-spend buttons | `£5 / £10 / £20` hardcoded (`shop/index.html:93–95`) | configurable array — a bakery wants £2/£3/£5 |
| Quick-stamp buttons | `1–5` | configurable |
| Max transaction | `£1000` | per-client cap |
| Stamp icon | 🏆 in Wallet passes (`Program.cs:218`), ⚽ in activity tags (`card.html:264`), a cup SVG in the grid (`card.html:182,184`) | one configurable icon (emoji or SVG ref) used in all three places — these currently disagree with each other |
| Stamp noun | `item` default, `coffee` example | already per-reward (`rewards.item_name`), keep |
| Rounding | floor, per transaction | make the rule explicit and configurable (floor/nearest) |
| Timezone | `DateTime.UtcNow.Date` (`Program.cs:653`) | per-client IANA timezone |
| Week start | Monday hardcoded (`Program.cs:655`, weekday table `:732`) | per-client |
| Date format | `ddd d MMM`, `yyyy-MM-dd` | per-client culture |
| Language | `lang="en"`, `en-GB` in Wallet | per-client locale |

**Timezone is a correctness bug, not just a nicety.** Transactions are stamped `datetime('now')`
(UTC) and the dashboard buckets by UTC calendar date. An evening sale at 23:30 BST already lands on
the next day's bar. Store UTC (correct), convert to client-local for all reporting.

### 3.5 PWA and service workers

- `sw.js` cache key `qosfc-loyalty-v1` and `shop/sw.js` key `till-v1` must become
  `{slug}-{surface}-v{n}` and the version must bump on deploy, or clients get stale shells.
- `sw.js` precaches `/logo.png`, `/latte.png` — must precache the client-resolved brand URLs.
- Both manifests become endpoints (`/manifest.json`, `/shop/manifest.json`) rendered per client,
  matching the existing `/api/manifest/{token}` pattern.
- The dynamic manifest at `Program.cs:1000–1016` hardcodes name, both colours and both icons.

### 3.6 Google Wallet

| Item | Current | Change |
|---|---|---|
| Class ID | `{issuerId}.loyalty_card` — one per issuer (`GoogleWalletService.cs:29`) | `{issuerId}.loyalty_{slug}` — one class per client |
| Object ID | `{issuerId}.loyalty_{token}` | `{issuerId}.loyalty_{slug}_{token}` — prevents collision when clients share a platform issuer |
| `hexBackgroundColor` | `#094582` hardcoded (`GoogleWalletService.cs:118`) | client `--brand-primary` |
| `programLogo` | `{baseUrl}/logo.jpg` | client `wallet-logo` asset URL |
| `language` | `en-GB` | client locale |
| Issuer credentials | per-shop only | **support both**: platform issuer (default — client does nothing) and BYO issuer (clients who want their own) |
| Access token | fetched on every call (`GoogleWalletService.cs:84`) | cache per service account until expiry |
| `GoogleWalletService` | `new` per request, parses the key and creates an `RSA` each time | cache one instance per process |

**This section is not solved by separate containers.** The class ID lives on Google's servers, so
N containers sharing one issuer account collide on the same `{issuerId}.loyalty_card` and
overwrite each other's pass branding. See §2.3.

A **platform issuer should be the default**. The current 8-step Google Cloud setup in the admin
help text is a hard stop for a café owner; with a platform issuer the client gets wallet passes on
day one and never sees it.

### 3.7 Auth and PINs

Today: `staff_pin` and `admin_pin` stored in plaintext, compared with `pin == expected`
(`Program.cs:258–264`); the client keeps the PIN in `sessionStorage` and sends it as an `X-Pin`
header on every request; there is no rate limiting. A 4-digit PIN with unlimited attempts is
brute-forceable in minutes — acceptable-ish for one café on an obscure URL, not acceptable once
you are selling this.

Separate containers reduce the blast radius to one client but do not fix it. Changes (see §5):
hash PINs, rate-limit, exchange the PIN for a short-lived signed session token, and generate
random PINs at provisioning instead of shipping `1234` / `9999`.

---

## 4. Database changes

Under Option A the schema stays exactly as it is per client — one DB per container, no tenant
column anywhere, every existing query untouched. §4.2 onwards applies in both models and is worth
doing regardless. §4.1 is the catalogue Option B would need; it is recorded here so the decision
is reversible, and **is not work to do now**.

### 4.1 Deferred (Option B only): `platform.db` catalogue

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

Under Option B, branding and programme settings would stay **inside each tenant's own DB**, so one
file remains a complete, portable client. Under Option A that role is played by
`clients/{slug}/client.json` in git (§2.2), which is strictly better: it is diffable and
reviewable, and a container can be rebuilt from it.

### 4.2 Per-client schema: additions (both options)

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

### 4.3 Settings: keep the KV table, layer it under the config file

The flat `settings(key, value)` table is fine and cheap — don't replace it with columns. Do:

- **Namespace keys**: `brand.*`, `copy.*`, `theme.*`, `programme.*`, `wallet.*`, `locale.*`,
  `auth.*`. Values that are structured (quick-spend buttons, theme token map) stored as JSON.
- **Bind to a typed `ClientConfig` record** on read, with defaults in code. Every consumer goes
  through the typed object, so a missing key can never render as an empty string in the UI.
- **Resolution order under Option A**: code defaults → `clients/{slug}/client.json` (mounted,
  read-only) → DB `settings` overrides written by the admin UI. Cache the merged object in memory;
  invalidate on save. `client.json` is the source of truth you commit; the DB layer is for
  same-day tweaks a client asks for over the phone.
- **Never return secrets.** `GET /api/admin/settings` already does this correctly for the service
  account JSON (returns a boolean) — apply the same discipline to every new secret.

### 4.4 Other DB findings worth fixing during this work

1. **Migrations are unversioned** — `Program.cs:68–76` runs `ALTER TABLE` in a `try { } catch { }`
   that silently swallows *every* error, not just "column exists". Replace with a versioned runner
   that executes per client DB at startup and on tenant creation, and that fails loudly.
2. **Hard delete destroys audit trail** — `DELETE /api/admin/customer/{id}` (`Program.cs:884`)
   deletes the customer's transactions. Soft-delete instead; keep a genuine hard-delete path
   behind a separate "GDPR erasure" action that also logs to `platform_audit`.
3. **No idempotency at the till** — a double-tap on "Add" creates two earns. `client_ref` above
   fixes it; the till generates a UUID per action.
4. **Balance is a `SUM` over all transactions** on every card load, every SSE push, and every till
   lookup (`PointsBalance`, `Program.cs:114`). Fine at current volume, and the new index makes it
   cheaper. Revisit with a cached balance column only if a client's ledger gets long.
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

## 5. Security checklist

Under Option A, isolation is handled by the container boundary, so the whole "did I scope this
query" class of work disappears. What remains is ordinary hardening, and none of it is optional —
one client's customer list is still worth stealing.

- **PINs hashed** (PBKDF2 or bcrypt, per-client salt); no plaintext PIN in the DB. Today
  `PinOk` does `pin == expected` against a plaintext value (`Program.cs:258–264`).
- **Rate-limit** `/api/shop/login`, `/api/admin/login` and every PIN-guarded endpoint per IP;
  lock out after N failures. A 4-digit PIN with unlimited attempts is the single worst hole in
  the app today, and containerising does not touch it.
- **Replace the `X-Pin` header** with a short-lived signed session token issued by `/login`, so
  the PIN crosses the wire once rather than on every request and never sits in `sessionStorage`.
- **Wallet service-account key out of the DB** — mount it as a file or inject as env per
  container. Under Option A this is strictly easier than encrypting it at rest.
- **Change the seeded default PINs.** Fresh installs ship `1234` / `9999` (`Program.cs:56–63`).
  Provisioning must generate random PINs per client and force a change on first admin login.
- **`AllowedHosts: "*"`** → that client's host(s). With a reverse proxy in front, also set
  `ForwardedHeaders` so rate limiting sees real client IPs rather than the proxy's.
- **Asset uploads**: sniff content type, re-encode, cap size, never build a path from user input.
- **The `clients/` directory holds no secrets in git.** Wallet keys and generated PINs go in a
  secrets store or an untracked `.env` per client; `client.json` holds only branding and rules.

If you later move to Option B, add: tenant resolved from `Host` in middleware and never from
client input, a platform super-admin on a reserved host, and an isolation test asserting client
A's admin PIN cannot read client B's customers.

---

## 6. Code structure

`Program.cs` at 1,020 lines with 30 inline endpoints is already at its limit. Option A does not
force a restructure the way tenant plumbing would, but Phase 1 touches branding across every
endpoint and page, so split it then, keeping minimal-API style:

```
src/
  Config/        ClientConfig (typed), ConfigLoader (defaults -> client.json -> DB overrides)
  Data/          Db, MigrationRunner, Migrations/001_..., queries
  Branding/      ThemeCss endpoint, PageTemplate substitution, AssetResolver, presets
  Wallet/        GoogleWalletService, access-token cache, slug-scoped class/object IDs
  Endpoints/     Public, Card, Shop, Admin
  Realtime/      SseHub
tests/
  Config.Tests   config resolution order, theme rendering, migration runner
ops/
  provision.sh   generate a client dir + compose service block
  compose.gen.py render docker-compose.yml from clients/
clients/
  qosfc/client.json + assets/
```

Under Option B this gains a `Tenancy/` folder and an isolation test suite; nothing above is
wasted.

There are currently **no tests at all**. Under Option A the critical ones are config resolution
and the migration runner — a bad migration now runs against N client databases.

---

## 7. Phased delivery

Each phase ships independently and leaves the live QOSFC deployment working.

### Phase 0 — Foundations (no behaviour change)
Versioned migration runner replacing the silent `ALTER` block (`Program.cs:68–76`); typed
`ClientConfig` bound over the existing `settings` table; split `Program.cs` per §6; add the test
project. **Nothing visible changes.**

### Phase 1 — Parameterise everything (still one client, still one container)
`/theme.css` + token rewrite of all four pages; placeholder substitution for every string in §3.2;
asset resolution via `/brand/*` with fallbacks; manifests and service workers become rendered
endpoints; 4–6 theme presets; programme, currency, locale and timezone settings from §3.4; new
**Branding** tab in admin with live preview. Config resolution order wired up: defaults →
`/app/config/client.json` → DB overrides.
**Milestone: a new establishment can be skinned end-to-end without touching code.** This is the
phase that actually delivers the goal, and it is identical under either deployment model.

### Phase 2 — Fleet provisioning (replaces the old "tenant plumbing" phase)
Move `clients/qosfc/` into git with QOSFC's current branding extracted from the HTML; mount it
into the existing container and verify the live site is byte-identical. Then: `compose.gen`
rendering one compose file with N services, Traefik in front doing host routing and TLS,
`provision.sh` to scaffold a new client, per-client volumes, `DOTNET_gcServer=0`. Drop the
published `3002:8080` port mapping.
**Milestone: a second establishment goes live with a script and a DNS record.**

### Phase 3 — Hardening
PIN hashing, rate limiting, session tokens, generated per-client PINs forced on first login;
Wallet key out of the DB and into a mounted secret; **slug-scoped Google Wallet class and object
IDs (§2.3 — required before client #2 if they share an issuer)**; Wallet access-token caching and
a cached service instance; `AllowedHosts` and `ForwardedHeaders`; idempotency keys at the till;
indices and WAL; soft delete plus a real GDPR erasure path; tenant-local timezone across all
reporting; asset pipeline (resize, strip EXIF, generate icon variants).

### Phase 4 — Fleet operations
Per-client automated backup and restore; health and uptime monitoring per service; a rollout
script that upgrades the fleet and can pin one client back; `config-export` so admin-UI edits can
be committed back to `clients/`; staff accounts replacing the shared staff PIN.

### Phase 5 — Optional / commercial
Locations for multi-site chains; Apple Wallet (needs an Apple Developer account and a Pass Type ID
certificate — currently a 501 stub at `Program.cs:482`); marketing-list export honouring
`marketing_ok` + `consent_at`; cross-client reporting via a fan-out job; billing/plan limits.
**If you reach ~30+ clients or want self-serve signup, re-open §2.5 before starting this phase.**

---

## 8. Risks and open questions

| Risk | Mitigation |
|---|---|
| Fleet drifts to mixed versions after a partial upgrade | One compose file, one `pull && up -d`; a rollout script that reports per-service image digests |
| Memory cost grows quietly per client | `DOTNET_gcServer=0`, measure RSS per container, set per-service memory limits |
| Google Wallet class collision across containers sharing an issuer | Slug-scoped class and object IDs in Phase 3 — **before** client #2 |
| Extracting QOSFC's branding into `client.json` changes the live site | Phase 2 starts by proving the mounted config renders byte-identical output before any second client exists |
| A bad migration now runs against N databases | Versioned runner that fails loudly, tested in Phase 0; back up volumes before a rollout |
| Stale service workers after the SW rewrite | Version cache keys per client and per release; `skipWaiting` + `clients.claim` are already present |
| Default PINs `1234`/`9999` shipped to a new client | Provisioning generates random PINs; forced change on first admin login |
| Theme presets producing unreadable text | Ship presets as vetted pairs; contrast-check custom overrides |

**Open questions for you:**

1. **How many establishments do you realistically expect in the first year?** This is the only
   input that could overturn the Option A recommendation. Under ~30, Option A is clearly right.
2. **Platform Google Wallet issuer, or does each establishment set up their own?** If shared, the
   class-ID fix in §2.3 is a hard blocker for client #2. If each brings their own, it can wait.
3. **Who owns DNS and TLS?** Wildcard `*.loyalty.example.com` plus CNAMEs for custom client
   domains — Traefik with Let's Encrypt handles this, but the DNS has to exist.
4. **Do any target clients have multiple sites?** If yes, `locations` moves from Phase 5 into
   Phase 1's data model.
5. **Any non-UK or non-GBP clients near term?** That moves currency and locale work to the front
   of Phase 1.
6. **Where does the box live, and what's its RAM ceiling?** Sets the practical client count before
   §2.5 applies.
