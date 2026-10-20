# ---------- Etapa 1: build / publish ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restaurar primero (aprovecha la caché de capas si solo cambia el código)
COPY ApiGateway.csproj ./
RUN dotnet restore ApiGateway.csproj

COPY . ./
RUN dotnet publish ApiGateway.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- Etapa 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish ./

EXPOSE 8080

# Usuario no-root incluido en las imágenes oficiales de .NET 8
USER $APP_UID

ENTRYPOINT ["dotnet", "ApiGateway.dll"]
