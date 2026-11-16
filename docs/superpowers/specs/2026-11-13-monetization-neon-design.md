# WinSweep Monetization, Auth & Neon Backend Design

**Date:** 2026-11-13  
**Status:** Draft for User Review  
**Target Endpoints:** Microsoft Store (`MSIX`) & Personal Desktop (`Unpackaged`)

---

## 1. Overview & Goals

WinSweep will use a **freemium session-credit + lifetime pass** monetization model backed by **Neon Postgres** (via a Vercel Serverless API) and **Microsoft Store Native In-App Purchases (`Windows.Services.Store.StoreContext`)** for payouts to Turkey via Microsoft Partner Center.

### Core Pricing & Metering Rules
1. **5 Free Cleanup Sessions (24h each):** Every new user/device starts with **5 free cleanup credits**.
2. **1 Credit = 1 Cleanup Session (24 Hours):** Consuming 1 credit unlocks all destructive and maintenance actions across WinSweep for **24 hours**, allowing users to clean Temp files, purge Project Artifacts, remove App Leftovers, and run Maintenance tasks in one sitting without losing multiple credits.
3. **Always Free (0 Credits):**
   - System Status live monitoring
   - Disk Analyzer directory scanning
   - Clean sweep candidate scanning & Dry-Run previews
   - Project Artifacts scanning & Installed Apps scanning
   - Quarantine Vault restore/rollback (users are never paywalled from recovering quarantined files)
4. **Paid Upgrade Tiers (Microsoft Store Add-ons):**
   - **10 Cleanup Runs Pack (\$1.99 USD):** Developer-managed consumable add-on that adds `+10` session credits to the user's Neon account and local state.
   - **Unlimited Lifetime Pass (\$12.99 USD):** Durable add-on that permanently unlocks `IsLifetime = true` across devices.
5. **Personal Desktop Endpoint Bypass:**
   - The personal unpackaged desktop build (`!AppEnvironment.IsStoreBuild`, published to `artifacts\desktop\`) automatically grants **Owner Unlimited** status by default, while also supporting an **Owner Master License Key** in Neon for testing or unlocking Store builds.

---

## 2. System Architecture

```
+-----------------------------------------------------------------------+
|                       WinSweep WinUI 3 Desktop App                    |
|                                                                       |
|  +-----------------------+     +-----------------------------------+  |
|  | ViewModels            | --> | LicensingGate / LicensingService  |  |
|  | - CleanViewModel      |     | - Checks Personal Build Bypass    |  |
|  | - ProjectArtifactsVM  |     | - Checks Active 24h Session       |  |
|  | - AppsViewModel       |     | - Checks Lifetime / Credits       |  |
|  | - MaintenanceVM       |     | - DPAPI-signed local cache        |  |
|  +-----------------------+     +-----------------+-----------------+  |
|                                                  |                    |
|          +---------------------------------------+---------+          |
|          |                                                 |          |
|          v                                                 v          |
|  +-------------------------------+     +---------------------------+  |
|  | StoreCommerceService          |     | NeonApiClient (HTTPS)     |  |
|  | (Windows.Services.Store)      |     | - /api/auth/register      |  |
|  | - RequestPurchaseAsync        |     | - /api/auth/login         |  |
|  | - GetAppLicenseAsync          |     | - /api/license/status     |  |
|  | - ReportConsumableFulfillment |     | - /api/license/consume    |  |
|  +-------------------------------+     | - /api/license/purchase   |  |
|                                        | - /api/license/redeem     |  |
|                                        +-------------+-------------+  |
+------------------------------------------------------|----------------+
                                                       | HTTPS (JSON)
                                                       v
+-----------------------------------------------------------------------+
|                  Vercel Serverless API (site/api/*)                   |
|                  Uses @neondatabase/serverless + JWT                  |
+------------------------------------------------------|----------------+
                                                       | TLS Postgres
                                                       v
+-----------------------------------------------------------------------+
|                   Neon Serverless PostgreSQL 18                       |
|      Tables: users, devices, transactions, license_keys, usage_logs   |
+-----------------------------------------------------------------------+
```

---

## 3. Database Schema (Neon Postgres)

We will provision a dedicated Neon project (`winsweep-prod`) in organization `org-super-block-66059891` (`aws-eu-central-1`) with the following schema:

```sql
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- 1. Registered user accounts
CREATE TABLE IF NOT EXISTS users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email TEXT UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,
    display_name TEXT,
    credits_remaining INT NOT NULL DEFAULT 5 CHECK (credits_remaining >= 0),
    is_lifetime BOOLEAN NOT NULL DEFAULT FALSE,
    active_session_expires_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 2. Device tracking (supports anonymous 5 free runs before sign-up & device linking)
CREATE TABLE IF NOT EXISTS devices (
    device_id TEXT PRIMARY KEY,
    user_id UUID REFERENCES users(id) ON DELETE SET NULL,
    credits_remaining INT NOT NULL DEFAULT 5 CHECK (credits_remaining >= 0),
    is_lifetime BOOLEAN NOT NULL DEFAULT FALSE,
    active_session_expires_at TIMESTAMPTZ,
    app_version TEXT,
    is_store_build BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 3. Store & License Key Transactions (idempotent via unique tracking_id)
CREATE TABLE IF NOT EXISTS transactions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID REFERENCES users(id) ON DELETE SET NULL,
    device_id TEXT NOT NULL,
    product_code TEXT NOT NULL, -- 'winsweep_10_runs', 'winsweep_lifetime', 'license_key'
    tracking_id TEXT UNIQUE NOT NULL, -- Store fulfillment tracking ID or redemption ID
    credits_added INT NOT NULL DEFAULT 0,
    granted_lifetime BOOLEAN NOT NULL DEFAULT FALSE,
    amount_usd NUMERIC(6,2) NOT NULL DEFAULT 0.00,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 4. Redeemable Promo / Owner License Keys
CREATE TABLE IF NOT EXISTS license_keys (
    key_code TEXT PRIMARY KEY,
    tier TEXT NOT NULL CHECK (tier IN ('lifetime', 'credits_10')),
    credits_amount INT NOT NULL DEFAULT 0,
    max_redemptions INT NOT NULL DEFAULT 1,
    redemptions_count INT NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 5. Session consumption & audit logs
CREATE TABLE IF NOT EXISTS usage_logs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID REFERENCES users(id) ON DELETE SET NULL,
    device_id TEXT NOT NULL,
    action_type TEXT NOT NULL,
    credits_after INT NOT NULL,
    session_expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

---

## 4. Serverless API Endpoints (`site/api/`)

Hosted alongside the static landing page in `site/`, allowing zero-cost Vercel deployment:

1. **`POST /api/auth/register`**
   - Body: `{ email, password, displayName, deviceId }`
   - Creates `users` row, links `devices.user_id`, reconciles remaining free credits (`LEAST(5, device.credits_remaining)` so registering doesn't reset used free runs to 5 if already spent, or grants 5 on a fresh device), and returns `{ token, licenseState }`.
2. **`POST /api/auth/login`**
   - Body: `{ email, password, deviceId }`
   - Verifies password hash (PBKDF2/scrypt via Node `crypto`), links `deviceId` to `user_id`, returns `{ token, licenseState }`.
3. **`GET /api/license/status?deviceId=...`**
   - Headers: Optional `Authorization: Bearer <token>`
   - Returns current `{ creditsRemaining, isLifetime, activeSessionExpiresAt, email, isAuthenticated }`.
4. **`POST /api/license/consume`**
   - Body: `{ deviceId, actionType }` (+ optional `Authorization: Bearer <token>`)
   - If `is_lifetime` is true or `active_session_expires_at > NOW()`, returns `{ allowed: true, sessionStarted: false, licenseState }`.
   - Else if `credits_remaining > 0`, atomically decrements `credits_remaining` by 1, sets `active_session_expires_at = NOW() + INTERVAL '24 hours'`, inserts a `usage_logs` row, and returns `{ allowed: true, sessionStarted: true, licenseState }`.
   - Else returns `402 Payment Required` with `{ allowed: false, licenseState }`.
5. **`POST /api/license/purchase`**
   - Body: `{ deviceId, productCode, storeId, trackingId }` (+ optional `Authorization: Bearer <token>`)
   - Idempotently records Microsoft Store add-on purchase in `transactions` (`ON CONFLICT (tracking_id) DO NOTHING`), increments `credits_remaining + 10` for `winsweep_10_runs` or sets `is_lifetime = TRUE` for `winsweep_lifetime`, and returns updated `licenseState`.
6. **`POST /api/license/redeem`**
   - Body: `{ deviceId, keyCode }` (+ optional `Authorization: Bearer <token>`)
   - Validates `license_keys`, increments `redemptions_count`, grants `+10` credits or `is_lifetime = TRUE`, and returns updated `licenseState`.

---

## 5. Client-Side Components (`WinSweep.Core` & `WinSweep.App`)

### 5.1 `WinSweep.Core.Licensing`
- **`LicenseState` (Model):**
  - `int CreditsRemaining` (default `5`)
  - `bool IsLifetime`
  - `bool IsPersonalOwnerBuild` (`!AppEnvironment.IsStoreBuild`)
  - `DateTimeOffset? ActiveSessionExpiresAt`
  - `string? UserEmail`
  - `string? AuthToken`
  - Computed properties: `bool HasActiveSession`, `bool CanExecuteSweep`, `string StatusBadgeText` (e.g., `"Owner Edition (Unlimited)"`, `"Lifetime Pro"`, `"Active Session (22h left)"`, `"4 Free Runs Left"`).
- **`DeviceIdentity`:**
  - Reads Windows `MachineGuid` from `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography` and hashes it with SHA-256 into a stable, privacy-safe `device_id`.
- **`LicenseCacheStore`:**
  - Persists `LicenseState` to `%LOCALAPPDATA%\WinSweep\license.dat` encrypted/signed with Windows DPAPI (`ProtectedData` / HMAC tied to `DeviceIdentity`) so users cannot trivially edit a plain JSON number, while ensuring the app works smoothly even if the user cleans their PC while offline.
- **`LicensingService`:**
  - Coordinates `NeonApiClient` and `LicenseCacheStore`.
  - Exposes `TryConsumeCleanupRunAsync(string actionType)`:
    - Returns `true` immediately for Personal Desktop builds (`!AppEnvironment.IsStoreBuild`), Lifetime users, or when an active 24h session is already running.
    - Otherwise consumes 1 credit via Neon API (with offline fallback to local DPAPI cache + background sync) and starts a 24h session.
    - Returns `false` when 0 credits remain, prompting the UI to open `AccountPaywallDialog`.

### 5.2 `WinSweep.App` Store Commerce & UI
- **`StoreCommerceService`:**
  - Wraps `Windows.Services.Store.StoreContext`.
  - Attaches `App.MainWindowHandle` via `WinRT.Interop.InitializeWithWindow.Initialize`.
  - Product Store IDs configured in `MonetizationConfig`:
    - `TenRunsStoreId` (Partner Center Add-on ID for **10 Cleanup Runs — \$1.99**)
    - `LifetimeStoreId` (Partner Center Add-on ID for **Lifetime Unlimited — \$12.99**)
  - Checks `GetAppLicenseAsync()` on startup to automatically restore Durable Lifetime purchases after Windows reinstalls.
- **UI Integration:**
  - **Sidebar Footer / Status Pill (`MainWindow.xaml`):** Displays current plan/credits badge (e.g., `5 Free Runs Left` or `Active Session • 23h left` or `Lifetime Pro`) + quick button to open the **Account & Upgrade** dialog.
  - **Account & Upgrade Dialog (`Views/AccountPaywallDialog.xaml`):**
    - Tab 1 / Section 1: **Upgrade / Top-Up Credits**
      - Card 1: **10 Cleanup Sessions — \$1.99** (*"24 hours of unlimited cleaning per session"*)
      - Card 2: **Lifetime Unlimited — \$12.99** (*"Best Value • Never pay again"*)
    - Tab 2 / Section 2: **Sign Up / Sign In (Sync Across Devices)**
      - Email + Password registration/login backed by Neon Postgres so credits are tied to their email.
    - Tab 3 / Section 3: **Redeem License Key**
      - Allows entering a promo/owner license key.
  - **Gated Destructive Actions:**
    - `CleanViewModel.ExecuteCleanCommand` (only when `DryRun == false`)
    - `ProjectArtifactsViewModel.PurgeSelectedCommand`
    - `ProjectArtifactsViewModel.PruneDockerCommand`
    - `AppsViewModel.RemoveSelectedLeftoversCommand`
    - `MaintenanceViewModel.RunSelectedCommand`

---

## 6. Verification & Testing Plan

1. **Neon Database Verification:**
   - Provision Neon database via `mcp-server-neon`, run schema migration, seed an Owner Master License Key (`WINSWEEP-OWNER-LIFETIME`), and verify tables via SQL.
2. **Unit Tests (`WinSweep.Core.Tests`):**
   - Test `LicensingService` 5-free-runs initialization.
   - Test 24-hour session window reuse (multiple sweeps within 24 hours only consume 1 credit).
   - Test expiry after 24 hours consuming the next credit.
   - Test paywall block when `CreditsRemaining == 0` and session expired.
   - Test Personal Build bypass (`!IsStoreBuild` never decrements credits or blocks).
   - Test License Key redemption and Store purchase credit fulfillment.
3. **Dual Endpoint Builds:**
   - Rebuild both `artifacts\desktop\WinSweep.App.exe` and `artifacts\msix\WinSweep.App_0.1.0.0_x64.msix`, plus reinstall the signed MSIX locally.
