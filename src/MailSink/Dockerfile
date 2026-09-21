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
EXPOSE 1025

# The listener takes unauthenticated input from the network and parses hostile MIME, so it does
# not run as root. Port 1025 is unprivileged, so nothing here needs the extra rights. APP_UID is
# defined by the base image (1654); /mail must be writable by it.
RUN mkdir -p /mail && chown $APP_UID:$APP_UID /mail /app
USER $APP_UID

ENTRYPOINT ["dotnet", "MailSink.dll"]
