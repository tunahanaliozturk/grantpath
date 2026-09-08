# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source

# Restore before the rest of the source is copied, so editing a .cs file does not invalidate the restore
# layer. The .editorconfig comes along because analyzer severities live in it and the build treats
# warnings as errors, so leaving it out makes the image build fail where a local build passes.
COPY global.json .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src/GrantPath.Abac/GrantPath.Abac.csproj src/GrantPath.Abac/
COPY src/GrantPath.Rebac/GrantPath.Rebac.csproj src/GrantPath.Rebac/
COPY src/GrantPath.Data/GrantPath.Data.csproj src/GrantPath.Data/
COPY src/GrantPath.Api/GrantPath.Api.csproj src/GrantPath.Api/
RUN dotnet restore src/GrantPath.Api/GrantPath.Api.csproj

COPY src/ src/
RUN dotnet publish src/GrantPath.Api/GrantPath.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# The image ships a non-root user. The only reason services still run as root is that nobody changed it.
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app .

ENTRYPOINT ["dotnet", "GrantPath.Api.dll"]
