# Releasing Horizon Converter

Checklist for cutting a public build. Everything below assumes you are in the repository root.

## 0. One-time setup

### Repository

Create an empty **public** repo on GitHub (no README, no .gitignore — this repo already has both),
then:

```bash
git remote add origin https://github.com/<user>/horizon-converter.git
git branch -M main
git push -u origin main
```

The working tree is already clean of build output: `dist/`, `bin/`, `obj/`, `keys/`, `*.keystore`,
`*.pfx` and `tests/vectors` are all in `.gitignore`. Never commit them — in particular never commit
`keys/` (it holds the APK signing key) and never commit game assets (`.ttx`, `.tmb`, `.tsb`, `.ttc`).

### Signing keys

| Thing | Where | Notes |
| --- | --- | --- |
| APK release key | `keys/horizon-release.keystore` (alias `horizon`) | Password in `keys/RELEASE-KEYSTORE.txt`. **Back both up.** Android will refuse to install an update signed with a different key. |
| Desktop exe | `tools/sign-app.ps1` + the local code-signing certificate | Self-signed, so Windows SmartScreen still warns. See §4. |

`keys/signing.local.props` is picked up automatically by `src/AeroflyConv.Android/AeroflyConv.Android.csproj`,
so `dotnet build` produces an APK signed with the release key (verify: `apksigner verify --print-certs`,
DN should read `CN=Horizon Converter`).

## 1. Bump the version

Three places, all must agree:

1. `src/AeroflyConv.Core/ProductInfo.cs` → `Version = "1.1";`
2. `src/AeroflyConv.Cli/AeroflyConv.Cli.csproj` and `src/AeroflyConv.Gui/AeroflyConv.Gui.csproj`
   → `Version`, `FileVersion`, `AssemblyVersion`
3. `src/AeroflyConv.Android/AeroflyConv.Android.csproj`
   → `ApplicationVersion` (must increase by 1 every release), `ApplicationDisplayVersion`, `Version`

Add a `### 1.1` entry to the changelog in `README.md`.

## 2. Build the artifacts

The whole of §2 and §3 is wrapped in one script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\release.ps1
```

It reads the version from `ProductInfo.cs`, builds and signs the desktop executables, builds the APK
(with the release keystore when `keys/signing.local.props` exists), and writes `dist/v<version>/`
with a `SHA256SUMS.txt`. Use `-SkipAndroid`, `-NoSign` or `-Version 1.1` for partial runs.

Doing it by hand:

```powershell
# desktop (single file, compressed, self-contained)
dotnet publish src\AeroflyConv.Cli\AeroflyConv.Cli.csproj -c Release -r win-x64 `
  --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:EnableCompressionInSingleFile=true /p:DebugType=none -o dist\v1.1\cli
dotnet publish src\AeroflyConv.Gui\AeroflyConv.Gui.csproj -c Release -r win-x64 `
  --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:EnableCompressionInSingleFile=true /p:DebugType=none -o dist\v1.1\gui

# sign both (one path per call)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-app.ps1 -Paths dist\v1.1\cli\HorizonConverter.exe
powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-app.ps1 -Paths dist\v1.1\gui\HorizonConverter.exe

# android (Release configuration is required—Debug uses Fast Deployment and won't run standalone)
dotnet build src\AeroflyConv.Android\AeroflyConv.Android.csproj -c Release
copy src\AeroflyConv.Android\bin\Release\net10.0-android\com.horizonconverter.app-Signed.apk `
     dist\v1.1\android\HorizonConverter-1.1.apk
```

Smoke test before shipping:

```powershell
dist\v1.1\cli\HorizonConverter.exe --version
dist\v1.1\gui\HorizonConverter.exe        # convert one .ttx and one .tmb
adb install -r dist\v1.1\android\HorizonConverter-1.1.apk
```

## 3. Checksums and release notes

```powershell
$dir = "dist\v1.1"
$lines = Get-ChildItem $dir -Recurse -File -Include HorizonConverter*.exe,HorizonConverter*.apk |
  ForEach-Object {
    $rel = $_.FullName.Substring((Resolve-Path $dir).Path.Length + 1).Replace('\', '/')
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $rel"
  }
$lines | Out-File "$dir\SHA256SUMS.txt" -Encoding utf8
```

Write `dist/v1.1/RELEASE-NOTES-1.1.md` (what changed, file table with sizes, known limits) and copy
`RELEASE-NOTES` + `SHA256SUMS.txt` into the release.

## 4. Publish on GitHub

Web UI: **Releases → Draft a new release**

* Tag: `v1.1` (create on `main`), title `Horizon Converter 1.1`
* Description: paste the body of `RELEASE-NOTES-1.1.md`
* Attach these four files:
  * `HorizonConverter.exe` (GUI) — rename to `HorizonConverter-1.1-win-gui.exe`
  * `HorizonConverter.exe` (CLI) — rename to `HorizonConverter-1.1-win-cli.exe`
  * `HorizonConverter-1.1.apk`
  * `SHA256SUMS.txt`
* Tick **Set as the latest release**, publish.

With the `gh` CLI instead:

```bash
git tag v1.1 && git push origin v1.1
gh release create v1.1 \
  dist/v1.1/gui/HorizonConverter.exe#HorizonConverter-1.1-win-gui.exe \
  dist/v1.1/cli/HorizonConverter.exe#HorizonConverter-1.1-win-cli.exe \
  dist/v1.1/android/HorizonConverter-1.1.apk \
  dist/v1.1/SHA256SUMS.txt \
  --title "Horizon Converter 1.1" --notes-file dist/v1.1/RELEASE-NOTES-1.1.md
```

### Expect two warnings, and say so in the release notes

* **Windows SmartScreen**: the exe is signed with a self-signed certificate, so users see
  "Windows protected your PC" → *More info* → *Run anyway*. Only a certificate that SmartScreen
  already trusts removes this — see §8 for the two affordable ways to get one.
* **Android Play Protect**: sideloaded APKs trigger "unknown app" prompts; the APK is not from Play,
  so users must allow installs from the source they downloaded it from.

Include the checksums in the release text so people can verify the download before running it.

## 5. Other places you can publish

* **itch.io** — good for small tools; supports exe + APK in one page.
* **ModDB / Nexus Mods** — where the flight-sim audience looks for Aerofly content.
* The **Aerofly forums / Discord** — the most direct audience; link the GitHub release.
* If you ever want **Google Play**: you need an `.aab` (`dotnet publish -c Release -f net10.0-android`
  with `AndroidPackageFormat=aab`), Play App Signing, a privacy policy and a data-safety form.
  Play requires a fresh upload key anyway, so the current keystore stays for sideload builds.

## 6. Licensing hygiene

* Ship `LICENSE`, `THIRD-PARTY-NOTICES.md` and `docs/licenses/` with every release (the exe/APK
  bundles NLayer/NVorbis; LZHAM is public domain; the ASTC tables follow ARM's Apache-2.0 code).
* Never attach game `.ttx` / `.tmb` / `.tsb` / `.ttc` samples, and never publish `.afsenc` tooling.
* If someone reports a licence problem with a dependency, the fix is a code change, not a silent
  removal — record it in `THIRD-PARTY-NOTICES.md`.

## 7. Releasing from CI

Two workflows are included:

| Workflow | Trigger | Does |
| --- | --- | --- |
| `.github/workflows/build.yml` | push / PR | builds desktop + APK, uploads unsigned artifacts (sanity check only) |
| `.github/workflows/release.yml` | tag `v*` | publishes CLI/GUI, builds the signed APK, writes `SHA256SUMS.txt`, creates a **draft** release |

To make CI sign the APK, add four repository secrets (Settings → Secrets and variables → Actions):

```
KEYSTORE_BASE64     base64 of keys/horizon-release.keystore
KEYSTORE_PASSWORD   the store password
KEY_ALIAS           horizon
KEY_PASSWORD        the key password
```

Print the base64 on Windows with:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("keys\horizon-release.keystore")) | Set-Clipboard
```

Then `git tag v1.0 && git push origin v1.0` produces a draft release with the four assets attached.
Review it, edit the notes, publish.

## 8. Getting rid of the SmartScreen warning

A self-signed certificate cannot remove it. The realistic options, cheapest first:

| Option | Cost | Effect | Requirements |
| --- | --- | --- | --- |
| **SignPath Foundation** | free for OSS | EV-level signature, warning gone immediately | public repo, OSI-approved licence, a CI that builds the artifact; apply at signpath.org, review takes weeks |
| **Azure Trusted Signing** | ~10 USD/month | warning gone | Microsoft account with 3+ years of verifiable identity/business history; cloud signing, no USB token |
| **OV certificate** | ~100–200 USD/year | warning fades as reputation builds (weeks–months) | registered business, hardware token |
| **EV certificate** | ~300–500 USD/year | warning gone immediately | registered business, hardware token |

`signpath/artifact-configuration.xml` is a starting point for the SignPath application; when the
project is accepted, uncomment the "Sign executables" step in `release.yml` and add
`SIGNPATH_API_TOKEN` / `SIGNPATH_ORGANIZATION_ID` as secrets.

Note that SmartScreen reputation is per file hash: re-signing an existing file resets it, unsigned
files can still become "trusted" after enough clean downloads, and downloading via tools that do not
set the Mark-of-the-Web skips the check entirely (that is why some GitHub projects "never warn").
