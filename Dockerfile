# ── Build ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/ ./src/
RUN dotnet publish src/FileBeam.csproj \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o /out

# ── Runtime ─────────────────────────────────────────────────────────────────
# runtime-deps is enough for a self-contained single-file binary
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /srv
COPY --from=build /out/filebeam .
RUN chmod +x filebeam && mkdir -p files drop

EXPOSE 8080

# Declare persistent directories as volumes.
# When no -v flag is given, Docker creates anonymous managed volumes here
# so files survive container restarts (but not `docker rm`).
# For true host persistence, mount directories: -v /path/on/host:/srv/share (or something similar)
VOLUME ["/srv/share/download", "/srv/share/upload"]

ENTRYPOINT ["./filebeam", "--download", "/srv/share/download", "--port", "8080"]
