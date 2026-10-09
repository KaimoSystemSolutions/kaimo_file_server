# Visit https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when run by VS in quick mode (default for debug configuration).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
# Npgsql probes GSSAPI during startup — keep libgssapi-krb5-2 so password-
# authenticated deployments do not emit a native-library load error.
# postgresql-client-17 (pg_dump) lets the Web UI create on-demand backups into
# the shared backup folder (a read-only dump; the destructive restore stays on
# the Host). Pulled from the PGDG apt repo so the client matches the server.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libgssapi-krb5-2 openssh-client rsync smbclient ca-certificates curl gnupg \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(. /etc/os-release && echo $VERSION_CODENAME)-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update && apt-get install -y --no-install-recommends postgresql-client-17 \
    && apt-get purge -y --auto-remove curl gnupg \
    && rm -rf /var/lib/apt/lists/*
# restic (file backup/restore) — the pinned official release binary, verified
# against its published SHA-256 before installation. Bump all three ARGs
# together (checksums from the release's SHA256SUMS file). BSD-2-Clause; the
# license ships with the image (see NOTICE, "Third-party binaries").
ARG TARGETARCH
ARG RESTIC_VERSION=0.19.1
ARG RESTIC_SHA256_AMD64=f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c
ARG RESTIC_SHA256_ARM64=a5f64aaab53d51e311fa3829124c5b703f2d14cf187d8640b6be3b2b49376465
RUN apt-get update && apt-get install -y --no-install-recommends curl bzip2 \
    && arch="${TARGETARCH:-amd64}" \
    && case "$arch" in \
         amd64) sha="$RESTIC_SHA256_AMD64" ;; \
         arm64) sha="$RESTIC_SHA256_ARM64" ;; \
         *) echo "unsupported architecture for restic: $arch" >&2; exit 1 ;; \
       esac \
    && curl -fsSL -o /tmp/restic.bz2 \
        "https://github.com/restic/restic/releases/download/v${RESTIC_VERSION}/restic_${RESTIC_VERSION}_linux_${arch}.bz2" \
    && echo "${sha}  /tmp/restic.bz2" | sha256sum -c - \
    && bunzip2 -c /tmp/restic.bz2 > /usr/local/bin/restic \
    && chmod 0755 /usr/local/bin/restic \
    && rm /tmp/restic.bz2 \
    && apt-get purge -y --auto-remove curl bzip2 \
    && rm -rf /var/lib/apt/lists/* \
    && restic version
COPY --chmod=0644 third-party/restic/LICENSE /usr/share/doc/restic/LICENSE
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081
EXPOSE 8443


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
COPY ["src/Kaimo_File_Server.Web/Kaimo_File_Server.Web.csproj", "src/Kaimo_File_Server.Web/"]
COPY ["src/Kaimo_File_Server.Core/Kaimo_File_Server.Core.csproj", "src/Kaimo_File_Server.Core/"]
COPY ["src/Kaimo_File_Server.Infrastructure/Kaimo_File_Server.Infrastructure.csproj", "src/Kaimo_File_Server.Infrastructure/"]
COPY ["src/Kaimo_File_Server.Search/Kaimo_File_Server.Search.csproj", "src/Kaimo_File_Server.Search/"]
RUN dotnet restore "./src/Kaimo_File_Server.Web/Kaimo_File_Server.Web.csproj" -a $TARGETARCH
# Copy only source code into build image — no tests/, docs/, tools/ and no
# IDE/build artifacts (additionally kept out by .dockerignore from context).
COPY src/ src/
WORKDIR "/src/src/Kaimo_File_Server.Web"
RUN dotnet publish "./Kaimo_File_Server.Web.csproj" \
    -c $BUILD_CONFIGURATION \
    -a $TARGETARCH \
    --no-restore \
    --no-self-contained \
    -o /app/publish \
    /p:UseAppHost=false

# The Blazor loader is a framework static asset in .NET 10. Keep a broken
# restore layer from reaching Docker Hub as a seemingly healthy web image.
RUN grep -q '"Route":"_framework/blazor.web.js"' \
    /app/publish/Kaimo_File_Server.Web.staticwebassets.endpoints.json

#RUN dotnet publish "./Kaimo_File_Server.Web.csproj" \
#    -c $BUILD_CONFIGURATION \
#    -a $TARGETARCH \
#    --no-restore \
#    --no-self-contained \
#    -o /app/publish \
#    /p:UseAppHost=false

# This stage is used in production or when run by VS in regular mode (default if debug configuration is not used).
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Kaimo_File_Server.Web.dll"]
