# 第三方声明

Rung 包含或衍生自以下第三方作品。

## IoTClient

- 仓库：https://github.com/zhaopeiym/IoTClient
- 版权：Copyright (c) 2019 农码一生
- 许可证：MIT
- 使用方式：**源码移植**。S7 协议的报文结构与地址解析逻辑参考其实现后重写。
  详见 [`third_party/IoTClient/README.md`](third_party/IoTClient/README.md)，
  许可证原文见 [`third_party/IoTClient/LICENSE`](third_party/IoTClient/LICENSE)。

## Snap7 / Sharp7

- 仓库：https://github.com/SCADACore/Sharp7 · http://snap7.sourceforge.net
- 使用方式：**仅作为行为参考**，未使用任何代码。
  PDU 容量的计算方式（读 `PDU - 18`、写保守取 `PDU - 35`）与其保持一致，
  因为这两个数字经过了十余年现场验证。
- 注意：Snap7 采用 LGPLv3，Sharp7 同源。**其代码不得进入本仓库**——
  Rung 是 MIT，链接 LGPL 代码会带来许可证兼容问题。

## FluentModbus

- 仓库：https://github.com/Apollo3zehn/FluentModbus
- 许可证：MIT
- 使用方式：NuGet 包引用。Modbus 不做移植——FluentModbus 原生异步、维护活跃，
  且自带 Modbus Server 实现，可用于无真机的集成测试。

---

## 引入新依赖前的检查清单

1. 许可证是否与 MIT 兼容？GPL / LGPL / AGPL **一律不可**
2. 是否为商业授权？`HslCommunication` 是国内工业通讯领域最常被误用的一个，它**不是免费的**
3. 在本文件登记，并在 `Directory.Packages.props` 中集中管理版本
