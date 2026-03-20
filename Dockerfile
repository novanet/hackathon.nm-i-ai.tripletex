FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY publish/ .
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "TripletexAgent.dll"]
