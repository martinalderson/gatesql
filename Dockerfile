FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DbProxy.slnx .
COPY src/DbProxy/DbProxy.csproj src/DbProxy/
RUN dotnet restore src/DbProxy/DbProxy.csproj

COPY src/ src/
RUN dotnet publish src/DbProxy/DbProxy.csproj -c Release -o /app/publish

# Pre-built libpg_query for ARM64 (NuGet package only ships linux-x64)
COPY native/libpg_query-linux-arm64.so /app/publish/native/libpg_query-linux-arm64.so

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN mkdir -p /app/keys /app/logs /app/data

COPY --from=build /app/publish .

# On ARM64, place the native lib where .NET can find it
RUN if [ "$(uname -m)" = "aarch64" ]; then cp /app/native/libpg_query-linux-arm64.so /app/libpg_query.so; fi
RUN rm -rf /app/native

COPY docker-config.json /app/config.json

EXPOSE 15432 8080

ENTRYPOINT ["dotnet", "DbProxy.dll", "/app/config.json"]
