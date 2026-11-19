# Privacy

Vireo is made by Refora Technologies to keep what you do on the web yours. This page says exactly what Vireo stores, and
everything it sends anywhere.

## Vireo collects nothing

Vireo has no accounts, no analytics, no telemetry and no crash reporting. Refora Technologies receives nothing about you,
your browsing or your downloads. There is no Vireo server.

## What stays on your computer

Your history, bookmarks, settings, start-page sites, download list, sessions (the tabs to bring back) and site data
(cookies, storage, saved sign-ins kept by the browser engine) are stored in `%LOCALAPPDATA%\Vireo`, for your Windows account
only. You can clear them at any time (Settings → Privacy → Clear browsing data), have them cleared each time Vireo closes,
or delete them when uninstalling.

**Private windows** keep nothing: no history, cookies, site data, session or download list is written to disk, and all of
it is gone when the window closes. Only the files you choose to download are kept, in the folder you picked for private
downloads. A file saved in a private window carries Windows' "from the internet" mark (so Windows still checks it before
it runs) but not the address it came from.

**Offering links you copy** (off unless you turn it on, in Settings → Download manager or on the welcome page): when you
copy something in another program, Vireo looks at it on your computer to see whether it is a single link to a file. If it
is, Vireo offers to download it; if not, it is ignored at once. Nothing you copy is stored, logged or sent anywhere.
Copies made in Vireo itself, and anything a password manager marks as private, are not read at all.

## What Vireo sends, and to whom

Apart from the sites you visit, Vireo only connects to these, and only for these reasons:

| To | When | What is sent |
|---|---|---|
| vireo.reforatech.com (Vireo's website) | A minute after starting, then twice a day | A plain request for the public YouTube rules file and its signature. Nothing about you; the site's host sees your IP address, as with any request. |
| Your search engine (Google, DuckDuckGo, Brave, …) | As you type in the address bar, if search suggestions are on (never in private windows) | What you have typed so far |
| Microsoft Defender SmartScreen | When you open a page or a download, as Microsoft Edge does | The address, to check it against Microsoft's lists of phishing and malware |
| The filter-list publishers (EasyList, Peter Lowe, AdGuard) | When the community blocking lists are on, about once a day | A request for the list; nothing about you |
| GitHub (`api.github.com`, `github.com`) | A minute after Vireo starts, then once a day, to look for updates | Vireo's version number, in the request |
| YouTube, Bilibili | Only when a page you opened there plays a video, to list its qualities | The video's address |

Sync, when you turn it on, writes to a folder in **your own** cloud storage (OneDrive, Google Drive, Dropbox), encrypted
with a key made from your passphrase. Neither Refora Technologies nor your cloud provider can read it.

## The browser engine

Pages are shown by Microsoft's WebView2 runtime, which is part of Windows. Microsoft may collect diagnostic data from it
according to your Windows privacy settings (Settings → Privacy & security → Diagnostics & feedback); Vireo does not add to
it.

## Passwords

Saved passwords are kept by the browser engine's own password manager, the same one Microsoft Edge uses, encrypted by
Windows for your account. Vireo itself never reads, stores or moves passwords, and never opens another browser's
password store.

## Questions

Open an [issue](https://github.com/refora-technologies/vireo/issues), or email **reforatech@gmail.com**.
