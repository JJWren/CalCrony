# Build context is the repo root (see docker-compose.yml) so project references resolve.

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG VERSION=0.0.0-local
WORKDIR /source

COPY src/CalCrony.Contracts/CalCrony.Contracts.csproj src/CalCrony.Contracts/
COPY src/CalCrony.Web/CalCrony.Web.csproj src/CalCrony.Web/
RUN dotnet restore src/CalCrony.Web/CalCrony.Web.csproj

COPY src/CalCrony.Contracts/ src/CalCrony.Contracts/
COPY src/CalCrony.Web/ src/CalCrony.Web/
RUN dotnet publish src/CalCrony.Web/CalCrony.Web.csproj -c Release -o /app/publish -p:Version=$VERSION

# ---------- Runtime (static files via nginx) ----------
FROM nginx:alpine AS runtime

COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
COPY src/CalCrony.Web/nginx.conf /etc/nginx/conf.d/default.conf
# .inc extension keeps it out of nginx's conf.d/*.conf auto-include; nginx.conf includes it.
COPY src/CalCrony.Web/calcrony-security-headers.inc /etc/nginx/conf.d/calcrony-security-headers.inc
# nginx's stock entrypoint runs every script in /docker-entrypoint.d before serving.
COPY src/CalCrony.Web/docker-entrypoint.d/40-calcrony-appsettings.sh /docker-entrypoint.d/40-calcrony-appsettings.sh
COPY src/CalCrony.Web/docker-entrypoint.d/50-calcrony-csp.sh /docker-entrypoint.d/50-calcrony-csp.sh

RUN rm -f /usr/share/nginx/html/appsettings.Development.json \
 && chmod +x /docker-entrypoint.d/40-calcrony-appsettings.sh /docker-entrypoint.d/50-calcrony-csp.sh

EXPOSE 80
