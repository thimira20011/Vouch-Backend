# Stage 1: Build & Restore
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy project files for caching
COPY ["Vouch.slnx", "./"]
COPY ["src/Vouch.Domain/Vouch.Domain.csproj", "src/Vouch.Domain/"]
COPY ["src/Vouch.Application/Vouch.Application.csproj", "src/Vouch.Application/"]
COPY ["src/Vouch.Infrastructure/Vouch.Infrastructure.csproj", "src/Vouch.Infrastructure/"]
COPY ["src/Vouch.Api/Vouch.Api.csproj", "src/Vouch.Api/"]
COPY ["tests/Vouch.UnitTests/Vouch.UnitTests.csproj", "tests/Vouch.UnitTests/"]

RUN dotnet restore "src/Vouch.Api/Vouch.Api.csproj"

# Copy source and publish
COPY . .
WORKDIR "/src/src/Vouch.Api"
RUN dotnet publish "Vouch.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Minimal Runtime (Green Coding / Alpine footprint)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

# Step 12: Health check — Docker monitors container liveness via /healthz
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Vouch.Api.dll"]
