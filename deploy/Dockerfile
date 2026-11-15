# Build with the dotnet publish output directory as the context.
ARG ASPNET_RUNTIME=10.0.14
FROM mcr.microsoft.com/dotnet/aspnet:${ASPNET_RUNTIME}
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --chown=app:app . .
USER app
ENTRYPOINT ["dotnet", "chat.Api.dll"]
