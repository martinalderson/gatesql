# Build libpg_query native lib for the target platform (needed for ARM64)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS libpg-build
RUN apt-get update && apt-get install -y --no-install-recommends build-essential git && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch 17-6.0.0 https://github.com/pganalyze/libpg_query.git /libpg_query
WORKDIR /libpg_query
RUN make build_shared

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DbProxy.slnx .
COPY src/DbProxy/DbProxy.csproj src/DbProxy/
RUN dotnet restore src/DbProxy/DbProxy.csproj

COPY src/ src/
RUN dotnet publish src/DbProxy/DbProxy.csproj -c Release -o /app/publish

# Copy native lib built for this platform (covers ARM64 where NuGet package lacks it)
COPY --from=libpg-build /libpg_query/libpg_query.so /app/publish/libpg_query.so

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN mkdir -p /app/keys /app/logs /app/data

COPY --from=build /app/publish .
COPY docker-config.json /app/config.json

EXPOSE 15432 8080

ENTRYPOINT ["dotnet", "DbProxy.dll", "/app/config.json"]
