# Code signing — free, via SignPath Foundation

**Goal:** sign `RaceReadyCheckBridge.exe` so Windows SmartScreen stops warning users about an
"unknown publisher." [SignPath Foundation](https://signpath.org) gives qualifying open-source
projects a free code-signing certificate (private key held on their HSM) and plugs straight into
this repo's GitHub Actions release workflow. It's not *instant* trust, but reputation now builds
against **your publisher identity**, so it carries across every future release instead of resetting
each time (which is what happens with an unsigned exe — every build has a new hash).

---

## Prerequisites
- Public GitHub repo — ✅ `Angelwraith/racereadycheck-bridge`
- A recognized open-source license file (`LICENSE`) in the repo root — **REQUIRED, not yet present.**
  Add one (e.g. MIT) before applying, or SignPath will reject the application.

## Step 1 — Apply (do this now)
1. Go to <https://signpath.io/solutions/open-source-community>.
2. Submit an application for the project:
   - **Repository:** `https://github.com/Angelwraith/racereadycheck-bridge`
   - **License:** whichever you added (MIT recommended)
   - **Description:** "Optional Windows companion for RaceReadyCheck — global hotkeys + Forza
     telemetry relay. Self-contained .NET 8 single-file exe, no installer."
3. Wait for approval (manual review, typically a few days).

## Step 2 — After approval: configure SignPath
SignPath creates an organization for you and connects this repo (install the **SignPath GitHub App**
when prompted). In the SignPath dashboard, collect/create:
1. **Organization ID** (Settings).
2. **Project** — slug e.g. `racereadycheck-bridge`.
3. **Artifact configuration** that signs `RaceReadyCheckBridge.exe` — note its slug (e.g. `exe`).
4. **Signing policy** for releases — slug e.g. `release-signing`.
5. **API token** for the CI user.

## Step 3 — Add the GitHub secret
Repo → Settings → Secrets and variables → Actions → **New repository secret**:
- Name: `SIGNPATH_API_TOKEN`
- Value: the API token from Step 2

## Step 4 — Wire signing into `.github/workflows/release.yml`
Insert these three steps **between** the existing `Publish exe` and `Package zip` steps. Replace the
`<PLACEHOLDER>` values with your SignPath slugs. The `Package zip` step is unchanged — because it
copies the exe out of the publish folder, overwriting that exe with the signed copy means the
released zip automatically contains the signed exe.

```yaml
      # --- insert AFTER "Publish exe", BEFORE "Package zip" ---

      - name: Upload unsigned exe for SignPath
        id: unsigned
        uses: actions/upload-artifact@v4
        with:
          name: unsigned
          path: bin/Release/net8.0-windows/win-x64/publish/RaceReadyCheckBridge.exe

      - name: Sign the exe (SignPath)
        uses: signpath/github-action-submit-signing-request@v2
        with:
          api-token: '${{ secrets.SIGNPATH_API_TOKEN }}'
          organization-id: '<YOUR_SIGNPATH_ORG_ID>'
          project-slug: 'racereadycheck-bridge'
          signing-policy-slug: 'release-signing'
          artifact-configuration-slug: 'exe'
          github-artifact-id: '${{ steps.unsigned.outputs.artifact-id }}'
          wait-for-completion: true
          output-artifact-directory: 'signed'

      - name: Swap in the signed exe
        shell: pwsh
        run: Copy-Item "signed/RaceReadyCheckBridge.exe" "bin/Release/net8.0-windows/win-x64/publish/RaceReadyCheckBridge.exe" -Force
```

Notes:
- **Verify versions/inputs against SignPath's current demo** before committing —
  <https://github.com/SignPath/github-actions-demo> — SignPath occasionally bumps the action's
  major version or renames inputs, and its setup wizard shows the exact snippet for your project.
- The private key never touches CI; the workflow only handles the API token (a secret) and the
  signed output that SignPath returns.

## Step 5 — Verify a release
After the next `release.bat` run, download the release zip and: right-click
`RaceReadyCheckBridge.exe` → **Properties → Digital Signatures** — you should see your certificate.
SmartScreen prompts fade as signed builds accumulate downloads under your publisher identity.

## After it's working
Update the "If Windows warns you" card on the download page (`racereadycheck.com/bridge/index.html`)
to reflect that the app is signed (fewer/no warnings) instead of the current "Run anyway" wording.
