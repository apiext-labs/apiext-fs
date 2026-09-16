# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Apiext.Fs/Apiext.Fs.csproj", "Apiext.Fs/"]
RUN dotnet restore "./Apiext.Fs/Apiext.Fs.csproj"
COPY . .
WORKDIR "/src/Apiext.Fs"
RUN dotnet build "./Apiext.Fs.csproj" -c $BUILD_CONFIGURATION -o /app/build
RUN mkdir -p /app/container-data

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Apiext.Fs.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
EXPOSE 8080
EXPOSE 8081

LABEL name="Apiext.Fs" \
      summary="Apiext.Fs is a self-hosted, S3-compatible file storage server built with ASP.NET Core that stores objects on the local filesystem and contains user and web management."

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    StoreLocation=/datastore \
    ConnectionStrings__DefaultConnection="Data Source=/datastore/apiext-fs.db" \
    DataProtection__KeysPath=/datastore/.data-protection \
    UseStaticAccount=true \
    StaticUser="" \
    StaticPassword=""

WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build --chown=$APP_UID:$APP_UID /app/container-data/ /datastore/
VOLUME ["/datastore"]
USER $APP_UID
ENTRYPOINT ["dotnet", "Apiext.Fs.dll"]
