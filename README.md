# Media Controls website

A small static Astro presentation for Media Controls for PowerToys Command
Palette and its browser companions. This `pages` branch starts without the
application's Git history.

## Develop

Use Node.js 22.23.4 or later (Node 24 LTS is recommended; `.nvmrc` pins 24.19.0).

```powershell
npm ci
npm run dev
```

Open the Local URL printed by Astro. The default path is
`http://127.0.0.1:4321/MediaControlsExtension/`.

```powershell
npm run check
npm run build
npm run preview
```

The production output is in `dist/`. No server runtime is needed.

## Hosting

The default site origin and base path target GitHub Pages at
`https://jiripolasek.github.io/MediaControlsExtension/`. This does not enable
hosting, push a branch, or publish anything by itself.

For a different host, set `SITE_URL` to its origin and `BASE_PATH` to its path
before building. For example, a dedicated subdomain uses `BASE_PATH=/`.

```powershell
$env:SITE_URL = 'https://media.example.com'
$env:BASE_PATH = '/'
npm run build
```

To host under the personal website, set `SITE_URL=https://jiripolasek.com` and
choose a base path such as `/media-controls`.

## Content and artwork

- `src/pages/index.astro`: the landing page.
- `src/pages/companions.astro`: companion preview and setup.
- `src/pages/players.astro`: supported players, feature tables, and connection help.
- `src/data/players.ts`: compatibility rows adapted from the application's
  `docs/user/GSMTC-Compatibility.md`; keep the two guides aligned when updating support.
- `src/pages/privacy.astro`: shared desktop, browser companion, and website privacy information.
- `src/content/privacy.md`: the policy shared by the website and store submission kit.
- `src/pages/support.astro`: companion connection help, removal, and issue reporting.
- `src/data/site.ts`: shared links and browser listing URLs.
- `src/styles/global.css`: shared visual styles and responsive layouts.
- `src/components/ThemeControl.astro`: the header's single icon button, switching
  Light / Dark and using the `astro-theme-toggle` client for its ripple.
- `src/components/ThemeScript.astro`: theme initialization before page rendering,
  device theme changes, and local preference storage.
- `src/assets/`: supplied background artwork and the actual desktop screenshot.
- `src/assets/browsers/`: official browser icons from the Windows browser distributions.
- `src/components/FeatureScreenshots.astro`: Command Palette and Dock views cropped
  from the actual desktop screenshot, with a link to the complete image.
- `public/images/social.png`: supplied GitHub social artwork.
- `public/logo.svg`: the existing Media Controls logo.
- `src/assets/jiri-polasek.png`: the supplied author logo, shown in monochrome
  using its transparency as a CSS mask.

Astro optimizes the source images to WebP at build time. Sora is bundled through
`@fontsource-variable/sora` and served from the same site, including Latin-extended
glyphs. Accent lines use a lighter Sora weight. The site makes no requests to
external font services and includes no analytics.
The font's license is included in `public/fonts/Sora-OFL.txt`.

Body text is 16px. Supporting labels and captions stay at or above 14px at all
breakpoints. The website uses no cookies, analytics, or tracking. The theme follows
the device until a visitor chooses Light or Dark. The button always switches to
the opposite visible theme; there is no separate system step in the cycle.
An explicit choice is saved in local storage under `media-controls-theme`.
Existing Auto preferences continue to follow the device until the button is used.
Both themes retain the gold button color; text accents
use a deeper gold in light mode for contrast.
The ripple respects reduced motion and falls back to an immediate switch when
View Transitions are unavailable. Theme preferences stay with the local controller
so device defaults and explicit choices keep their behavior. The package's MIT license is
included in `public/licenses/astro-theme-toggle.txt`.

Browser icon artwork comes from Chrome and Edge's `VisualElements/Logo.png` and
Firefox's `browser/VisualElements/VisualElements_150.png`. The component accounts
for transparent tile padding and preserves shadows without redrawing the marks.
Browser names and icons belong to their respective owners.

Browser companions are marked as developer previews because store listings are
not available in the supplied source. Add their verified URLs to
`src/data/site.ts` and update the preview copy when they are published. Preview
setup needs a companion-enabled application build; it is not promised by the
current Store release.

The original graphics are enough for this first version. A current clean
Command Palette screenshot and a browser popup screenshot would be useful
future replacements or additions.

## Checks

For the store listing URLs, policy synchronization command, and remaining
submission steps, see [Store submission](docs/store-submission.md).
The shared policy distinguishes local Windows/iTunes media, optional remote VLC
connections and credentials, browser tab data, local logs and artwork, and the
website. Its browser-store disclosure fields remain scoped to the companion.

Run `npm run check`, `npm run build`, and `git diff --check` before committing.
Verify narrow-screen layout, keyboard access, screenshot links, copy feedback,
and all install links in the browser when changing the page.
