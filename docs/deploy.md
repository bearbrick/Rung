# 部署

两种交付形态。**离线内网优先用单文件**——目标机不需要装 .NET，也不需要联网拉镜像。

## 单文件 + systemd

在有 .NET 10 SDK 和 Node 的机器上打包：

```bash
./scripts/publish.sh linux-x64      # 或 linux-arm64
```

产物在 `artifacts/rung-linux-x64/`：一个 `rung-host` 可执行文件（约 51 MB）、
`wwwroot/`、`appsettings.json`，以及一个 `libSystem.IO.Ports.Native.so`
（将来做 Modbus RTU 串口用）。

拷到目标机：

```bash
sudo useradd --system --home /opt/rung --shell /usr/sbin/nologin rung
sudo mkdir -p /opt/rung /etc/rung /var/lib/rung /var/log/rung
sudo cp -r artifacts/rung-linux-x64/* /opt/rung/
sudo cp artifacts/rung-linux-x64/rung.json.example /etc/rung/rung.json
sudo chown -R rung:rung /opt/rung /var/lib/rung /var/log/rung

sudo cp /opt/rung/rung.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now rung
```

改完配置后 `sudo systemctl restart rung`。日志走 journald：

```bash
sudo journalctl -u rung -f
```

## 容器

```bash
docker build -t rung:0.1.0 .
docker run -d --name rung -p 5580:5580 \
  -v /etc/rung:/etc/rung:ro \
  -v rung-data:/var/lib/rung \
  rung:0.1.0
```

> 这份 Dockerfile **尚未在真实环境构建验证过**（开发机上没有 Docker）。
> 第一次用请留意基础镜像标签和 npm 缓存层是否符合你们的内网源配置。

## 端口与目录约定

| 项 | 值 |
|---|---|
| Web / API 端口 | `5580`（避开 5000/8080/9090 这些高频端口） |
| 安装目录 | `/opt/rung` |
| 配置 | `/etc/rung/rung.json` |
| 数据 | `/var/lib/rung` |
| 服务名 | `rung.service` |
| Redis key 前缀 | `rung:tag:*` / `rung:device:*` |

这套东西一旦各叫各的，排障时会很难受，所以固定下来。

## 健康检查

```bash
/opt/rung/rung-host --healthcheck          # 退出码 0 = 正常
curl -s localhost:5580/api/health
```

探针**只认 HTTP 200，不看 `status` 是 healthy 还是 degraded**。
有设备掉线时网关本身是好的，重启它不但没用，还会把其他正常设备的采集一起中断——
设备级告警交给监控系统，用 `/api/devices` 的 `state` 字段。

## 安全

`rung.service` 里做了 systemd 沙箱收紧（`ProtectSystem=strict`、`NoNewPrivileges`、
限制地址族等），容器镜像里以 uid 10001 的非 root 用户运行。

网关待在产线网里，是 IT/OT 边界上攻击面最靠前的一环，**不要用 root 跑**。
默认端口 5580 是非特权端口，不需要任何 capability。
