# Client configuration

One directory per establishment. Mounted read-only into that client's container at
`/app/config`, and read at startup as the **provisioning seed**.

```
clients/
  <slug>/
    client.json      # seed: identity, locale, programme rules, theme, copy, wallet
    assets/          # optional starter logo/hero, same names as /brand/<name>
```

```yaml
services:
  thebakery:
    image: ghcr.io/andycqos74/coffeeloyalty:latest
    volumes:
      - ./clients/thebakery:/app/config:ro    # seed, read-only
      - thebakery_data:/app/data              # database + uploaded assets
```

Point `CLIENT_CONFIG_DIR` somewhere else if you need to; it defaults to `config/` next to
the binary.

## The database is the source of truth

Resolution order for every value:

```
code defaults  ->  client.json (seed)  ->  DB settings (admin Branding tab — wins)
```

`client.json` only gets a new establishment to a presentable state on first boot. After
that the client edits their own branding from **Admin → Branding**, and those edits win.
Uploaded images go to `/app/data/assets/` on the persisted volume, never to the read-only
config mount, so they survive image upgrades.

Take a backup with **Admin → Branding → Download this client's configuration**
(`GET /api/admin/config-export`) and commit the result back here. That is also how you
clone one client's look as the starting point for another.

## What you can set

Any key in `client.json` may be partially specified — anything you leave out keeps its
default. The full set with current values is what `config-export` returns. Sections:

| Section | Holds |
|---|---|
| `identity` | slug, shop name, short name, contact email |
| `locale` | language, time zone, currency symbol/code/decimal places, week start, date format |
| `programme` | points per unit, till quick-amount buttons, stamp icon and glyphs, default item noun |
| `theme` | ~25 colour tokens plus the two font families |
| `copy` | every visible string across the four pages, plus the markdown privacy policy |
| `wallet` | Google Wallet programme name, class suffix, object prefix |

## Two things to get right per client

**`wallet.classSuffix` must be unique** when clients share one Google Wallet issuer
account. The class is `{issuerId}.{classSuffix}` — a record on Google's servers — so two
containers using the same suffix overwrite each other's pass design. Separate containers
do not help.

**Never change `wallet.objectPrefix` for a live client.** Object ids are
`{issuerId}.{objectPrefix}{token}`; changing it orphans every already-saved pass, which
then silently stops updating. Existing clients stay on `loyalty_`.

Secrets (the Wallet service-account key, PINs) do **not** belong in `client.json` — it is
committed to git. Keep them in the environment or the database.
