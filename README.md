# CupkekGames Asset Vaults — Google Drive

Google Drive storage for [`com.cupkekgames.assetvaults`](../com.cupkekgames.assetvaults), over rclone.

Install it alongside the vault and a **Google Drive** row appears in **Tools > CupkekGames > Asset Vault**. Nothing else changes: this package implements `IVaultBackend` and registers itself, and the vault window never learns that Drive exists.

## Setup, once

1. **Google Cloud Console** → new project → enable the Drive API → Credentials → OAuth client ID → application type **Desktop app**. This is the only manual step; Google has no API to create a client.
2. Create the settings asset (the vault window offers a button), paste the client id and secret.
3. Press **Download rclone**. A single self-contained binary, no admin rights, no installer, no PATH edits.
4. Press **Authorize with Google**. The browser opens, you approve, the token comes back.

If Google answers `access_denied`, add your account under **OAuth consent screen → Test users**.

If no browser tab appears, or the `127.0.0.1:53682` page stays blank: the link is printed to the console the moment rclone starts listening, and the inspector shows **Open the sign-in page again** while it waits. Paste the link into another browser if the default one will not render it. The listener lives for 10 minutes per attempt; a link from an earlier attempt is dead, so press Authorize again rather than reloading an old tab.

**Open in Drive** (inspector, and the vault window's connection row) opens the vault folder in the Drive web UI. The folder is created by the first upload, so before that there is nothing to open.

## Where the secrets go

- **Client id and secret** live in the settings asset and are committed. RFC 8252 treats a desktop-app client secret as non-confidential, because it cannot be kept secret inside a distributed application, and Google issues them for exactly this flow. Rotate the client before making a repository public.
- **The refresh token does not.** That one grants access to your Drive, so it is written to `UserSettings/AssetVault.GoogleDrive.json`, which Unity's standard `.gitignore` already excludes. This is the single place Unity Localization is a bad model to copy: it keeps credentials in the asset and people commit them.

## The 7-day token, and scope

While the OAuth consent screen is in **Testing** status, Google expires every refresh token after 7 days, whatever scope it carries (only name/email/profile-only apps are exempt). Transfers then fail with `invalid_grant`, which the backend reports as "Google sign-in has expired"; press **Re-authorize** and you are back. To stop it recurring, set **OAuth consent screen → Publishing status → In production**. That is the only fix; changing scope does not remove the expiry.

Scope decides what publishing costs you:

| Scope | You get | You pay |
|---|---|---|
| `drive` (default) | Full access, so you can manage the vault folder by hand in the Drive web UI | It is a **restricted** scope: an unverified production app shows Google's "unverified app" warning on sign-in, which you click through under Advanced |
| `drive.file` | Only files this app created; not restricted, so production needs no verification and shows no warning | rclone cannot see anything you upload manually |

## Why rclone

It already solves OAuth refresh, resumable chunked upload, retry with backoff and checksums. Reimplementing those is weeks of work and would put an OAuth secret inside a compiled assembly.

**No `rclone.conf`, ever.** rclone reads a remote's settings from `RCLONE_CONFIG_<NAME>_<KEY>` environment variables, so the whole remote is defined per invocation: no interactive `rclone config` session to drive, and no shared config file to keep in sync between machines. The environment also keeps the refresh token off a command line that any process listing can read.

The binary is downloaded on demand into `Library/AssetVault/rclone/`, pinned to a version in the settings asset so two machines cannot silently disagree, and verified against the release's own published `SHA256SUMS` before extraction. `Library/` because every Unity `.gitignore` in existence excludes it — a package should not oblige its consumer to add an ignore line for a 20 MB binary. A system-wide rclone already on `PATH` wins over the managed copy.

## Platforms

**Only Windows has actually been exercised.** The archive names and the execute-bit step for macOS and Linux are the documented ones and should work, but treat a first run there as unproven. Where no mapping exists the provisioner says so and points you at installing rclone yourself, rather than downloading the wrong build.

## Dependencies

- `com.cupkekgames.assetvaults`

## Installation

Install via the CupkekGames UPM scoped registry (`https://www.docs.cupkek.games/upm`), or as a local `file:` path during development.
