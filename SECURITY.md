# Security

Vireo is a web browser: it opens pages nobody has vetted, downloads files, and updates itself. Those are what this policy
is about.

## Reporting a problem

If you find a way to misuse Vireo, please tell us **privately**: use
[Report a vulnerability](https://github.com/refora-technologies/vireo/security/advisories/new) on this repository's
Security tab. Only you and Refora Technologies can see the report, and we can work on the fix with you there. If you would
rather not use GitHub, email **reforatech@gmail.com** with "Vireo security" in the subject. Please don't open a public
issue about it until it is fixed.

It helps to include:

- what an attacker could do, and what they would need first (a page you open, a file you download, a position on the
  network, an account on the same PC)
- the steps to reproduce it, or a proof of concept
- the Vireo version (Settings → About) and your Windows version

## What happens next

- We reply within three working days to say we have it.
- We confirm the problem, work on a fix, and keep you told how it is going.
- The fix goes out in a release, and the release notes describe the problem once people have had the chance to update.
- With your permission, we thank you by name in those notes.

Please give us a reasonable time to fix it before you talk about it publicly, and don't access other people's data or
disrupt other people's computers while testing.

## What is in scope

- Vireo's own code: the window, the download manager, media detection and saving, blocking, sync, profiles, the updater
  and the installer.
- How Vireo configures the WebView2 engine (permissions, pop-ups, downloads, private windows).

Problems in the Chromium engine itself belong to Microsoft (WebView2) or the Chromium project; we will help you get them
to the right place.

## How Vireo protects you

- **Updates:** downloaded only from this repository's published releases, and run only if the file matches the SHA-256
  published with the release. Vireo installs per user, so an update never runs with administrator rights.
- **Downloads:** every saved file carries Windows' "from the internet" mark, so SmartScreen checks programs before they
  first run and Office opens documents in Protected View. Pages and downloads are also checked against Microsoft Defender
  SmartScreen. A site that starts download after download by itself is stopped.
- **Pages:** pop-ups and surprise new tabs are blocked, insecure connections are upgraded or asked about, and private
  windows share nothing with normal ones.

## Supported versions

Fixes go into the newest release. Vireo checks for updates every day and installs them when it closes, so the newest
version is the one to test and the one we support.
