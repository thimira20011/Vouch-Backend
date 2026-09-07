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
- [Docker & Docker Compose](https://www.docker.com/) (Optional for local PostgreSQL)

### Running with Docker Compose
```bash
docker-compose up -d
```
This spins up PostgreSQL 17 and the Vouch API on `http://localhost:5000`.

### Running Locally with .NET CLI
```bash
# 1. Restore dependencies
dotnet restore Vouch.slnx

# 2. Run unit tests
dotnet test

# 3. Start the API
dotnet run --project src/Vouch.Api/Vouch.Api.csproj
```

The API will start and automatically seed initial data (campuses, admin "The Architect", reflection prompts, and fallback icebreakers) into the database.
Open `http://localhost:5000/openapi/v1.json` or query `http://localhost:5000/` for service health.
