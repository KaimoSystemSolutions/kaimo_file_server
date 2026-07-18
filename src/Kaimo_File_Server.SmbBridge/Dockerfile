# Kaimo SMB Bridge (gRPC-Control-Plane). Context = Repo-Wurzel (Kaimo_File_Server).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# libgssapi-krb5-2: Npgsql versucht optional GSSAPI und meckert sonst beim Start.
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 5080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# nuget.config zuerst -> deterministischer Restore (nuget.org als einzige Quelle).
COPY ["nuget.config", "."]
COPY ["src/Kaimo_File_Server.SmbBridge/Kaimo_File_Server.SmbBridge.csproj", "src/Kaimo_File_Server.SmbBridge/"]
COPY ["src/Kaimo_File_Server.Core/Kaimo_File_Server.Core.csproj", "src/Kaimo_File_Server.Core/"]
COPY ["src/Kaimo_File_Server.Infrastructure/Kaimo_File_Server.Infrastructure.csproj", "src/Kaimo_File_Server.Infrastructure/"]
COPY ["src/Kaimo_File_Server.Search/Kaimo_File_Server.Search.csproj", "src/Kaimo_File_Server.Search/"]
RUN dotnet restore "./src/Kaimo_File_Server.SmbBridge/Kaimo_File_Server.SmbBridge.csproj"
# Nur den Quellcode ins Build-Image kopieren -- kein tests/, docu/, tools/ und
# keine IDE-/Build-Artefakte (die haelt zusaetzlich .dockerignore aus dem Context).
COPY src/ src/
# Proto-Vertrag, den die SmbBridge.csproj einbindet (liegt ausserhalb von src/).
COPY samba-vfs/protos/ samba-vfs/protos/
WORKDIR "/src/src/Kaimo_File_Server.SmbBridge"
RUN dotnet build "./Kaimo_File_Server.SmbBridge.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Kaimo_File_Server.SmbBridge.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Kaimo_File_Server.SmbBridge.dll"]
