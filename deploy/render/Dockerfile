# syntax=docker/dockerfile:1
# Free web service only: no disks, workers, paid pre-deploy job or database image.
FROM node:22.23.4-bookworm-slim AS frontend
WORKDIR /src
COPY TaskFlow-FE/package*.json ./
RUN npm install --global npm@11.3.0 && npm ci
COPY TaskFlow-FE/ ./
ARG VITE_WORKSPACES_ENABLED=true
ARG VITE_WORKSPACE_LIFECYCLE_ENABLED=true
ARG VITE_INVITATIONS_ENABLED=false
RUN VITE_WORKSPACES_ENABLED=$VITE_WORKSPACES_ENABLED VITE_WORKSPACE_LIFECYCLE_ENABLED=$VITE_WORKSPACE_LIFECYCLE_ENABLED VITE_INVITATIONS_ENABLED=$VITE_INVITATIONS_ENABLED npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble AS backend
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY .config/ .config/
COPY TaskFlow-BE/ TaskFlow-BE/
RUN dotnet restore TaskFlow-BE/TaskFlow.Api --locked-mode && dotnet publish TaskFlow-BE/TaskFlow.Api -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble AS app
WORKDIR /app
COPY --from=backend /out/ ./
COPY --from=frontend /src/dist/ ./wwwroot/
COPY --chmod=0555 deploy/render/start.sh ./start.sh
ENV ASPNETCORE_ENVIRONMENT=Production DOTNET_GCHeapHardLimit=0x10000000
EXPOSE 10000
USER $APP_UID
ENTRYPOINT ["/bin/sh", "/app/start.sh"]
