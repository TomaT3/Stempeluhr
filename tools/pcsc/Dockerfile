# Build separately for review: docker build -f tools/pcsc/Dockerfile --output type=local,dest=artifacts/pcsc .
FROM --platform=linux/arm64 debian:trixie-slim@sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a AS pcsc-build
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential debhelper dh-exec dh-runit flex libpolkit-gobject-1-dev \
    libsystemd-dev libudev-dev meson pkgconf systemd-dev curl ca-certificates python3 \
    && rm -rf /var/lib/apt/lists/*
COPY tools/pcsc/build-packages.sh /build-packages.sh
RUN bash /build-packages.sh /out
FROM scratch
COPY --from=pcsc-build /out/ /
