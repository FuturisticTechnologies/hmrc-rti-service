# ---------------------------------------------------------------------------
# hmrc-rti-service
# Multi-stage: restore and publish on the SDK image, run on the smaller runtime.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file first so `restore` is cached independently of source
# changes - a code-only edit does not re-download the package graph.
COPY src/Hmrc.Rti.Api/Hmrc.Rti.Api.csproj src/Hmrc.Rti.Api/
RUN dotnet restore src/Hmrc.Rti.Api/Hmrc.Rti.Api.csproj

COPY . .
RUN dotnet publish src/Hmrc.Rti.Api/Hmrc.Rti.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish

# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

COPY --from=build /app/publish .

# Run unprivileged. The image ships a non-root `app` user for this.
USER app

EXPOSE 8080

# Liveness only - readiness (which checks Postgres) is the orchestrator's probe,
# so a database blip does not get the container restarted.
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
    CMD ["dotnet", "--version"]

ENTRYPOINT ["dotnet", "Hmrc.Rti.Api.dll"]
