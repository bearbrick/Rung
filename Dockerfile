# 前端单独一层：改后端代码时不必重跑 npm，构建快很多
FROM node:24-alpine AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.slnx Directory.*.props global.json ./
COPY src/ src/
# 前端已在上一层构建好，这里跳过，否则镜像里还得装 Node
COPY --from=web /src/Rung.Host/wwwroot/ src/Rung.Host/wwwroot/
RUN dotnet publish src/Rung.Host -c Release -o /app -p:SkipWebUi=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /opt/rung

# 不以 root 运行：网关待在产线网里，是攻击面最靠前的一环
RUN useradd --system --create-home --uid 10001 rung \
 && mkdir -p /etc/rung /var/lib/rung \
 && chown -R rung:rung /opt/rung /var/lib/rung

COPY --from=build --chown=rung:rung /app ./
USER rung

EXPOSE 5580
ENV ConfigPath=/etc/rung/rung.json

# 健康检查直接用网关自己的接口：有设备掉线时它会返回 degraded，
# 但进程本身是好的，所以只认 HTTP 200
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
  CMD ["/opt/rung/rung-host", "--healthcheck"]

ENTRYPOINT ["/opt/rung/rung-host"]
