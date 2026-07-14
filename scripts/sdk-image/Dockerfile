# .NET SDK + docker CLI: lets Aspire's orchestrator (DCP) and Testcontainers manage
# sibling containers through the mounted host Docker socket when no local SDK exists.
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN ARCH="$(uname -m)" \
 && curl -fsSL "https://download.docker.com/linux/static/stable/${ARCH}/docker-27.5.1.tgz" \
    | tar -xz --strip-components=1 -C /usr/local/bin docker/docker \
 && docker --version
