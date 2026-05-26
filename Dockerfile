# syntax=docker/dockerfile:1.7

# -----------------------------------------------------------------------------
# Build stage
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the files needed to restore first to maximise layer caching.
COPY Directory.Build.props Directory.Packages.props NuGet.config* ./
COPY MultiRepoMcp.slnx ./
COPY src/MultiRepoMcp/MultiRepoMcp.csproj src/MultiRepoMcp/

RUN dotnet restore src/MultiRepoMcp/MultiRepoMcp.csproj

# Copy the rest of the source and publish.
COPY src/ src/
RUN dotnet publish src/MultiRepoMcp/MultiRepoMcp.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# -----------------------------------------------------------------------------
# Runtime stage
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as a non-root user. The aspnet base image already creates a non-root
# 'app' user (uid 1654); use it explicitly so we don't accidentally run as root.
USER app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "MultiRepoMcp.dll"]
