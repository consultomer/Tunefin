# Builds the plugin and installs it into Jellyfin's plugins folder (mounted at /plugins).
# Used as a one-shot compose service, so no local .NET SDK or build script is needed.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Jellyfin.Plugin.YouTubeMusic.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out --no-restore

FROM alpine:3.20
COPY --from=build /out/Jellyfin.Plugin.YouTubeMusic.dll /plugin/
COPY meta.json /plugin/
COPY Resources/logo.png /plugin/logo.png
COPY install/ /install/
ENV PLUGIN_DIR_NAME=YouTubeMusic_1.0.0.0 \
    SIDECAR_URL=http://ytm-sidecar:8787 \
    LIBRARY_ROOT_PATH=/media/YouTube
CMD ["/bin/sh", "/install/install.sh"]
