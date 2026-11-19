# Vireo's website

Everything in this folder is published by **Netlify** as it is (no build step), at **https://vireo.reforatech.com**,
whenever `main` changes. Pull requests get a preview address of their own. The settings are in
[`netlify.toml`](../netlify.toml) at the repository's root: what is published, the security headers, and caching.

## Working on it

1. Make a branch: `git checkout -b website/<what-you-are-doing>`.
2. Serve the folder (`python -m http.server --directory website 8090`, or `npx serve website`) and open it.
3. Check it as Netlify will serve it: `cd scripts && node check-site.mjs` (the security headers from `netlify.toml`,
   missing files, the 404 page, and how the rules files are served).
4. Open a pull request into `main`. Netlify builds a preview; it is reviewed, and published when merged.

To use a static site generator (Astro, Eleventy, Vite …), keep its source here and set its build command and output
folder in `netlify.toml`.

## The rules folder

`rules/v1/youtube.json` (and its signature, `youtube.json.minisig`) is what Vireo downloads to stay ahead of YouTube's
ad changes without a new release. Vireo checks every copy against Refora's rules key (built into Vireo) before using it,
and takes only a version newer than what it has. To publish new rules:

1. Change the rules (key names and selectors only: it is data, never code) and raise `"version"` by one.
2. Sign it: `dotnet run --project tools/vireo-sign -- sign --secret <your secret key> website/rules/v1/youtube.json`.
   The tool refuses a malformed file, and checks the new signature against Vireo's key before writing it.
3. Check it as Vireo will: `dotnet run --project tools/vireo-sign -- verify website/rules/v1/youtube.json`.
4. Copy the same file into `src/Vireo/Assets/rules/youtube.json`, so the next release carries it built in.
5. Merge into `main`. Every Vireo picks it up within about twelve hours.

The address (`/rules/v1/`) is built into every Vireo copy: never move or rename it. A future format goes in `/rules/v2/`
beside it. The rules files are cached for five minutes only (see `netlify.toml`).

## Things to keep

- **The Download link** points at `https://github.com/refora-technologies/vireo/releases/latest/download/VireoSetup.exe`,
  which is always the newest *published* release, so the site never offers something that is not released.
- **The look** is the browser's: the colours in `style.css` (`--bg`, `--accent` …) are Vireo's own, the font is Plus
  Jakarta Sans (licence in `assets/OFL.txt`), and the logo is `assets/vireo-mark.svg`, copied from
  `src/Vireo/Assets/vireo-mark.svg` (the one master mark: change it there, then copy it here).
- **Pictures of Vireo are drawn, not screenshotted:** the window, the downloads list, the notices and menus on the page
  are HTML and CSS (`.app` and `.window` in `style.css`), with made-up file names and `example.com` sites. They stay sharp
  at any size, weigh almost nothing, and can never show something from a real computer (a screenshot can catch the
  desktop, a clipboard, a history). Describe only what Vireo really does.
- **The social preview** (`assets/social.png`, 1200×630) is what link previews show; redraw it if the headline changes.
- **Scripts:** only `site.js`. The security policy in `netlify.toml` refuses inline scripts and anything from another
  site, so keep scripts in files here.
- **Privacy:** no analytics, trackers or third-party fonts and scripts. Vireo blocks them, and its own site should not need
  them.
- **Wording:** describe saving videos as watching offline what you have the right to; never show or name paid streaming
  services or piracy sites. See `TERMS.md`.
