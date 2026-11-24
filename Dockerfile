# Multi-stage Dockerfile for Sentinel-MCP API
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copy Solution and Project files for caching restore layer
COPY Sentinel.sln ./
COPY nuget.config ./
COPY src/Sentinel.Core/Sentinel.Core.csproj src/Sentinel.Core/
COPY src/Sentinel.Persistence/Sentinel.Persistence.csproj src/Sentinel.Persistence/
COPY src/Sentinel.Api/Sentinel.Api.csproj src/Sentinel.Api/
COPY tests/Sentinel.UnitTests/Sentinel.UnitTests.csproj tests/Sentinel.UnitTests/
COPY tests/Sentinel.ContractTests/Sentinel.ContractTests.csproj tests/Sentinel.ContractTests/
COPY tests/Sentinel.IntegrationTests/Sentinel.IntegrationTests.csproj tests/Sentinel.IntegrationTests/

RUN dotnet restore Sentinel.sln

# Copy source code and build
COPY src/ src/
COPY db/ db/
RUN dotnet publish src/Sentinel.Api/Sentinel.Api.csproj -c Release -o /app/publish --no-restore

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish ./
COPY db/ /app/db/

# Run as non-root user
USER $APP_UID

ENTRYPOINT ["dotnet", "Sentinel.Api.dll"]
