# Build from the repo root:  docker build -f src/FundFlow.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FundFlow.Core/FundFlow.Core.csproj src/FundFlow.Core/
COPY src/FundFlow.Worker/FundFlow.Worker.csproj src/FundFlow.Worker/
RUN dotnet restore src/FundFlow.Worker/FundFlow.Worker.csproj
COPY src/ src/
RUN dotnet publish src/FundFlow.Worker/FundFlow.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "FundFlow.Worker.dll"]
