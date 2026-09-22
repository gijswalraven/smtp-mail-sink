FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# The lock file comes along so the restore layer is pinned to the same transitive graph CI
# verified, rather than whatever nuget.org resolves on the day the image happens to build.
COPY MailSink.csproj packages.lock.json ./
RUN dotnet restore --locked-mode
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

# Mail lands here; mount a volume so it survives container restarts.
ENV MailSink__MailDirectory=/mail
VOLUME /mail

# The standard submission ports, which the sink defaults to. Both are privileged and this image
# runs as a non-root user; whether that works depends on the runtime. Docker sets
# net.ipv4.ip_unprivileged_port_start=0 so it binds them fine, but Azure Container Instances does
# not, and there the bind fails with EACCES -- so deploy.ps1 moves them above 1024. Both verified.
EXPOSE 587 465

# The plain-text port, opened only in the Development environment.
EXPOSE 1025

# The health endpoint an orchestrator probes. Deliberately not published alongside the SMTP
# ports: the probe runs against the container, senders never reach it.
EXPOSE 8080

# The listener takes input from the network and parses hostile MIME, so it does not run as root.
# APP_UID is defined by the base image (1654); /mail must be writable by it.
RUN mkdir -p /mail && chown $APP_UID:$APP_UID /mail /app
USER $APP_UID

ENTRYPOINT ["dotnet", "MailSink.dll"]
