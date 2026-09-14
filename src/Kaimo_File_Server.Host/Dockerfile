# Visit https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when run by VS in quick mode (default for debug configuration).
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER root
# Npgsql probes GSSAPI during startup — keep libgssapi-krb5-2 so password-
# authenticated deployments do not emit a native-library load error.
# postgresql-client-17 (pg_dump/pg_restore) is required because the Host owns the
# database: it takes the scheduled and pre-migration backups and runs the
# one-shot startup restore. Pulled from the PGDG apt repo so the client version
# matches the PostgreSQL 17 server (pg_dump must be >= the server version).
RUN apt-get update && apt-get install -y --no-install-recommends \
        libgssapi-krb5-2 ca-certificates curl gnupg \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(. /etc/os-release && echo $VERSION_CODENAME)-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update && apt-get install -y --no-install-recommends postgresql-client-17 \
    && apt-get purge -y --auto-remove curl gnupg \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app


# Build on the native build platform, but publish only the runtime assets for
# the target architecture. Without -a, packages such as SkiaSharp copy native
# binaries for every supported OS/architecture into the image.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src
# nuget.config first → deterministic restore (nuget.org as sole source).
COPY ["nuget.config", "."]
# Central Package Management: restore reads all package versions from here.
COPY ["Directory.Packages.props", "."]
COPY ["src/Kaimo_File_Server.Host/Kaimo_File_Server.Host.csproj", "src/Kaimo_File_Server.Host/"]
COPY ["src/Kaimo_File_Server.Core/Kaimo_File_Server.Core.csproj", "src/Kaimo_File_Server.Core/"]
COPY ["src/Kaimo_File_Server.Infrastructure/Kaimo_File_Server.Infrastructure.csproj", "src/Kaimo_File_Server.Infrastructure/"]
COPY ["src/Kaimo_File_Server.Search/Kaimo_File_Server.Search.csproj", "src/Kaimo_File_Server.Search/"]
RUN dotnet restore "./src/Kaimo_File_Server.Host/Kaimo_File_Server.Host.csproj" -a $TARGETARCH
# Copy only source code into build image — no tests/, docs/, tools/ and no
# IDE/build artifacts (additionally kept out by .dockerignore from context).
COPY src/ src/
WORKDIR "/src/src/Kaimo_File_Server.Host"
RUN dotnet publish "./Kaimo_File_Server.Host.csproj" \
    -c $BUILD_CONFIGURATION \
    -a $TARGETARCH \
    --no-restore \
    --no-self-contained \
    -o /app/publish \
    /p:UseAppHost=false

# This stage is used in production or when run by VS in regular mode (default if debug configuration is not used).
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Kaimo_File_Server.Host.dll"]
