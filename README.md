# MigrationTool

A Dockerized .NET migration runner solution.

## Solution structure

- `MigrationTool.sln`
- `src/MigrationTool.App` (console app)
- `migrations` (SQL scripts mounted into the container)

## Local run

```bash
dotnet restore MigrationTool.sln
dotnet run --project src/MigrationTool.App -- --migrations-path ./migrations --dry-run
```

## Docker run

```bash
docker compose up --build
```

This starts the app in dry-run mode and reads SQL files from `./migrations`.

To apply mode (not dry-run):

```bash
docker compose run --rm migration-tool
```

To pass custom args:

```bash
docker compose run --rm migration-tool --migrations-path /app/migrations --dry-run
```
