# Build and publish HiveOps.Api (context = repository root)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/HiveOps.Api/HiveOps.Api.csproj
RUN dotnet publish src/HiveOps.Api/HiveOps.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
RUN groupadd --gid 1654 app && useradd --uid 1654 --gid app --shell /bin/bash --create-home app
COPY --from=build /app/publish .
RUN mkdir -p /app/data/dataprotection /tmp/hiveops-git-work && chown -R app:app /app/data /tmp/hiveops-git-work
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HiveOps.Api.dll"]
