FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source
COPY src/CalCrony.Contracts/CalCrony.Contracts.csproj src/CalCrony.Contracts/
COPY src/CalCrony.Bot/CalCrony.Bot.csproj src/CalCrony.Bot/
RUN dotnet restore src/CalCrony.Bot/CalCrony.Bot.csproj
COPY src/ src/
RUN dotnet publish src/CalCrony.Bot/CalCrony.Bot.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "CalCrony.Bot.dll"]
