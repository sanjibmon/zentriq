# Zentriq License Server. Build from the repository root (it shares backend/Zentriq.Domain and Zentriq.Application):
#   docker build -f licensing/Dockerfile -t zentriq-license-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/Zentriq.Domain backend/Zentriq.Domain
COPY backend/Zentriq.Application backend/Zentriq.Application
COPY licensing/Directory.Build.props licensing/Directory.Packages.props licensing/
COPY licensing/Zentriq.LicenseServer licensing/Zentriq.LicenseServer
RUN dotnet publish licensing/Zentriq.LicenseServer/Zentriq.LicenseServer.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    LicenseServer__SigningKeyFile=/run/secrets/zls_signing_key
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Zentriq.LicenseServer.dll"]
