# syntax=docker/dockerfile:1

# ── Build ─────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so this layer is cached until the package list changes.
COPY SmartSchoolAI.csproj ./
RUN dotnet restore SmartSchoolAI.csproj

COPY . ./
RUN dotnet publish SmartSchoolAI.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ── Runtime ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Everything the app writes lives in two places, both mounted as volumes:
#   /app/App_Data              SQLite database, uploaded photos/avatars/attachments, logs, backups
#   /home/app/.aspnet          cookie/antiforgery keys (lost keys sign every user out on redeploy)
# They are created and owned by the unprivileged `app` user so a fresh named volume is writable.
RUN mkdir -p /app/App_Data /home/app/.aspnet \
 && chown -R app:app /app/App_Data /home/app/.aspnet

COPY --from=build --chown=app:app /app/publish ./

USER app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Default="Data Source=/app/App_Data/smartschool.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartSchoolAI.dll"]
