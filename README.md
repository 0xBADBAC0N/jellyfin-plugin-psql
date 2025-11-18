# Jellyfin PostgreSQL Database Provider

This plugin swaps Jellyfin’s embedded SQLite database for PostgreSQL by supplying a custom `IJellyfinDatabaseProvider`. It bundles the required EF Core migrations so Jellyfin can create/upgrade the schema directly in PostgreSQL without patching the upstream server.

## Compatibility

- Target ABI / Jellyfin release: **10.12.x** (see `manifest.json:10`)
- .NET runtime: **net9.0**
- Database: PostgreSQL 14+ (tested with `postgres:16`)

## (Optional) Install .NET 9 SDK on Ubuntu 24.04

```bash
# Install prerequisites
sudo apt-get update
sudo apt-get install -y ca-certificates curl gnupg

# Add Microsoft package feed (no apt package exists yet for dotnet-sdk-9.0 on Noble)
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 9.0 --install-dir $HOME/.dotnet

# Export PATH/DOTNET_ROOT in your shell (e.g., add to ~/.bashrc)
export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH
export DOTNET_ROOT=$HOME/.dotnet

# Verify
dotnet --info
```

## Build & Package (bash)

```bash
# Prerequisites: .NET 9 SDK on PATH and this repo checked out
export JellyfinRepositoryPath=/mnt/d/wsl/projects/jellyfin   # adjust to your Jellyfin clone

# 1. Publish the provider
dotnet publish src/Jellyfin.Plugin.Postgres/Jellyfin.Plugin.Postgres.csproj \
  -c Release \
  -o publish

# 2. Bundle manifest + binaries into a plugin folder & zip
PLUGIN_DIR=postgres-database-provider
rm -rf "dist/$PLUGIN_DIR"
mkdir -p "dist/$PLUGIN_DIR"
cp manifest.json "dist/$PLUGIN_DIR/"
cp publish/* "dist/$PLUGIN_DIR/"
(cd dist && zip -r "../${PLUGIN_DIR}.zip" "$PLUGIN_DIR")

echo "Folder dist/$PLUGIN_DIR and archive ${PLUGIN_DIR}.zip are ready to mount under /config/plugins"
```

## Docker compose (Postgres + Jellyfin + plugin)

```yaml
services:
  jellyfin-config-init:
    image: busybox:1.37
    volumes:
      - jellyfin_config:/config
    entrypoint: ["/bin/sh","-c"]
    command: |
      set -e
      mkdir -p /config/data
      cat <<'JSON' > /config/data/database.json
      {
        "DatabaseType": "PLUGIN_PROVIDER",
        "CustomProviderOptions": {
          "PluginName": "postgres-database-provider",
          "PluginAssembly": "Jellyfin.Plugin.Postgres",
          "ConnectionString": "Host=jellyfin-db;Database=jellyfin;Username=jellyfin;Password=supersecret",
          "Options": [
            { "Key": "default-schema", "Value": "public" },
            { "Key": "command-timeout", "Value": "60" },
            { "Key": "enable-retry-on-failure", "Value": "true" },
            { "Key": "retry-count", "Value": "5" },
            { "Key": "retry-delay-seconds", "Value": "15" },
            { "Key": "builder:SearchPath", "Value": "public" }
          ]
        },
        "LockingBehavior": "NoLock"
      }
      JSON

  jellyfin-db:
    image: postgres:16
    restart: unless-stopped
    environment:
      POSTGRES_DB: jellyfin
      POSTGRES_USER: jellyfin
      POSTGRES_PASSWORD: supersecret
    volumes:
      - jellyfin_pgdata:/var/lib/postgresql/data

  jellyfin:
    image: jellyfin/jellyfin:latest
    depends_on:
      jellyfin-config-init:
        condition: service_completed_successfully
      - jellyfin-db
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin
    ports:
      - "8096:8096"
    volumes:
      - jellyfin_config:/config
      - jellyfin_cache:/cache
      - ./postgres-database-provider.zip:/config/plugins/postgres-database-provider.zip:ro
volumes:
  jellyfin_config:
  jellyfin_cache:
  jellyfin_pgdata:
```

The lightweight `jellyfin-config-init` container runs once to seed `/config/data/database.json`. Subsequent Jellyfin restarts reuse the same file; change the heredoc contents if you need different settings.

## Supported custom options

1. Provision PostgreSQL (standalone or via `docker-compose`). Minimal example:

   ```yaml
   services:
     jellyfin-db:
       image: postgres:16
       restart: unless-stopped
       environment:
         POSTGRES_DB: jellyfin
         POSTGRES_USER: jellyfin
         POSTGRES_PASSWORD: supersecret
       volumes:
         - jellyfin_pgdata:/var/lib/postgresql/data
   ```

2. Mount the plugin bundle into the Jellyfin container:

   ```yaml
   services:
     jellyfin:
       image: jellyfin/jellyfin:latest
       volumes:
         - jellyfin_config:/config
         - jellyfin_cache:/cache
         - ./postgres-database-provider.zip:/config/plugins/postgres-database-provider.zip:ro
   ```

3. Inside `/config/data/database.json` set the database type to the plugin provider and supply the connection info:

   ```json
   {
     "DatabaseType": "PLUGIN_PROVIDER",
     "CustomProviderOptions": {
       "PluginName": "postgres-database-provider",
       "PluginAssembly": "Jellyfin.Plugin.Postgres",
       "ConnectionString": "Host=jellyfin-db;Database=jellyfin;Username=jellyfin;Password=supersecret", 
       "Options": [
         { "Key": "default-schema", "Value": "public" },
         { "Key": "command-timeout", "Value": "60" },
         { "Key": "enable-retry-on-failure", "Value": "true" },
         { "Key": "retry-count", "Value": "5" },
         { "Key": "retry-delay-seconds", "Value": "15" },
         { "Key": "builder:SearchPath", "Value": "public" }
       ]
     },
     "LockingBehavior": "NoLock"
   }
   ```

   - `PluginName` must match the folder/zip name dropped into `/config/plugins`
   - `PluginAssembly` is the DLL without extension
   - `ConnectionString` is passed to `NpgsqlConnectionStringBuilder`
   - Use `builder:<Option>` entries to override arbitrary connection-string keywords (e.g. `builder:SearchPath`)

4. Restart Jellyfin. The server logs should include: `Configured PostgreSQL connection: Host=..., Database=...` if loading succeeded.

## Supported custom options

| Key | Description |
| --- | --- |
| `default-schema` | Schema used for the EF migrations history table |
| `command-timeout` | Command timeout (seconds) passed to EF Core |
| `enable-retry-on-failure` | Enables `EnableRetryOnFailure` for transient errors |
| `retry-count` | Overrides retry attempts (default 5 when retries enabled) |
| `retry-delay-seconds` | Delay between retries (default 15s) |
| `EnableSensitiveDataLogging` | Enables EF Core sensitive logging (debug only) |
| `builder:*` | Sets a raw `NpgsqlConnectionStringBuilder` property, e.g. `builder:SearchPath` |

Backups (`MigrationBackupFast`, `RestoreBackupFast`, `DeleteBackup`) are intentionally unsupported because PostgreSQL backups should be handled through your DBA tooling (pg_dump, snapshots, etc.).

## Updating migrations

The repository already includes a PostgreSQL migration history (`Migrations/*`) matching the current Jellyfin entity model. Whenever the upstream schema changes you must regenerate them:

```bash
# (Once) install EF tooling
dotnet tool install --global dotnet-ef

# Regenerate migrations
JellyfinRepositoryPath=/absolute/path/to/jellyfin \
  dotnet ef migrations add <MigrationName> \
    --project src/Jellyfin.Plugin.Postgres/Jellyfin.Plugin.Postgres.csproj \
    --output-dir Migrations \
    --context Jellyfin.Database.Implementations.JellyfinDbContext

# optional: remove the previous snapshot/migration if you are replacing it
```

The design-time factory uses a dummy local connection string; only the SQL it outputs is relevant.

## Next steps

- Generate PostgreSQL-specific EF Core migrations (both the base schema and future updates)
- Harden the provider (additional options, connection resiliency, automated tests)
- Publish the compiled bundle to GitHub Releases or another artifact host for easier installation
