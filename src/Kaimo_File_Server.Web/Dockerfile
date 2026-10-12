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
# license ships with the image (see NOTICE, "Third-party binaries"), together with
# the licenses of the Go modules compiled into the binary. After a version bump,
# regenerate them with third-party/restic/update-licenses.py — the build below
# fails while THIRD_PARTY_LICENSES.txt belongs to another restic version.
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
COPY --chmod=0644 third-party/restic/LICENSE third-party/restic/THIRD_PARTY_LICENSES.txt /usr/share/doc/restic/
# COPY --chmod also applies to the directory it creates; make it traversable again.
RUN chmod 0755 /usr/share/doc/restic \
    && grep -q "^restic ${RESTIC_VERSION} " /usr/share/doc/restic/THIRD_PARTY_LICENSES.txt \
    || { echo "third-party/restic/THIRD_PARTY_LICENSES.txt is not for restic ${RESTIC_VERSION}: run update-licenses.py" >&2; exit 1; }
# rclone — backend of restic repositories of type "rclone" (restic starts
# "rclone serve restic --stdio"). Pinned official release, verified like restic
# (checksums from the release's SHA256SUMS file). MIT; its license and the
# licenses of its Go modules ship like restic's (third-party/rclone/, regenerate
# with third-party/restic/update-licenses.py after a version bump). The ARGs
# must not start with RCLONE_: rclone reads every RCLONE_* variable as a flag.
ARG KAIMO_RCLONE_VERSION=1.75.2
ARG KAIMO_RCLONE_SHA256_AMD64=349ac8fba6ff65d6247043f1750cdcb518ec5d500ef91463a10d37c0ccdf3702
ARG KAIMO_RCLONE_SHA256_ARM64=7e1e8d69654941b7b7df84ee74c5f7fb09cce7d5496947fb7bf16b55ff427d10
RUN apt-get update && apt-get install -y --no-install-recommends curl unzip \
    && arch="${TARGETARCH:-amd64}" \
    && case "$arch" in \
         amd64) sha="$KAIMO_RCLONE_SHA256_AMD64" ;; \
         arm64) sha="$KAIMO_RCLONE_SHA256_ARM64" ;; \
         *) echo "unsupported architecture for rclone: $arch" >&2; exit 1 ;; \
       esac \
    && curl -fsSL -o /tmp/rclone.zip \
        "https://downloads.rclone.org/v${KAIMO_RCLONE_VERSION}/rclone-v${KAIMO_RCLONE_VERSION}-linux-${arch}.zip" \
    && echo "${sha}  /tmp/rclone.zip" | sha256sum -c - \
    && unzip -p /tmp/rclone.zip "rclone-v${KAIMO_RCLONE_VERSION}-linux-${arch}/rclone" > /usr/local/bin/rclone \
    && chmod 0755 /usr/local/bin/rclone \
    && rm /tmp/rclone.zip \
    && apt-get purge -y --auto-remove curl unzip \
    && rm -rf /var/lib/apt/lists/* \
    && rclone version
COPY --chmod=0644 third-party/rclone/LICENSE third-party/rclone/THIRD_PARTY_LICENSES.txt /usr/share/doc/rclone/
# Source of the LGPL-3.0 libraries compiled into rclone (LGPL-3.0 section 4).
COPY --chmod=0644 third-party/rclone/source/ /usr/share/doc/rclone/source/
RUN chmod 0755 /usr/share/doc/rclone /usr/share/doc/rclone/source \
    && grep -q "^rclone ${KAIMO_RCLONE_VERSION} " /usr/share/doc/rclone/THIRD_PARTY_LICENSES.txt \
    || { echo "third-party/rclone/THIRD_PARTY_LICENSES.txt is not for rclone ${KAIMO_RCLONE_VERSION}: run update-licenses.py" >&2; exit 1; }
# Project license and notices, and the exact list of OS packages in this image
# with their source packages (see NOTICE, "Operating system packages"). Keep
# this after the last apt-get install so the list is complete.
COPY --chmod=0644 LICENSE NOTICE /usr/share/doc/kaimo/
RUN chmod 0755 /usr/share/doc/kaimo \
    && dpkg-query -W -f='${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\n' \
        | sort > /usr/share/doc/kaimo/os-packages.txt
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
# License texts and notices of the NuGet packages published into /app (see
# NOTICE, "Third-party components"). After a package change, regenerate them with
# third-party/nuget/update-licenses.py — the build fails while the file belongs
# to a different package list than this application's deps.json.
COPY --chmod=0644 third-party/nuget/Kaimo_File_Server.Web.THIRD_PARTY_LICENSES.txt /usr/share/doc/kaimo/NUGET_THIRD_PARTY_LICENSES.txt
RUN sum=$(grep -o '"path": "[^"]*"' Kaimo_File_Server.Web.deps.json | cut -d'"' -f4 | LC_ALL=C sort -u | sha256sum | cut -d' ' -f1) \
    && grep -qx "Package list SHA-256: $sum" /usr/share/doc/kaimo/NUGET_THIRD_PARTY_LICENSES.txt \
    || { echo "third-party/nuget/Kaimo_File_Server.Web.THIRD_PARTY_LICENSES.txt does not match the published packages: run third-party/nuget/update-licenses.py" >&2; exit 1; }
ENTRYPOINT ["dotnet", "Kaimo_File_Server.Web.dll"]
