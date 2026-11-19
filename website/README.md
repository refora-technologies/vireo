# Vireo's website

Everything in this folder is published to GitHub Pages as it is, when a change to it is merged into `main` (see
`.github/workflows/pages.yml`). Once the repository is public it is served at
`https://refora-technologies.github.io/vireo/`, or at a custom domain (add a `CNAME` file here with the domain, and point
the domain's DNS at GitHub Pages).

## Working on it

1. Make a branch: `git checkout -b website/<what-you-are-doing>`.
2. Open `index.html` in a browser, or serve the folder (`npx serve website`) to see it as it will be published.
3. Open a pull request into `main`. It is reviewed, and published when merged.

To use a static site generator (Astro, Eleventy, Vite …), keep its source here and add its build command to the "Build"
step of `pages.yml`, uploading its output folder instead of `website`.

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
- **Privacy:** no analytics, trackers or third-party fonts and scripts. Vireo blocks them, and its own site should not need
  them.
- **Wording:** describe saving videos as watching offline what you have the right to; never show or name paid streaming
  services or piracy sites. See `TERMS.md`.
