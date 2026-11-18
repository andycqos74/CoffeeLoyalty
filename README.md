# Coffee Loyalty

A digital stamp/points loyalty card for a café or restaurant: customers join on their phone and get a card with a
QR code, staff scan it on the till page (`/shop/`) to add stamps or points and redeem rewards, and the owner runs
everything from `/admin/`. ASP.NET Core 9 minimal API, SQLite, plain HTML/JS front end, optional Google Wallet passes.

## Themes

The code is the same for every business; the **theme** sets the look of the customer pages, their starting words
and photos, the shop name, and the colours used for the home-screen app and the Google Wallet pass. Like the Gleam
site engine, each business runs the same Docker image in **its own container** with its own `THEME` and its own
`/app/data` volume, so sites never share customers, settings or photos (this isn't multi-tenant).

| Theme | Business | Look |
| --- | --- | --- |
| `qosfc` (default) | Queen of the South Café | Navy and royal blue, Space Grotesk + Manrope, trophy stamps |
| `cairndale` | The Oak at the Cairndale Hotel & Spa | Teal-slate, cream and claret, Cormorant Garamond + Jost, acorn stamps (design direction 2b "Teal-led — the room after six") |

```
themes/<name>/theme.json       name, label, colours, Google Fonts URL, stamp + reward icons (SVG),
                               starting settings (shop_name, wallet_program_name) and default page content
themes/<name>/public/          theme.css, logo.png and images, served at the site root (/theme.css, /img/...)
templates/join.html, card.html the customer pages, shared by every theme ({{placeholders}}, see PageRenderer.cs)
wwwroot/css/base.css           layout shared by every theme
```

To add a theme, copy `themes/qosfc` to `themes/<new-name>`, change `theme.json` and restyle `public/theme.css`
(it styles the class names used in `templates/`). The app checks the theme when it starts and says what's missing.
A theme that isn't built into the image can be mounted read-only and selected with `THEME_DIR` (see
`docker-compose.yml`).

**Changing defaults later:** a page field nobody has edited always shows the theme's current default, so editing
`theme.json` and redeploying updates every site that hasn't changed that field. Settings (`shop_name`, ...) are only
seeded into a new database and are then changed in Admin → Settings.

## Editing the customer pages (Admin → Pages)

Admin → **Pages** lists every word and photo on the join page, the loyalty card and the privacy policy, next to a
live phone preview that updates as you type. **Save changes** publishes them; **Use theme default** puts a field
back. In text, `[square brackets]` highlight words in the theme's accent colour, `*stars*` make italics,
`**double stars**` bold, and each new line in the headline is a line break. In the privacy policy a line starting
`## ` is a heading. Email addresses become links.

Photos: **Upload a photo…** (JPEG, PNG or WebP; resized in the browser to 2000 px before upload and stored in
`/app/data/media`) or **Choose from library…** (uploads plus the theme's own images). The logo is also used for the
home-screen icon and the Google Wallet pass, so upload a square PNG.

## Animated photos

The join page photo (and the card photo, if the theme shows one) can play as a subtle seamless loop, using the
Gleam site engine's `<animated-image>` player (`wwwroot/lib/animated-image.js`): slow drift, twinkling lights, light
sparkle, pulses along lines, and particles (steam/embers, dust, snow, bubbles) in an area of the photo. Parallax
is left out for now.

1. In Admin → Pages, press **✦ Animate this photo…** under the photo. The admin's browser analyses the photo for
   points of light (`wwwroot/admin/anim-analyse.js`, the same algorithm as Gleam's `src/anim/analyse.js`).
2. Pick a preset (**Gentle drift**, **Subtle**, **Network alive**, **Twinkle only**, **Cinematic drift**), switch
   effects on and off, adjust strength and speed, and drag **Draw the area** on the preview for particles.
3. Or **describe the animation** ("let steam rise gently from the cup") and press **Ask Claude**: the photo, the
   light analysis and your words go to the Claude API, and its proposed settings load into the preview for review.
   Nothing reaches customers until **Save animation**.

The animation belongs to the photo, wherever it's used, and stays the same if you edit the words. Visitors whose
phones ask for reduced motion see the still photo. Ask Claude needs `ANTHROPIC_API_KEY`; without it the presets
still work. It allows 30 requests an hour per site.

## Settings (environment variables)

| Variable | |
| --- | --- |
| `THEME` | Theme folder in `themes/` (default `qosfc`) |
| `THEME_DIR` | Or a full path to a theme folder mounted into the container |
| `DATA_DIR` | Where the database and uploaded photos live (default `data/` next to the app, i.e. `/app/data`) |
| `ANTHROPIC_API_KEY` | Optional: turns on **Ask Claude** in the Animate panel |
| `ANIMATE_MODEL` / `ANIMATE_EFFORT` | Model and effort for Ask Claude (default `claude-opus-5-5`, `medium`). Refusals fall back server-side to the model Anthropic recommends |
| `ANTHROPIC_BASE_URL` | Optional proxy or gateway for the Claude API |

PINs, points per £1, the shop name and Google Wallet credentials are in Admin → Settings.

## Running

```
dotnet run -- --THEME cairndale --DATA_DIR ./data-cairndale
```

Docker: `docker-compose.yml` runs one site; copy the commented `cairndale` service for another site on the same
server (its own port and volume). Existing sites keep their database: with `THEME=qosfc` they look exactly as
before, and their pages become editable in Admin → Pages.
