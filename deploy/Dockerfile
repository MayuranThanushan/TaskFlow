# syntax=docker/dockerfile:1
FROM node:22.23.4-bookworm-slim AS frontend
WORKDIR /src
COPY TaskFlow-FE/package*.json ./
RUN npm install --global npm@11.3.0 && npm ci
COPY TaskFlow-FE/ ./
ARG VITE_WORKSPACES_ENABLED=false
ARG VITE_WORKSPACE_LIFECYCLE_ENABLED=false
RUN VITE_WORKSPACES_ENABLED=$VITE_WORKSPACES_ENABLED VITE_WORKSPACE_LIFECYCLE_ENABLED=$VITE_WORKSPACE_LIFECYCLE_ENABLED npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble AS backend
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY .config/ .config/
COPY TaskFlow-BE/ TaskFlow-BE/
RUN dotnet restore TaskFlow-BE/TaskFlow.Api --locked-mode && dotnet tool restore
RUN dotnet build TaskFlow-BE/TaskFlow.Api -c Release --no-restore && dotnet publish TaskFlow-BE/TaskFlow.Api -c Release --no-build --no-restore -o /out /p:UseAppHost=false

FROM backend AS migration-bundle
ARG TARGETARCH
RUN mkdir -p /bundle
RUN case "$TARGETARCH" in amd64) rid=x64 ;; arm64) rid=arm64 ;; *) exit 1 ;; esac; \
    ConnectionStrings__TaskFlow='Server=127.0.0.1;Database=taskflow_design;User ID=unused;Password=unused' \
    dotnet ef migrations bundle --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build --target-runtime linux-$rid --output /bundle/efbundle

# Explicit one-shot migration job; no SDK or schema changes in web startup.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble AS migrate
WORKDIR /app
COPY --from=migration-bundle --chmod=0555 /bundle/efbundle ./efbundle
COPY --from=backend /out/appsettings.json ./appsettings.json
COPY deploy/migrate.sh ./migrate.sh
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/taskflow-bundle
USER $APP_UID
ENTRYPOINT ["/bin/sh", "/app/migrate.sh"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble AS app
WORKDIR /app
COPY --from=backend /out/ ./
COPY --from=frontend /src/dist/ ./wwwroot/
RUN mkdir -p /var/lib/taskflow/keys && chown app:app /var/lib/taskflow/keys
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=https://+:8443 DataProtection__KeysPath=/var/lib/taskflow/keys TASKFLOW_SECRETS_DIRECTORY=/run/secrets
EXPOSE 8443
USER $APP_UID
ENTRYPOINT ["dotnet", "TaskFlow.Api.dll"]
