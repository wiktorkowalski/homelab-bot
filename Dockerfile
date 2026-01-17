# Stage 1: Build React frontend
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend
COPY src/AdminDashboard/package*.json ./
RUN npm ci
COPY src/AdminDashboard/ ./
RUN npm run build -- --outDir=dist

# Stage 2: Build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY src/HomelabBot/*.csproj ./
RUN dotnet restore
COPY src/HomelabBot/ ./
# Copy frontend build output to backend wwwroot
COPY --from=frontend-build /app/frontend/dist ./wwwroot
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false /p:EnforceCodeStyleInBuild=false /p:RunAnalyzers=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install curl for healthcheck
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

COPY --from=backend-build /app/publish .
ENTRYPOINT ["dotnet", "HomelabBot.dll"]
