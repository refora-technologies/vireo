# Developing Vireo

## What you need

- Windows 10 (1809 or later) or Windows 11, x64
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- The WebView2 runtime (part of Windows 11 and up-to-date Windows 10)
- For the browser tests: [Node.js](https://nodejs.org/) 20 or later (it runs the local test server)
- For the installer: [Inno Setup 6](https://jrsoftware.org/isinfo.php)

## Build and run

```
dotnet build src/Vireo -c Release
src\Vireo\bin\Release\net10.0-windows\Vireo.exe
```

Your data lives in `%LOCALAPPDATA%\Vireo` (set `VIREO_DATA` to a folder of your own to keep a development profile apart).

## Tests

```
dotnet test tests/Vireo.Tests                                         # the unit tests: engine, filters, streams, DASH, the joiner, updates, ...
VIREO_LIVE=1 dotnet test tests/Vireo.Tests --filter RealStreamTests   # public test streams (needs the internet)
```

### Driving the real browser

`Vireo.exe --selftest <out-dir> <base-url>` opens a window and drives itself for several minutes: it opens pages, uses the
keyboard, moves the tabs to every side, puts tabs to sleep and measures memory, saves a download from a signed-in page over
several connections, blocks ads on a local shop page, tests the privacy features and a private window, finds and saves
videos and streams, and checks bookmarks, history, suggestions and find. It also opens a real YouTube page (skipped
offline), presses the button on its player and checks the saved file plays. It writes screenshots and `selftest.json` to
the out folder, and exits 0 when everything passed. Anything it downloads goes into the out folder.

It needs the local test server, which serves the sample pages and media in `tests/server/media`:

```
node tests/server/server.mjs          # port 4173
set VIREO_DATA=.data\selftest
src\Vireo\bin\Release\net10.0-windows\Vireo.exe --selftest out\shots http://127.0.0.1:4173
```

`VIREO_STEPS=windows,privacy` runs only some of the steps.

The `welcome` and `manager` steps cover the welcome page and the download-manager features. "Start with Windows" is written
to a key of the test's own (`HKCU\Software\VireoSelfTest\Run`, removed at the end), never to Windows' Run key; the
notification area's messages are recorded, not shown; "when done" only records what it would have done; and the
downloader started alone (`--downloads --quiet`) runs in a profile folder inside the out folder.

Public sites (needs the internet, only on request): `VIREO_STEPS=sites` opens each page in the survey list, presses play
and its Download button, and reports what was found. `VIREO_SITES=<names>` picks some of them; `VIREO_SURVEY_URL=<page>`
adds one page of your own; `VIREO_SURVEY_DOWNLOAD=1` saves the smallest quality into the out folder and checks it plays;
`VIREO_NETLOG=1` writes every answer the page received and every request the blocker stopped to `netlog-*.txt`, for
working out why a site's video is not found.

Freezes (needs the internet, only on request): `VIREO_STEPS=freezes` opens busy, ad-heavy news sites in six tabs while a
300 MB throttled download runs over every connection, switches and scrolls for `VIREO_FREEZE_MINUTES` (default 4), and fails
if the window ever stops answering for a second. It prints how many requests were blocked and which addresses repeat. Copy
the community filter lists into the test profile's `filters` folder first, or the busy sites are not blocked the way they
are for people. To see where a freeze happens, install dotnet-stack (`dotnet tool install dotnet-stack --tool-path .tools`)
and set `VIREO_STALL_STACKS` to `.tools\dotnet-stack.exe`: every stall then writes all thread stacks, taken while the window
is still stuck, to `stalls.log` in the profile folder.

The test server also serves made-up files of any size with ranges and throttling: `/generated/<bytes>.bin?throttle=<KBps>`.

### Filter lists for the tests

`curl -L -o lists/easylist.txt https://easylist.to/easylist/easylist.txt` (and `easyprivacy.txt`) lets the real-list test
run; without them it is skipped.

### Updates

The updater reads `https://api.github.com/repos/refora-technologies/vireo/releases/latest`. Set `VIREO_UPDATE_FEED` to the
address of a JSON file shaped like GitHub's answer (a local test server) to try an update end to end without publishing.

## The installer

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This runs the tests, publishes Vireo (self-contained, ready-to-run), builds `out\installer\VireoSetup.exe` with Inno Setup
and writes its checksum, `VireoSetup.exe.sha256`. The version comes from `<Version>` in `src/Vireo/Vireo.csproj`.

The icon and the installer's pictures are drawn from the one master mark, `src/Vireo/Assets/vireo-mark.svg`:

```
cd scripts
npm install
node make-images.mjs
```

## Where things are

```
src/Vireo/
  Downloads/   segmented download engine, speed limiter, naming
  Media/       finding videos on pages, HLS, YouTube, the MP4 joiner
  Blocking/    filter engine (Adblock Plus syntax), list updater
  Services/    settings, library (bookmarks + history), URL input and cleaning, tab sleep policy
  Controls/    tab strip, tab header, download rows and panel, library panel
  MainWindow.*.cs   the window, split by what each part does
  Assets/      start page, built-in filters, logo, overlay.js (the in-page button)
tests/Vireo.Tests/   unit tests
tests/server/        the local test server and its sample pages and media
installer/           setup.iss and the installer's pictures
website/             the website (published to GitHub Pages)
scripts/             drawing the icon and installer images
```
