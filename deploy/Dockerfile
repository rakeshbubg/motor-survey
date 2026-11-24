# Build: docker build -f deploy/Dockerfile -t motor-survey .        (add --build-arg USE_ONNX=true once you have a model)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG USE_ONNX=false
WORKDIR /src
COPY . .
RUN dotnet publish src/MotorSurvey.Web/MotorSurvey.Web.csproj -c Release -o /app -p:UseOnnx=${USE_ONNX}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 Survey__DataFolder=/data Erp__ExcelPath=/data/erp_customers.xlsx
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "MotorSurvey.Web.dll"]
