# Build context must be the AgentPermit repo root, because the gateway project links
# config/first-action.json (the single source of truth for the action definition).
#   az acr build -t agentpermit-gateway:week2 -r acragentper5a7a3d60 -f backend/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer-cached on unchanged csproj).
COPY backend/src/AgentPermit.Gateway/AgentPermit.Gateway.csproj backend/src/AgentPermit.Gateway/
RUN dotnet restore backend/src/AgentPermit.Gateway/AgentPermit.Gateway.csproj

# Copy the gateway source and the linked data files (action definition + policy snapshot), then publish.
COPY backend/src/ backend/src/
COPY config/ config/
COPY policies/ policies/
RUN dotnet publish backend/src/AgentPermit.Gateway/AgentPermit.Gateway.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The aspnet image already defaults ASPNETCORE_HTTP_PORTS=8080; make it explicit.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AgentPermit.Gateway.dll"]
