# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PatientFeedback.Core/PatientFeedback.Core.csproj src/PatientFeedback.Core/
COPY src/PatientFeedback.Oracle/PatientFeedback.Oracle.csproj src/PatientFeedback.Oracle/
COPY src/PatientFeedback.Synthetic/PatientFeedback.Synthetic.csproj src/PatientFeedback.Synthetic/
COPY src/PatientFeedback.Web/PatientFeedback.Web.csproj src/PatientFeedback.Web/
COPY src/PatientFeedback.Simulator/PatientFeedback.Simulator.csproj src/PatientFeedback.Simulator/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/PatientFeedback.Web/PatientFeedback.Web.csproj \
    && dotnet restore src/PatientFeedback.Simulator/PatientFeedback.Simulator.csproj

COPY .editorconfig ./
COPY db/ db/
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/PatientFeedback.Web/PatientFeedback.Web.csproj \
        --configuration Release --no-restore --output /app/web -p:UseAppHost=false \
    && dotnet publish src/PatientFeedback.Simulator/PatientFeedback.Simulator.csproj \
        --configuration Release --no-restore --output /app/simulator -p:UseAppHost=false

# ---- visit simulator (demo only) ---------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS simulator
WORKDIR /app
COPY --from=build --link /app/simulator .
USER $APP_UID
ENTRYPOINT ["dotnet", "PatientFeedback.Simulator.dll"]

# ---- web app (default target) ------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, non-root "app" user (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS web
LABEL org.opencontainers.image.title="patient-feedback-web" \
      org.opencontainers.image.source="https://github.com/hekimcan206/patient-feedback-workflow" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/web .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PatientFeedback.Web.dll"]
