FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ ./backend/
RUN dotnet restore backend/PpkiSmartFormatter.slnx
RUN dotnet publish backend/services/Ppki.Api/Ppki.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
COPY rules/ /app/rules/
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ppki.Api.dll"]
