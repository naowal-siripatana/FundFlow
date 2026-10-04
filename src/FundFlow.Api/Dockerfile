# Build from the repo root:  docker build -f src/FundFlow.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FundFlow.Core/FundFlow.Core.csproj src/FundFlow.Core/
COPY src/FundFlow.Api/FundFlow.Api.csproj src/FundFlow.Api/
RUN dotnet restore src/FundFlow.Api/FundFlow.Api.csproj
COPY src/ src/
RUN dotnet publish src/FundFlow.Api/FundFlow.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FundFlow.Api.dll"]
