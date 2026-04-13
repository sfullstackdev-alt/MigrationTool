FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MigrationTool.sln ./
COPY src/MigrationTool.App/MigrationTool.App.csproj src/MigrationTool.App/
COPY src/MigrationTool.CloneAndBuild/MigrationTool.CloneAndBuild.csproj src/MigrationTool.CloneAndBuild/
RUN dotnet restore MigrationTool.sln

COPY . .
RUN dotnet publish src/MigrationTool.App/MigrationTool.App.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates docker.io \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "MigrationTool.App.dll"]
