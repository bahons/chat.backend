# Build with the dotnet publish output directory as the context.
ARG ASPNET_RUNTIME=10.0.14
FROM mcr.microsoft.com/dotnet/aspnet:${ASPNET_RUNTIME}
WORKDIR /app
COPY --chown=app:app . .
USER app
ENTRYPOINT ["dotnet", "chat.Api.dll"]
