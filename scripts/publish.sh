#!/usr/bin/env bash
# 打出可直接拷到目标机的发布包。
#
#   ./scripts/publish.sh                # linux-x64
#   ./scripts/publish.sh linux-arm64    # 树莓派、边缘盒子
#
# 产物是单个可执行文件加 wwwroot，目标机不需要装 .NET——
# 离线内网交付时这一点很关键。
set -euo pipefail

RID="${1:-linux-x64}"
OUT="artifacts/rung-${RID}"

rm -rf "$OUT"
dotnet publish src/Rung.Host \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -o "$OUT"

cp deploy/rung.service "$OUT/"
cp samples/gateway.json "$OUT/rung.json.example"

echo
echo "发布包：$OUT"
du -sh "$OUT"
