# Visit https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when run by VS in quick mode (default for debug configuration).
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app


# This stage is used to build the service project.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# nuget.config first → deterministic restore (nuget.org as sole source).
COPY ["nuget.config", "."]
COPY ["src/Kaimo_File_Server.Host/Kaimo_File_Server.Host.csproj", "src/Kaimo_File_Server.Host/"]
COPY ["src/Kaimo_File_Server.Core/Kaimo_File_Server.Core.csproj", "src/Kaimo_File_Server.Core/"]
COPY ["src/Kaimo_File_Server.Infrastructure/Kaimo_File_Server.Infrastructure.csproj", "src/Kaimo_File_Server.Infrastructure/"]
COPY ["src/Kaimo_File_Server.Smb/Kaimo_File_Server.Smb.csproj", "src/Kaimo_File_Server.Smb/"]
RUN dotnet restore "./src/Kaimo_File_Server.Host/Kaimo_File_Server.Host.csproj"
# Copy only source code into build image — no tests/, docs/, tools/ and no
# IDE/build artifacts (additionally kept out by .dockerignore from context).
COPY src/ src/
WORKDIR "/src/src/Kaimo_File_Server.Host"
RUN dotnet build "./Kaimo_File_Server.Host.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied into the final stage.
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Kaimo_File_Server.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when run by VS in regular mode (default if debug configuration is not used).
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Kaimo_File_Server.Host.dll"]