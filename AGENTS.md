# Forge Tools contributor instructions

## Changes and updates

Follow the existing GitHub Actions release system. A source push alone does not make an update available to installed apps.

- Core changes (including the mapping editors in `src/Forge.App`) require a version increment in `src/Forge.App/Forge.App.csproj` and a matching `core-v<version>` release tag.
- Plugin changes require a version increment in that plugin's `plugin.json` and the matching tag from its `.github/workflows/*-release.yml` workflow. Do not bump unaffected plugins merely because their editor lives in Core.
- Use patch increments for bug fixes. Check remote main and existing tags before choosing versions. Never overwrite a published tag or reuse a released version.
- Keep minimumCoreVersion accurate when a plugin begins depending on newer Core features.
- Build the solution in Release with warnings as errors and run `tests/Forge.Regression` before pushing changes. Add relevant regression coverage for behavior fixes.
- When publishing is in scope, push the source commit, verify CI, then push the matching release tags. Verify release workflow completion and assets, not just successful Git pushes.
- Core releases must include Windows and Linux ZIPs plus SHA-256 sidecars. Core's updater discovers published `core-v*` GitHub releases.
- Plugin release workflows publish their ZIP, calculate its actual SHA-256, and commit the corresponding `catalog/catalog.json` entry on main. Do not pre-advertise an unavailable package or invent a checksum. Check the generated catalogue version, URL and hash against the release asset after publishing.
- Fetch the workflow-generated catalogue commit after publication. Report whether updates are actually available or only prepared locally, and clearly identify any authentication or CI blocker.
- Do not commit portable `data` folders, credentials, build outputs, or downloaded packages.

## Verification

```powershell
dotnet restore ForgeTools.slnx --configfile NuGet.Config
dotnet build ForgeTools.slnx -c Release --no-restore --warnaserror
dotnet run --project tests/Forge.Regression/Forge.Regression.csproj -c Release --no-build
```
