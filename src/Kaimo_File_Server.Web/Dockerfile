# Visit https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when run by VS in quick mode (default for debug configuration).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
# Npgsql probes GSSAPI during startup. Keep the runtime dependency available so
# password-authenticated deployments do not emit a native-library load error.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
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
