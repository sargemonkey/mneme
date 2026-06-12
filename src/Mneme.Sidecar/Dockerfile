FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ./src ./src
COPY ./Directory.Build.props ./
RUN dotnet publish src/Mneme.Sidecar/Mneme.Sidecar.csproj \
    -c Release -o /app --self-contained false /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /data
ENV MNEME_SQLITE_PATH=/data/mneme.db \
    MNEME_HTTP_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -q -O- http://127.0.0.1:8080/readyz || exit 1
ENTRYPOINT ["dotnet", "Mneme.Sidecar.dll"]
