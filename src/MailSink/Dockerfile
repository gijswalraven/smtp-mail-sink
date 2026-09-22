FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MailSink.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

# Mail lands here; mount a volume so it survives container restarts.
ENV MailSink__MailDirectory=/mail
VOLUME /mail

# The standard submission ports. Both are privileged and this image runs as a non-root user, but
# container runtimes set net.ipv4.ip_unprivileged_port_start=0, so binding them needs no extra
# capability -- verified against this image. A runtime that does not do that will fail to bind;
# point MailSink__StartTlsPorts__0 and MailSink__ImplicitTlsPorts__0 at high ports if so.
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
