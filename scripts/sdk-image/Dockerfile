# .NET SDK + docker CLI: lets Aspire's orchestrator (DCP) and Testcontainers manage
# sibling containers through the mounted host Docker socket when no local SDK exists.
#
# The base image is pinned by the digest of its multi-arch index, so a rebuild runs exactly the
# image that was reviewed. The tag stays next to the digest for Dependabot (docker ecosystem),
# which moves the two together when the 10.0 line is republished.
#   tag: mcr.microsoft.com/dotnet/sdk:10.0 — at this digest SDK 10.0.401, runtime 10.0.12
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317

# The docker CLI comes from Docker's static-binary channel, which publishes no checksums, so the
# SHA-256 of each architecture's tarball is pinned here and checked before anything is unpacked.
# Bumping DOCKER_CLI_VERSION means re-pinning both hashes:
#   curl -fsSL https://download.docker.com/linux/static/stable/<x86_64|aarch64>/docker-<version>.tgz | sha256sum
ARG DOCKER_CLI_VERSION=27.5.1
ARG DOCKER_CLI_SHA256_X86_64=4f798b3ee1e0140eab5bf30b0edc4e84f4cdb53255a429dc3bbae9524845d640
ARG DOCKER_CLI_SHA256_AARCH64=e6b53725a73763ab3f988c73f8772eaed429754c1a579db5ff11f21990fd1817
RUN set -eu \
 && arch="$(uname -m)" \
 && case "$arch" in \
      x86_64) sha256="$DOCKER_CLI_SHA256_X86_64" ;; \
      aarch64) sha256="$DOCKER_CLI_SHA256_AARCH64" ;; \
      *) echo "No pinned docker CLI checksum for architecture '$arch'." >&2; exit 1 ;; \
    esac \
 && curl -fsSL -o /tmp/docker.tgz "https://download.docker.com/linux/static/stable/${arch}/docker-${DOCKER_CLI_VERSION}.tgz" \
 && echo "${sha256}  /tmp/docker.tgz" | sha256sum -c - \
 && tar -xzf /tmp/docker.tgz --strip-components=1 -C /usr/local/bin docker/docker \
 && rm /tmp/docker.tgz \
 && docker --version
