# Contributing to Vireo

Thank you for wanting to help. Vireo is made by Refora Technologies and is open to everyone's help: fixes, testing on
sites and computers we don't have, better wording, and ideas.

## Ways to help

- **Report a bug** as an [issue](https://github.com/refora-technologies/vireo/issues/new/choose), with the form filled in.
  For a video Vireo does not find, the page's address is the single most useful thing to include.
- **Suggest an idea** as an issue with the "Idea" form.
- **Test on your sites and your computer**, especially older or low-memory laptops.
- **Send a change** as a pull request, as below.

Security problems are the exception: report them privately, as [SECURITY.md](SECURITY.md) explains.

## What Vireo will not take

- Anything that gets around copy protection (DRM). Vireo recognises protected content and refuses it, and that stays.
- Anything that reads other programs' data, such as another browser's saved passwords.
- Tracking, analytics or telemetry of any kind.

## Before a bigger change

For anything larger than a fix, such as a new panel, a new setting, or a change to how something looks or works, open an
issue first. Vireo stays deliberately simple, and it is better to agree on the shape of a change before you spend time on
it.

## Building and testing

You need Windows 10 or 11 (x64) and the .NET 10 SDK; Node.js for the browser self-test; Inno Setup 6 for the installer.

```
dotnet build src/Vireo -c Release
dotnet test tests/Vireo.Tests
```

[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) explains the browser self-test (a real window driving itself through every
feature), the local test server and the installer.

## Pull requests

1. Work on a branch of your own, named for what it does (`fix/tab-drag-flicker`, `website/home-page`).
2. Keep a pull request to one change, and say in its description what it changes and why, and how you checked it.
3. The unit tests must pass; for anything you can see or click, run the self-test steps it touches and include a
   screenshot.
4. Every pull request is reviewed by a maintainer before it is merged. Only maintainers publish releases.

### Style

- Match the code around you: plain names, small methods, and comments that say *why*, written as sentences.
- Words people read in Vireo are plain and short: "Download", "Asleep", "Restart to update", not jargon.
- New UI follows the existing look (`src/Vireo/Themes/Theme.xaml`): the dark palette, the teal accent, Plus Jakarta Sans.

## The website

The website lives in [`website/`](website/) and is published by Netlify at https://vireo.reforatech.com when a change to it is merged into `main` (see `website/README.md`).
Its pull requests follow the same rules.

## Code of conduct

Everyone taking part is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).

By contributing, you agree that your contribution is licensed under the [GNU General Public License v3.0](LICENSE), like
the rest of Vireo.
