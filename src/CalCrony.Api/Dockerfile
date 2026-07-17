FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source
COPY src/CalCrony.Contracts/CalCrony.Contracts.csproj src/CalCrony.Contracts/
COPY src/CalCrony.Api/CalCrony.Api.csproj src/CalCrony.Api/
RUN dotnet restore src/CalCrony.Api/CalCrony.Api.csproj
COPY src/ src/
RUN dotnet publish src/CalCrony.Api/CalCrony.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "CalCrony.Api.dll"]
