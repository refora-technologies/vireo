# Vireo in detail

Everything Vireo does, and how. For a quick overview, see the [README](../README.md).

## What Vireo does

**Tabs where you want them.** Top, bottom, left or right. Left and right make a list that can fold
down to icons. The choice is remembered.

**Keeps itself up to date.** Vireo checks for a new version a minute after it starts and then once a day, downloads it in
the background, checks it against the SHA-256 published with the release, and installs it when you close Vireo, or at once
with "Restart to update" in the menu, which brings your tabs back. It installs for you alone, so updating never asks for
administrator rights. Settings → About shows the version and can check now.

**Light on memory.** A hidden tab is frozen after 30 s (no processor time, wakes instantly) and freed
after 10 min (the page is dropped; its title and address stay, and it reloads when you click it).
Tabs playing sound, still loading or pinned are never touched. On computers with 4 GB or less the
timers shorten (10 s / 2 min) and Chromium's low-end mode is switched on. The menu shows total memory,
"See all tabs" (Ctrl+Shift+A) lists every tab with its state, and "Free memory now" does it at once.

**A real download manager.** Downloads started by pages are taken over by Vireo's own engine
(`src/Vireo/Downloads`): the file is split over several connections (8 by default), fast connections
take over the tail of slow ones, everything is written into a pre-sized file, and progress is saved so a
download survives a pause, a dropped connection or closing the browser. Queue, speed limit, naming,
the page's cookies and Referer (so signed-in downloads work) and a progress bar that shows each
connection's piece of the file. Built not to drop in the middle: a connection that sends nothing for 30 seconds is cut and
opened again from where it stopped; a connection that keeps failing hands its part to the others instead of ending the
download; a host that answers "too many requests" gets fewer connections; tries that bring new bytes never count against
the download; and while the computer is offline it waits (up to 30 minutes) for the internet to come back.

**Downloads that outlive the browser.** Closing Vireo while downloads run asks what to do: keep downloading in the
background, or stop them (they carry on from where they stopped next time). In the background the browser and its page
engine close completely and only the downloader stays, with an icon in the notification area: about 40 MB of memory.
Its tooltip shows the progress; a click opens a small window with every download (pause, resume, cancel, paste a new link)
and "Open Vireo", which brings the browser back in the same process without interrupting anything. When everything is done
it shows a notice and exits a minute later. Settings, Downloads, sets the answer for every time.

**Saves video and audio from pages, natively.** Vireo watches what a page plays (`src/Vireo/Media`) and
puts a **Download** button right on each video or audio player, with a menu of qualities. The toolbar
button counts what a page offers and opens a Media panel with the same choices. It understands plain
files, HLS streams (encrypted AES-128 ones too, and live streams that are recorded until you stop them)
DASH streams (.mpd) and videos whose picture and sound come separately; those are fetched together and
joined into one MP4 by Vireo's own joiner, with no outside tools. Streams made of MPEG-TS pieces (the most
common kind) are converted to MP4 by Vireo's own converter too (H.264 video, AAC sound). YouTube works the same way: the video is found from
the page address, every quality up to 4K is listed, and "Audio only" saves an .m4a. The button can be
set to Hover / Always / Off, and switched off per site, in Settings. Protected (DRM) streams are
recognised and never downloaded.

**Streams that are awkward on purpose.** Playlists and pieces are fetched like a browser would (redirects followed,
relative addresses resolved against where the playlist really came from, the player's address sent as the referer, with
two other honest tries if refused). A playlist served as a picture, and pieces with a PNG/GIF/JPEG glued in front, are
recognised and cleaned. A connection that goes quiet for 30 s is dropped and retried, so one stuck piece cannot freeze a
download. A few pieces that are gone from the server are left out and counted instead of failing the whole file. Live
streams are recorded until you press Stop, the stream says it is over, it stops growing, or its playlist disappears.
A master playlist that lists each size once per kind of sound shows each size once.

**Finding streams that hide.** Every answer a page or any of its frames receives is judged by its own type, whatever its
address looks like, so a playlist named `poster.png`, `list.txt` or `manifest.xml` is found. Small answers with no
useful type (an `/api/source?id=7` sent as plain text) are looked inside: one that starts `#EXTM3U` or holds an
`<MPD>` is a stream. Small JSON answers from a site's own API are read for `.m3u8`/`.mpd` addresses, so a stream can be
offered before the player asks for it; such a link is only shown once it answers as a stream. Playlists asked for by
service workers count too. A player that writes its playlist in script and gives the video a `blob:` address
(so the playlist itself never crosses the network) is caught as the page makes it, and the playlist is kept in memory
for the download. A download that names itself a video (`filename=ep01.mkv`) is one whatever its type, and a
video inside a player frame is fetched with that frame as the referer its host expects. Only a capped number of small
answers per page are looked inside, so busy pages stay quick. Whatever a `<video>` or `<audio>` element itself loads is
offered whatever its address or type, and any request whose path names a `.m3u8`/`.mpd` playlist is tried as one even
when the server labels it oddly. The page's own text is read too: an `og:video` tag, a JSON-LD `contentUrl`, stream links
written into its markup or scripts, and the DASH manifest Facebook embeds in its page; such links are only shown once they
answer as media. Addresses that list their qualities with commas (`video-,360p,720p,.mp4.urlset/master.m3u8`, the form
nginx-vod and Akamai servers use) are read whole, so such a site's video is offered as soon as the page opens, before any
advert in front of it has finished. A video offered as two playlists, one per codec, is one entry: H.264 wherever it
exists (it plays on every computer), AV1 or HEVC only for sizes that have nothing else, and each size names its codec.

**Ad and tracker blocking, built in.** Reads the standard filter-list format: EasyList, EasyPrivacy, Peter Lowe's ad
servers and the AdGuard DNS filter (ad, tracker and device-telemetry hosts) are downloaded and refreshed in the background;
a small list of Vireo's own works with no download. Whole-host rules are kept as compact hashes (190,000 hosts in about
5 MB). Every request is checked before it leaves and a refused one fails like a dead connection (so pages and ad scripts
cannot tell it "got an answer"); leftover ad boxes are hidden by the names the page uses, and the shield shows what was
stopped, counting each address once (a script retrying the same blocked address counts once, not thousands of times). Ad
code that retries a refused address in a loop (one news site asked for the same ad configuration about 300 times a second)
is refused more and more slowly after three tries, up to five seconds each, so the loop cannot load the page engine or
make the window stop answering. On adblock.turtlecute.org Vireo blocks 115 of 130. The two it lets through are the "ads.js" / "pagead.js"
script checks: those names are the bait sites use to detect blockers, and refusing them everywhere starts "turn off your
ad blocker" walls. Video files and stream playlists are never refused, so players are not broken. Per-site and global
switches; switch the community lists off for a much smaller footprint.

**Privacy.** Tracking tags (utm_, fbclid, gclid...) are removed from links. Optional HTTPS-only with a
clear "this site isn't available securely" page. Tracking prevention level (Off / Basic / Balanced /
Strict). Optional guard against network-address leaks through video-call technology. Optional "forget
everything when closing". Private windows share no cookies, storage or history with the normal one,
and leave nothing behind when closed: no history, session, site icons, saved passwords or form data, and their download
list lives only in memory (a closed private window's unfinished downloads are cancelled and their pieces deleted; you are
asked first). The first private download asks once where private downloads should go: the normal downloads folder, or a
folder of your own (a USB stick, an encrypted drive); every private download goes there after that, and Settings can
change it. A private download's file carries the "from the internet" mark without naming the site it came from.

**Everything else.** Pick Google, DuckDuckGo, Bing or Brave for searches in Settings (Google is the default; the
start page follows it). Search ideas appear as you type, always from the engine you picked and never in private
windows (one switch turns them off). Bookmarks (the star, Ctrl+D), history, find in page with match counts (Ctrl+F),
zoom that is remembered per site, print, developer tools, pinned tabs, and a tab right-click menu (reload, duplicate,
pin, mute, close others). The page right-click menu adds "Download link / image / video with Vireo" and "Search for
the selection".

**Page Grabber** (Ctrl+Shift+G, or right-click a page): lists every picture, document, archive and sound on the
page, sorted by kind and filterable, and downloads the ones you tick. Streams Vireo found on the page come first.

**Pop-ups and pushy pages.** A window you clicked for opens as a tab (in front) and stays connected to the page that
opened it, so "sign in with ..." pop-ups work. Blocked and counted on the shield: windows nobody clicked for, ones aimed
at ad networks, a second window an embedded ad opens for one click, a window a script opens on the back of a link click,
links made and clicked by scripts, invisible layers laid over the page to catch clicks, and redirects to listed ad
addresses. A page that keeps showing message boxes is cut off after two, and notification requests are refused.
Message boxes ("alert", "confirm", "leave this page?") are drawn by Vireo.

**Keeps going.** Tabs are brought back on the next start (only the tab you were on loads; the others load when
clicked). A crashed page is reloaded once; if the whole browser engine dies, Vireo starts a new one and the tab you
were on comes back. A dropped download connection is retried with back-off before a download is called failed.
A watchdog times how quickly the window answers; if it ever stops answering for more than two seconds, what it was
doing is written to `%LOCALAPPDATA%\Vireo\crash.log` (so are errors, instead of the app closing).

**Your browser.** Vireo can be made the default browser (Settings, or `Vireo.exe --register` once, then confirm in
Windows Settings), and a link opened from another program goes to the running window instead of a second copy.
Bookmarks and history come over from Chrome, Edge, Brave, Vivaldi, Opera and Firefox, or from an exported
bookmarks.html. Passwords and form data are saved by the browser engine's own password manager (the one Microsoft
Edge uses), never in private windows; Vireo itself never reads or stores passwords, and never opens another browser's
password store. "Clear browsing data" removes history, cookies, cache or passwords by hand.

**Extensions.** Load an unpacked extension folder, or a .zip / .crx file (Settings, or Ctrl+Shift+E). Content scripts
and background workers run as usual; Vireo draws each extension's button next to the address bar and opens its popup
under it, sized to fit. Extensions from the web stores have to be downloaded as a file first.

**Start page.** Your sites, only the ones you put there with "Add site" (up to ten; hover a tile to edit or remove it;
your history is never shown there), live download speed with a minute of history, what is downloading, what was blocked, and a box that downloads any
pasted link (a stream, a video page or a file) at the best quality.

**Players that hide their stream.** Players that fetch their playlist from a Web Worker (Twitch's and Kick's) are watched
through the browser engine's DevTools connection, since the engine reports nothing a worker loads. A playlist that answers
only once (a single-use address) is served from the copy the page itself received. Bilibili's video, described inside its
page rather than in a playlist, is read from the page (each quality and the sound as whole files, joined). Pieces of
DRM-encrypted streams are never offered as files. The same video found twice (two servers, or a master playlist and one of
its parts) is listed once.

Checked on public sites (detected, and a download run to the end and verified to play): Twitch and Kick live, Rumble
(video and live), Vimeo (videos without DRM), TED, Bilibili, TikTok, Facebook, Twitter/X, Odysee, PeerTube, Streamable,
archive.org, CNN, Euronews, DW live TV, SoundCloud, Bandcamp, 9GAG, Coub, and pages built on Wistia, Mux/Video.js,
JW Player, hls.js, Shaka and Bitmovin players. Vimeo videos its owners protect with DRM are recognised and not downloaded.
Not verified: Dailymotion (a sign-up wall stopped the automated player), Reddit (a robot check).

**Windows, profiles and sync.** Ctrl+N opens another window; all windows share one download list, history, settings and
filter set, and the next start brings every window back. Drag a tab away from the tab strip (or right-click it and choose
"Move to new window") and it gets a window of its own with its page alive: what was typed, where it was scrolled, and its
back and forward history. While it is dragged, a small picture of the page follows the mouse; let go over another
window's tabs and it joins that window at that spot (a window left with no tabs closes). Profiles (the round badge next to the menu) are separate browsers for work and personal: each
runs as its own Vireo with its own folder, so sign-ins, cookies, history, bookmarks, settings and extensions never mix;
each can have a desktop shortcut. Sync needs no server of ours: choose a folder that OneDrive, Google Drive or Dropbox keeps
in step and a passphrase; each computer writes one file there, locked with AES-GCM under a key made from the passphrase
(PBKDF2-SHA256, 310,000 rounds), and reads the others'. Bookmarks, history (deletions too), start-page sites and the tabs
open on your other computers travel; passwords never do.

**Quiet notices.** "Download finished" and similar notices appear in the corner of the page only while Vireo is the window
in use. If another program is in front, nothing pops up over it: Vireo's taskbar button lights up and the notice is shown
when you come back.

**Safer downloads.** Every saved file carries the "from the internet" mark Windows reads (Mark of the Web), so SmartScreen
checks programs before they first run and Office opens documents in Protected View. Pages and downloads are checked
against Microsoft Defender SmartScreen. A site that starts download after download by itself is stopped after the third
in a minute until you allow it.

**Hands.** The first click in the address bar selects it all (a quick double-click too); Alt+Enter opens what you typed in
a new tab, Ctrl+Enter turns "example" into www.example.com, Shift+Delete forgets the highlighted history suggestion, and
right-click offers "Paste and go". Tabs can be dragged to reorder (pinned ones stay in front). The mouse's back and forward
buttons work over the page and over Vireo's own bars. The menu shows only the memory total; "See all tabs" (Ctrl+Shift+A)
opens a Tabs panel to search every open tab, see which are awake or asleep, switch to one, put it to sleep or close it. An invisible advert sheet laid over a player is made click-through,
so the click reaches the player instead of being swallowed.

## Shortcuts

| | |
|---|---|
| Ctrl+T / Ctrl+W / Ctrl+Shift+T | new, close, reopen closed tab |
| Ctrl+L, Alt+D, F6 | address bar |
| Ctrl+Tab, Ctrl+1…9 | switch tabs |
| Alt+Left / Alt+Right, F5 | back, forward, reload |
| Ctrl+D, Ctrl+Shift+O, Ctrl+H | bookmark, bookmarks, history |
| Ctrl+J | downloads |
| Ctrl+Shift+M, Ctrl+Shift+G | videos on the page, Page Grabber |
| Ctrl+F, F3 / Shift+F3 | find in page, next / previous match |
| Ctrl++ / Ctrl+- / Ctrl+0 | zoom in, out, reset (kept per site) |
| Ctrl+P, F12 | print, developer tools |
| Ctrl+Shift+E | extensions |
| Ctrl+Shift+A | search open tabs |
| Ctrl+N, Ctrl+Shift+W | new window, close the window |
| Ctrl+, | settings |
| Ctrl+Shift+N | private window |
| F11 | full screen |

## Known limits

- DASH streams cut into several periods are read as their longest period (ads between are skipped); live DASH with numbered pieces is recorded from the last 90 seconds
- TS streams with other codecs (HEVC, MP3, AC-3) are saved as .ts unless ffmpeg is next to Vireo
- The in-page Download button now also works inside embedded players (frames); this has only been checked by hand
- A joined video+audio download keeps both files' progress across a restart, but this is only tested by pausing
- Passwords cannot be brought over from another browser: the engine offers to save each one at your next sign-in
- Extension buttons have no badge text, and extensions cannot open windows of their own
- DNS-over-HTTPS (the engine does not expose a switch for it)
