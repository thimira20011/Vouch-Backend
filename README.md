# Vouch Backend — High-Trust University Connection Platform

> **SRS Version**: v2.0.0  
> **Tech Stack**: ASP.NET Core 10, C# 13/14, Entity Framework Core 10, PostgreSQL, SignalR, Docker (Alpine)  
> **Target Deployment**: Sri Lankan University Campuses (`.ac.lk`)  
> **Architect**: Thimira Niranjaya  

---

## 1. Overview & Core Philosophy

**Vouch** is a university connection platform designed for high trust, intentional communication, and deep compatibility. It moves away from superficial swiping apps by introducing:

- **Monastic Minimalism**: Quiet, restrained aesthetic, depth built through light and typography contrast.
- **Slow Tech**: One considered match per 24-hour cycle, letter-style text conversations, pause feature.
- **Social Proof via Vouching**: Character Cards built from peer endorsements rather than self-aggrandizing bios.
- **Green Coding & Efficiency**: Built on ASP.NET Core 10 with Alpine-based lightweight containerization for low Software Carbon Intensity (SCI).

---

## 2. Solution & Project Architecture

The backend follows **Clean Architecture** principles and domain-driven design:

```
Vouch-Backend/
├── Vouch.slnx                         # .NET 10 XML Solution
├── Dockerfile                         # Multi-stage Alpine container (Green Coding)
├── docker-compose.yml                 # Local PostgreSQL 17 & API stack
├── .github/workflows/ci.yml           # CI workflow (Build, Test, Container verification)
│
├── src/
│   ├── Vouch.Domain/                  # Enterprise Domain Rules & Entities
│   │   ├── Entities/
│   │   │   ├── BaseEntity.cs          # UUID & Timestamps
│   │   │   ├── Campus.cs              # Multi-tenant campus instance & soft launch gate
│   │   │   ├── User.cs                # Seekers, Vouchers, Ambassadors, Architect
│   │   │   ├── VouchRecord.cs         # Peer endorsements & character traits
│   │   │   ├── DailyMatch.cs          # 24h cycle matching records
│   │   │   ├── DailyReflection.cs     # "No Match Today" fallback philosophical quotes
│   │   │   ├── Conversation.cs        # Letter conversations & adaptive reveal stage
│   │   │   ├── Message.cs             # Considered text letters
│   │   │   ├── Report.cs              # Moderation reports & severity ratings
│   │   │   ├── Block.cs               # User blocking relationship
│   │   │   └── IcebreakerPrompt.cs    # 50 curated static prompts
│   │   ├── Enums/
│   │   │   └── DomainEnums.cs         # Roles, Traits, Interests, Stages, Statuses
│   │   └── Services/
│   │       ├── TrustScoreCalculator.cs       # Flat additive scoring, clique dampening
│   │       ├── SlowBurnCalculator.cs         # 25% @ 40%, 60% @ 70%, 100% @ 100%
│   │       ├── MatchmakingScorer.cs          # Values overlap, Interests, Faculty diversity
│   │       └── LaunchReadinessCalculator.cs  # 30 Ambassadors bootstrap gate
│   │
│   ├── Vouch.Application/             # Application Use Cases & Contracts
│   │   ├── Common/Interfaces/
│   │   │   ├── IApplicationDbContext.cs
│   │   │   ├── IJwtTokenService.cs
│   │   │   ├── IPasswordHasher.cs
│   │   │   ├── IAiWingmanService.cs
│   │   │   ├── ISlowBurnNotificationService.cs
│   │   │   └── IEncryptionService.cs
│   │   └── Features/
│   │       ├── Auth/                  # Registration, Login, Onboarding, Deletion
│   │       ├── Vouching/              # Peer Vouching & Trust score queries
│   │       ├── Matching/              # Daily Connection & Reflections
│   │       ├── Messaging/             # Letter delivery, Pause/Resume, Archive
│   │       ├── Moderation/            # Reports, Blocking, Architect Dashboard
│   │       └── AiWingman/             # Claude Haiku & 50 curated icebreakers
│   │
│   ├── Vouch.Infrastructure/          # Data Access & External Integrations
│   │   ├── Persistence/
│   │   │   ├── ApplicationDbContext.cs
│   │   │   └── DbInitializer.cs       # Pre-seeded Sri Lankan universities & reflections
│   │   ├── Security/
│   │   │   ├── AesEncryptionService.cs# AES-256 PII encryption at rest (NFR-4)
│   │   │   ├── BCryptPasswordHasher.cs# Password hashing
│   │   │   └── JwtTokenService.cs     # JWT auth with claims
│   │   ├── Services/
│   │   │   ├── AuthService.cs
│   │   │   ├── TrustService.cs
│   │   │   ├── MatchService.cs
│   │   │   ├── MessagingService.cs
│   │   │   ├── ModerationService.cs
│   │   │   ├── AnthropicAiWingmanService.cs # 3s latency budget + static fallback
│   │   │   └── SlowBurnNotificationService.cs
│   │   └── SignalR/
│   │       └── VouchHub.cs            # Real-time WebSocket letter delivery
│   │
│   └── Vouch.Api/                     # Presentation & Web API
│       ├── Endpoints/
│       │   ├── AuthEndpoints.cs
│       │   ├── VouchEndpoints.cs
│       │   ├── MatchEndpoints.cs
│       │   ├── MessagingEndpoints.cs
│       │   ├── WingmanEndpoints.cs
│       │   └── ModerationEndpoints.cs
│       ├── Program.cs
│       └── appsettings.json
│
└── tests/
    └── Vouch.UnitTests/               # Unit Test Suite
        └── DomainServices/
            ├── TrustScoreCalculatorTests.cs
            ├── SlowBurnCalculatorTests.cs
            └── LaunchReadinessCalculatorTests.cs
```

---

## 3. SRS v2.0.0 Business Rules Implementation Matrix

This table identifies implementation locations; it does not certify requirement completion. See the [review](docs/project-review-and-completion-plan.md) and [fix roadmap](docs/fix-roadmap.md) for verified gaps and progress.

| Requirement | Description | Implementation Location |
|---|---|---|
| **REQ-A1 to A6** | **Ambassador Bootstrap & Soft Launch Gate**: Campus locked until 30 active ambassadors each vouch for >=2 users. Launch Readiness Score. | [`LaunchReadinessCalculator.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Domain/Services/LaunchReadinessCalculator.cs), [`Campus.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Domain/Entities/Campus.cs) |
| **REQ-1 to 4** | **Verification & Incubation**: `.ac.lk` domain required. Accounts stay `InIncubation` until 3 unique vouches received (Ambassadors exempt). Values & Interests collected. | [`AuthService.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Infrastructure/Services/AuthService.cs), [`User.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Domain/Entities/User.cs) |
| **REQ-5 to 10** | **Vouching & Anti-Gaming**: Predefined traits (`Sincere`, `Respectful`, etc.), 1 vouch per peer. 1.0 base + 0.2 bonus for high-trust vouchers. Capped at 20.0. Clique dampening (50% reduction for >=3 mutual vouchers). 14-day age rule. 48h anomaly alert (>3 points). | [`TrustScoreCalculator.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Domain/Services/TrustScoreCalculator.cs), [`TrustService.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Infrastructure/Services/TrustService.cs) |
| **REQ-11 to 14** | **Matchmaking & Daily Reflection**: 1 match per 24h. If no quality match, surfaces "Daily Reflection" quote/question based on user's intellectual interests. | [`MatchmakingScorer.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Domain/Services/MatchmakingScorer.cs), [`MatchService.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Infrastructure/Services/MatchService.cs) |
| **REQ-15 to 20** | **Considered Messaging & Slow-Burn**: SignalR letter delivery. Messages < 5 chars do not increment reveal counter. Adaptive threshold (default 40, range 20-80). Clarity progression: 25% at 40%, 60% at 70%, 100% at 100%. | [`SlowBurnCalculator.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Domain/Services/SlowBurnCalculator.cs), [`MessagingService.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Infrastructure/Services/MessagingService.cs) |
| **REQ-21 to 23** | **Pause Chat & Inactivity**: Opt-in pause at any time (preserved). 21 days gentle nudge; 30 days inactivity archives (not deletes). | [`MessagingService.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Infrastructure/Services/MessagingService.cs) |
| **REQ-24 to 27** | **AI Wingman Icebreakers**: Anthropic Claude Haiku integration with 3-second latency budget. Automatic fallback to curated library of 50 pre-written prompts by tag. | [`AnthropicAiWingmanService.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Infrastructure/Services/AnthropicAiWingmanService.cs), [`CuratedIcebreakers.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Application/Features/AiWingman/CuratedIcebreakers.cs) |
| **NFR-4 to 7** | **Security & Privacy**: AES-256 PII encryption at rest, TLS 1.3 transit, permanent account deletion within 30 days. | [`AesEncryptionService.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Infrastructure/Security/AesEncryptionService.cs) |
| **NFR-10 to 14** | **Safety & Moderation**: Report flow (soft-hides reported user), blocking, 3 upheld reports in 90 days = automatic suspension. Architect dashboard. | [`ModerationService.cs`](file:///C:/Users/THIMIRA/Vouch-Backend/src/Vouch.Infrastructure/Services/ModerationService.cs) |

---

## 4. Getting Started

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PostgreSQL 17 or later, or [Docker & Docker Compose](https://www.docker.com/).
- PowerShell 7 for the commands below.

### Running with Docker Compose

Copy `.env.example` to `.env` and fill in `POSTGRES_PASSWORD`, `Jwt__Key` and `Security__EncryptionKey` with three independent strong secrets. In PowerShell 7, generate each value with:

```powershell
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Then start the local stack:

```powershell
Copy-Item .env.example .env # Only when creating your initial configuration.
# Edit .env and supply the required secrets before continuing.
docker compose up -d --build --wait
```

This starts PostgreSQL on localhost:5432 and the API at `http://localhost:5000`. Compose sets the container connection string from the PostgreSQL settings. Reference data is seeded for this local Development setup. Database and photo volumes persist across restarts; changing `.env` does not change credentials in an already initialized database volume.

### Running Locally with .NET CLI

Create `src/Vouch.Api/appsettings.Local.json` as valid JSON, filling in your actual local database connection and independently generated secrets:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=127.0.0.1;Port=5432;Database=vouch_db;Username=vouch;Password=YOUR_DATABASE_PASSWORD"
  },
  "Jwt": {
    "Key": "CHANGE_ME_WITH_AN_INDEPENDENT_RANDOM_SECRET_AT_LEAST_32_CHARACTERS",
    "Issuer": "VouchBackend",
    "Audience": "VouchFrontend"
  },
  "Security": {
    "EncryptionKey": "CHANGE_ME_WITH_ANOTHER_INDEPENDENT_RANDOM_SECRET"
  },
  "Database": { "SeedOnStartup": true }
}
```

The local file is ignored by Git and excluded from build/publish output. It loads only in Development, after base settings and before user secrets, environment variables and command-line arguments. Blank required connection/JWT/encryption settings and placeholder secret values prevent startup. `.env` is Compose syntax; `dotnet run` does not load it.

```powershell
dotnet restore Vouch.slnx
dotnet test tests/Vouch.UnitTests/Vouch.UnitTests.csproj --configuration Release
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Vouch.Api/Vouch.Api.csproj --no-launch-profile --urls http://localhost:5000
```

Check `http://localhost:5000/healthz` for database readiness and `/openapi/v1.json` for the Development API specification. Startup seeds campuses, reflections and icebreakers, with **no fixed admin account**. Secure admin provisioning and replacing the existing EnsureCreated setup with migration-based installation are tracked in Step 5. For upgrades, follow that work before using an existing database.

### Configuration and deployment boundaries

`.env.example` documents CORS browser origins, optional SMTP/Anthropic configuration and photo settings. Empty SMTP host disables delivery; configure all SMTP fields before testing alerts. Empty Anthropic key uses curated fallback prompts. Local photo storage defaults to `wwwroot/photos`; Compose mounts a persistent photo volume. Photo access controls remain Step 13 work.

For deployment, provide secrets through the environment or a secret manager, set the Production environment and `Database__SeedOnStartup=false`, and supply real HTTPS browser origins. The current setup is not production acceptance; the roadmap records remaining work. If fixed credentials from earlier versions were deployed, rotate them explicitly with an appropriate data/key migration; these changes do not rotate existing credentials or encryption keys.

Auth limits are per client IP (5 requests/minute on strict routes) or per authenticated user (20/minute on standard routes). Requests exceeding the limit receive JSON 429 and Retry-After. When running behind a proxy, configure only its actual IP through `ReverseProxy:KnownProxies` (for example `ReverseProxy__KnownProxies__0` in the API environment or a Compose override). The proxy must overwrite forwarded headers. Arbitrary raw forwarded headers do not identify callers. Do not enable blanket forwarded-header trust.

### API and PostgreSQL tests

The full solution test suite requires a separate disposable PostgreSQL server. Follow the [integration test instructions](tests/Vouch.IntegrationTests/README.md) for Docker or the temporary Windows server runner. It creates and removes only its own generated test database, with outbound email/AI and business workers disabled. CI runs the same harness against PostgreSQL 17.

One successful-login test is explicitly pending: randomized encrypted emails cannot currently be found during login. Step 4 repairs that defect; registration, onboarding, database health, migrations, rate limiting and signed JWT authorization are exercised now.
