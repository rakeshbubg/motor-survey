# Build: docker build -f deploy/Dockerfile -t motor-survey .        (add --build-arg USE_ONNX=true once you have a model)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG USE_ONNX=false
WORKDIR /src
COPY . .
RUN dotnet publish src/MotorSurvey.Web/MotorSurvey.Web.csproj -c Release -o /app -p:UseOnnx=${USE_ONNX}
# Car-check model for the phone (SSD Lite MobileNet v2, ~5 MB, Apache 2.0) served from our own server.
# If the download fails the phone falls back to Google's hosted copy, and if that fails the check is skipped.
RUN d=/app/wwwroot/lib/coco-ssd-lite; base=https://storage.googleapis.com/tfjs-models/savedmodel/ssdlite_mobilenet_v2; mkdir -p $d; \
    (command -v curl >/dev/null || (apt-get update -qq && apt-get install -y -qq curl >/dev/null)) && \
    (curl -fsSL $base/model.json -o $d/model.json && \
     for f in $(grep -o 'group1-shard[0-9]*of[0-9]*' $d/model.json | sort -u); do curl -fsSL $base/$f -o $d/$f; done && \
     echo "car-check model downloaded") || (echo "car-check model not downloaded; phones will use Google's copy" && rm -rf $d)

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 Survey__DataFolder=/data Erp__ExcelPath=/data/erp_customers.xlsx
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "MotorSurvey.Web.dll"]
