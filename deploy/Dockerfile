# Build and publish HiveOps.Api (context = repository root)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/HiveOps.Api/HiveOps.Api.csproj
RUN dotnet publish src/HiveOps.Api/HiveOps.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
# git is required by TenantGitService (repo clone/ls-remote/deploy); openssh for ssh remotes
RUN apt-get update && apt-get install -y --no-install-recommends git openssh-client ca-certificates \
    && rm -rf /var/lib/apt/lists/*
# aspnet:9.0 base image already ships the non-root `app` user (uid 1654)
COPY --from=build /app/publish .
RUN mkdir -p /app/data/dataprotection /tmp/hiveops-git-work /tmp/hiveops-git-workspaces \
    && chown -R app:app /app/data /tmp/hiveops-git-work /tmp/hiveops-git-workspaces
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HiveOps.Api.dll"]
