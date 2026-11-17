# Releasing Vireo

Only maintainers release. A release is a version tag, built by GitHub Actions into a draft, tried on real machines, then
published by hand. Installed copies of Vireo only ever see published releases.

## 1. Prepare

1. Merge everything that goes into the release into `main`.
2. Set the version in `src/Vireo/Vireo.csproj` (`<Version>1.2.0</Version>`). Versions are `major.minor.patch`:
   patch for fixes, minor for new features, major for changes that break something people rely on.
3. Write `release-notes/1.2.0.md`: what changed, in plain words, for the people using Vireo.
4. Run everything on a Windows machine:

   ```
   dotnet test tests/Vireo.Tests
   node tests/server/server.mjs                     # in a second window
   set VIREO_DATA=.data\selftest
   src\Vireo\bin\Release\net10.0-windows\Vireo.exe --selftest out\shots http://127.0.0.1:4173
   ```

   The self-test must end with exit code 0. Look through `out\shots` for anything that looks wrong.
5. Commit the version and the notes ("Vireo 1.2.0") and merge to `main`.

## 2. Tag

```
git tag v1.2.0
git push origin v1.2.0
```

The **Release build** workflow checks that the tag, the version and the notes agree, runs the tests, builds
`VireoSetup.exe` and its `.sha256`, attests the build, and creates a **draft** release with both files.

## 3. Try the draft

Download `VireoSetup.exe` from the draft and check, on at least one machine that had the previous version:

- it installs over the previous version and keeps the tabs, history and settings;
- Vireo starts, and Settings → About shows the new version;
- a fresh install on a machine without Vireo works, and so does uninstalling.

## 4. Publish

Publish the draft on GitHub. From then on, every installed Vireo finds it within a day (or at its next start), downloads
it, checks it against `VireoSetup.exe.sha256`, and installs it when it closes.

Then check it from the outside, as an installed Vireo sees it (no sign-in): the release is found, its installer and checksum
are found by name, and the installer matches the checksum.

```
VIREO_LIVE=1 dotnet test tests/Vireo.Tests --filter Published_release_is_one_the_updater_can_install
```

The names are what keep updates working: the tag is `vX.Y.Z`, and the files are always `VireoSetup.exe` and
`VireoSetup.exe.sha256` (the release workflow names them so; never rename them by hand).

## If something is wrong after publishing

Publish a fixed version (1.2.1) as soon as possible. Do not delete a published release that people may already have
downloaded; mark it in its notes instead. The updater never moves anyone to an older version.

## Signing (later)

When Refora Technologies has a code-signing certificate, `Vireo.exe` and `VireoSetup.exe` are signed in the release
workflow, at the place marked there. Signed builds stop SmartScreen's "unknown publisher" warning and most antivirus false
alarms.
