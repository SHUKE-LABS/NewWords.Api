# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NewWords.Api is a multilingual vocabulary learning application that helps users build and manage their foreign language vocabulary. The application supports multiple languages and uses AI-powered explanations to help users understand new words in context.

## Architecture

This is a .NET 8 Web API solution with a clean architecture pattern:

### Project Structure
- **NewWords.Api** - Main Web API project containing controllers, entities, services, and DTOs
- **Api.Framework** - Shared framework library providing base classes, helpers, and common functionality
- **LLM** - Language Learning Model service for AI-powered word explanations and language detection

### Key Components
- **Entity Framework**: Uses SqlSugar ORM for database operations with MySQL
- **Authentication**: JWT Bearer token authentication
- **LLM Integration**: Multi-provider AI service supporting OpenRouter and other providers with fallback mechanisms
- **Language Support**: 25+ supported languages for vocabulary learning
- **Repository Pattern**: Generic repository base classes for data access
- **Logging**: NLog with Seq integration for centralized structured logging

### Core Services
- **VocabularyService**: Manages user words, explanations, and vocabulary operations
- **LanguageService**: Handles AI-powered word explanations and language detection using configurable agents
- **AuthService**: JWT authentication and user management
- **ConfigurationService**: Manages LLM agent configurations and provider settings

## Development Commands

### Build and Run
```bash
# Build the entire solution
dotnet build NewWords.Api.sln

# Run the API in development mode
dotnet run --project src/NewWords.Api

# Run with specific profile
dotnet run --project src/NewWords.Api --launch-profile https
```

### Database
- Uses SqlSugar ORM with MySQL
- Connection configuration in `appsettings.json` under `DatabaseConnectionOptions`
- Database migrations are ordered, idempotent SQL scripts in `migration_scripts/` (`NN_<name>.sql`)
- **Applied automatically on deploy** (issue #41): `migration_scripts/apply_migrations.sh` runs on the VPS during `deploy_to_production.yml`, after files are copied and before the service restarts, tracked by a `schema_migrations` ledger so each script runs exactly once. A migration failure fails the deploy before restart. Add a migration by dropping the next-numbered idempotent `NN_*.sql`; see `migration_scripts/README.md` (including the one-time baseline precondition for the first automated deploy).

### Deploying to production (issue #46)
Production deploy is **manually gated**, not push-triggered. A merge to `master` runs the `test` job in `.github/workflows/deploy_to_production.yml` (build + tests) but does **not** touch prod. To ship, run that workflow by hand: GitHub → **Actions** → *Deploy NewWords.Api to production* → **Run workflow** (`workflow_dispatch`). Only that manual run executes `build-and-deploy`, which applies pending DB migrations, restarts the systemd service, and runs the post-restart crash-loop health check. This decouples "code merged" from "users disrupted" so releases land at a deliberate time.

### Development URLs
- HTTP: http://localhost:5116
- HTTPS: https://localhost:7162
- Swagger UI available at `/swagger` endpoint

### Launch Profiles
- **http**: Standard development profile (localhost only)
- **https**: HTTPS development profile
- **Local**: Local development profile (binds to all interfaces: `http://*:5116`)

## Configuration

### Key Configuration Sections
- **DatabaseConnectionOptions**: MySQL connection settings
- **Jwt**: JWT authentication configuration  
- **Agents**: LLM provider configurations (OpenRouter, etc.)
- **SupportedLanguages**: Array of supported language codes and names
- **AllowedCorsOrigins**: CORS configuration for frontend integration
- **AppStore**: Apple App Store Server API credentials for receipt verification (see below)
- **Redis**: Redis-backed dynamic configuration source (see below)

### Redis-Backed Dynamic Configuration
The `Redis` section wires the [ConfigManager.Provider](https://www.nuget.org/packages/ConfigManager.Provider) as an `IConfiguration` source, so LLM agent/model config can be adjusted through [ConfigManager.Web](https://github.com/shukebeta/ConfigManager.Web) without editing `appsettings.json` or redeploying.

- **Settings**: `ConnectionString` (StackExchange.Redis format), `Database` (index, default `0`), `ProjectPrefix` (`newwords.api`).
- **Registered last** in `Program.cs`, so values in Redis take authority over `appsettings.json`, environment variables, and command-line args.
- **Optional**: the source is only added when the connection string and prefix are set and the connection string is not the unsubstituted `PRODUCTION_REDIS_CONNECTION` placeholder. If Redis is unreachable, the app degrades gracefully and boots from `appsettings.json`.
- **Key format**: `newwords.api:<config-path>`. The provider strips the `newwords.api:` prefix and exposes the rest as a flat `IConfiguration` key (no JSON parsing), so arrays use the standard .NET indexed form. Examples:
  - `newwords.api:Agents:0:Provider` → `openrouter`
  - `newwords.api:Agents:0:BaseUrl` → `https://openrouter.ai/api/v1`
  - `newwords.api:Agents:0:ApiKey` → `<key>`
  - `newwords.api:Agents:0:Models:0` → `google/gemma-4-26b-a4b-it`
  - `newwords.api:Explanation:PreferredModels:0` → `google/gemma-4-26b-a4b-it`
- API keys live in Redis (an internal-only service; operator key visibility is accepted). Auth on the ConfigManager.Web edit path is a deployment concern (internal-only bind / reverse-proxy auth), not app code.
- No-restart reload of these values pairs with issue #20; the provider's pub/sub reload is in place, but cached config consumers are refreshed there.

#### Rollout checklist (before flipping `PRODUCTION_REDIS_CONNECTION` to a real value)

Once `PRODUCTION_REDIS_CONNECTION` is substituted to a real connection string, Redis is registered last and becomes authoritative for `Agents:*` and `Explanation:PreferredModels:*` (last-registered-wins). If those keys aren't fully populated in ConfigManager.Web at that moment (e.g. `Provider` set but `ApiKey` missing/empty), the Production fail-fast in `Program.cs` (`AgentApiKeyValidator`, issue #6) throws on every restart and the service crash-loops. Follow this order so the switch is verified locally first:

1. **Populate every required key in ConfigManager.Web** for the `newwords.api` prefix before touching the production secret:
   - `newwords.api:Agents:0:Provider`, `:BaseUrl`, `:ApiKey`, `:Models:0..n`
   - `newwords.api:Explanation:PreferredModels:0..n`
2. **Verify locally against the same Redis instance/prefix.** Point `appsettings.Local.json`'s `Redis` section (`ConnectionString`, `Database`, `ProjectPrefix`) at the same instance, run the API, and confirm:
   - startup logs show no `AgentApiKeyValidator` issues, and
   - a real explanation request succeeds end-to-end.
3. **Only then set/change `PRODUCTION_REDIS_CONNECTION`** in the production secret.
4. **Watch the deploy.** The deploy workflow's post-restart health gate (`.github/workflows/deploy_to_production.yml`) fails the run if the service crash-loops; also watch Seq for a few minutes for late errors.

### Apple App Store Receipt Verification (issue #38)
Clients submit a StoreKit 2 transaction id to `POST /Entitlement/VerifyApple`; the backend fetches the authoritative signed transaction from the **App Store Server API** (via `Mimo.AppStoreServerLibrary`), validates its JWS signature against Apple's certificate chain (`Certificates/AppleRootCA-G3.cer`), and on a valid active subscription upserts the #37 entitlement (`store = "appstore"`, `PremiumExpiresAt`, `OriginalTransactionId`). Restore reuses the same endpoint. Invalid / expired / tampered / revoked transactions grant nothing and return error code `42902` (`AppleVerificationFailedErrorCode`).

- **`AppStore` config section**: `BundleId`, `KeyId`, `IssuerId`, `PrivateKey`, `Environment`.
  - `PrivateKey` is the ES256 `.p8` key as **raw PEM** (convenient for Redis/Local) or **base64-encoded PEM** (single-line, deploy-secret friendly since a multiline PEM does not `sed`-substitute into JSON cleanly).
  - `Environment`: `"Production"`, `"Sandbox"`, or empty. **Empty** = query Production first and fall back to Sandbox on a TransactionIdNotFound (4040010) error — the correct default for mixed live/sandbox transactions. Pin `"Sandbox"` for sandbox-only testing to skip the Production round-trip.
- **Redis override keys**: `newwords.api:AppStore:BundleId`, `:KeyId`, `:IssuerId`, `:PrivateKey`, `:Environment` (re-read per verification call, so a change takes effect without restart).
- **Not validated at boot.** Unlike the LLM keys (`AgentApiKeyValidator`, issue #6), Apple creds are deliberately **not** fail-fast: iOS is not live yet, so a missing/placeholder key must not crash-loop the service. The endpoint returns error `42902` ("not configured") until real creds are set.
- **Sandbox coverage**: the grant/reject logic and the Production→Sandbox fallback are unit-tested via a mocked verifier seam; a real end-to-end sandbox verification requires live Apple credentials and is a manual step (not run in CI).
- **Follow-ups (out of scope here)**: Google Play verification (#24; entitlement store is already store-agnostic) and App Store Server Notifications V2 webhook (for server-side renewal/refund tracking).

### Environment-Specific Settings
- Development settings in `appsettings.Development.json`
- Local development settings in `appsettings.Local.json` (git-ignored)
- Production secrets should be replaced in `appsettings.json` (placeholders: `PRODUCTION_MYSQL_PASSWORD`, `PRODUCTION_SYMMETRIC_SECURITY_KEY`, `PRODUCTION_REDIS_CONNECTION`, `XAI_API_KEY`, and the Apple `AppStore` keys: `APPLE_BUNDLE_ID`, `APPLE_KEY_ID`, `APPLE_ISSUER_ID`, `APPLE_APP_STORE_PRIVATE_KEY`)

## LLM Integration

The application uses a flexible agent-based system for AI services:
- Multiple providers supported with fallback mechanisms
- Configurable model selection per provider
- Language-specific prompts for word explanations
- Supports both free and paid model tiers

## Database Schema

Key entities:
- **Users**: User accounts and profiles
- **UserWords**: User's vocabulary progress tracking
- **WordExplanations**: AI-generated word explanations
- **WordCollections**: Predefined vocabulary sets (CET-4/6, GRE, TOEFL, etc.)
- **QueryHistory**: Track user interaction history
- **UserSettings**: Per-user configuration and preferences

## API Structure

Controllers follow RESTful patterns:
- **AuthController**: Login, registration, token management
- **VocabularyController**: Word management and explanations
- **UserController**: User profile management
- **SettingsController**: User preferences
- **LLMController**: AI service endpoints

All controllers inherit from `BaseController` which provides common functionality and user context access.

## Logging Configuration

The application uses NLog with Seq integration for centralized logging:

### NLog Setup
- **File Logging**: Local log files in `logs/` directory
- **Console Logging**: Development console output
- **Seq Logging**: Centralized structured logging to Seq server

### Structured Logging Properties
- **Environment**: Automatically detects from `ASPNETCORE_ENVIRONMENT`
- **Host**: Machine hostname for environment identification
- **Application**: Set to "NewWords.Api"
- **Version**: Application version from configuration

### Log Filtering
ASP.NET Core framework verbose logs are filtered to reduce noise:
- Execution logs (`Microsoft.AspNetCore.Mvc.Infrastructure.*`)
- Routing logs (`Microsoft.AspNetCore.Routing.*`)
- Authorization logs (`Microsoft.AspNetCore.Authorization.*`)
- Hosting logs (`Microsoft.AspNetCore.Hosting.*`)

Only Warning level and above are logged for these categories, while application logs remain at Information level.

### Running with Logging
```bash
# Run with Local profile (recommended for development)
dotnet run --launch-profile Local

# Run with Development profile
dotnet run --launch-profile http
```

## Build & Test Commands
- Build solution: `dotnet build`
- Run application: `dotnet run --project src/NewWords.Api`
- Run with Local environment: `dotnet run --project src/NewWords.Api --launch-profile Local`