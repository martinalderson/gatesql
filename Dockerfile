FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DbProxy.slnx .
COPY src/DbProxy/DbProxy.csproj src/DbProxy/
RUN dotnet restore src/DbProxy/DbProxy.csproj

COPY src/ src/
RUN dotnet publish src/DbProxy/DbProxy.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN mkdir -p /app/keys /app/logs

COPY --from=build /app/publish .
COPY docker-config.json /app/config.json

EXPOSE 15432 8080

ENTRYPOINT ["dotnet", "DbProxy.dll", "/app/config.json"]
