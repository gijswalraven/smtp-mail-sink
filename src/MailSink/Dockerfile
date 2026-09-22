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

# The sink defaults to 587 and 465, but both are privileged and this image runs as a non-root
# user, which may not bind them. So the container listens high and the host publishes whichever
# port clients should see -- "-p 587:2587" for Docker, --ports for a container group. A host that
# can bind privileged ports directly (the Windows service, a VM) should use the 587/465 defaults.
ENV MailSink__StartTlsPorts__0=2587
ENV MailSink__ImplicitTlsPorts__0=2465
EXPOSE 2587 2465

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
