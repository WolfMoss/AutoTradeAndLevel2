# NinjaTrader 8 Tick 数据推送指标使用说明

## 📋 功能说明

`TickWebSocketSender` 是一个 NinjaTrader 8 自定义指标，用于：

- 通过 `OnMarketData` 方法实时获取市场 Tick 数据
- 通过 HTTP POST 将 Tick 数据推送到外部服务端
- 自动判断买卖方向（基于 Ask/Bid 价格关系）
- 异步推送，不阻塞 NinjaTrader 8 的正常运行

## 🔧 安装步骤

### 1. 复制指标文件

将 `TickWebSocketSender.cs` 文件复制到 NinjaTrader 8 的指标目录：

```
Documents\NinjaTrader 8\bin\Custom\Indicators\
```

### 2. 编译指标

1. 打开 NinjaTrader 8
2. 在菜单栏选择：`Tools` -> `Compile`
3. 确保编译成功，没有错误

**注意：** 如果编译时出现警告 `CS1704`（程序集重复引用），这是 NinjaTrader 8 编译环境的已知问题，不影响功能。代码中已使用 `#pragma warning disable` 禁用此警告。

### 3. 检查编译结果

编译成功后，你应该能在指标列表中找到 `TickWebSocketSender`。

如果找不到：

1. **检查命名空间**：确保文件在正确的目录，且命名空间为 `NinjaTrader.NinjaScript.Indicators`
2. **重新编译**：在 NinjaTrader 8 中执行 `Tools` -> `Compile`，确保没有错误
3. **重启 NinjaTrader 8**：编译后可能需要重启才能看到新指标
4. **检查文件位置**：确保文件在 `Documents\NinjaTrader 8\bin\Custom\Indicators\` 目录
5. **检查类名**：确保类名是 `TickWebSocketSender` 且继承自 `Indicator`

## 📖 使用方法

### 1. 在图表上添加指标

1. 右键点击图表 -> `Indicators`
2. 在左侧列表中找到 `TickWebSocketSender`
3. 点击 `Add` 添加到图表

### 2. 配置参数

在指标参数面板中配置：

- **WebhookUrl**：服务端接收 Tick 数据的 URL
  - 默认：`http://localhost:8500/tick`
  - 如果服务端运行在其他机器，使用：`http://服务器IP:8500/tick`
  - 如果使用 HTTPS，使用：`https://服务器地址:8500/tick`

### 3. 启动服务端

确保 SignalForwarder 服务端正在运行，并且监听在配置的端口（默认 8500）。

### 4. 验证连接

- 指标会在 `State.Realtime` 状态下自动初始化 HTTP 客户端
- 查看 NinjaTrader 8 的 Output 窗口（`Tools` -> `Output`），可以看到连接日志：
  - `"指标状态: Realtime，检查 HTTP 客户端状态"`
  - `"HTTP 客户端已初始化，Webhook URL: http://localhost:8500/tick"`

### 5. 验证数据推送

- 当收到 Tick 数据时，指标会自动发送 HTTP POST 请求
- 查看服务端日志，应该能看到接收到的 Tick 数据
- 如果发送失败，NT8 Output 窗口会显示错误信息

## 📊 Tick 数据格式

指标发送的 JSON 格式：

```json
{
  "Type": "Tick",
  "Instrument": "ES 03-26",
  "Price": 4250.25,
  "Volume": 100,
  "Time": "2024-01-15 10:30:45.123",
  "MarketDataType": "Last",
  "Direction": "buy"
}
```

**字段说明：**
- `Type`：数据类型，固定为 `"Tick"`
- `Instrument`：合约全名（例如：`"ES 03-26"`）
- `Price`：成交价格（浮点数）
- `Volume`：成交量（整数）
- `Time`：时间戳（格式：`yyyy-MM-dd HH:mm:ss.fff`）
- `MarketDataType`：市场数据类型（通常为 `"Last"`）
- `Direction`：买卖方向
  - `"buy"`：买入（成交价更接近 Ask 价格）
  - `"sell"`：卖出（成交价更接近 Bid 价格）
  - `"unknown"`：无法判断（无法获取 Ask/Bid 价格时）

## 🔍 买卖方向判断逻辑

指标使用以下逻辑判断买卖方向：

1. **优先方法**：使用 Ask/Bid 价格关系
   - 获取当前的 Ask 和 Bid 价格
   - 计算成交价与 Ask、Bid 的距离
   - 如果成交价在中价以上，判断为 `buy`
   - 如果成交价在中价以下，判断为 `sell`

2. **备用方法**：如果无法获取 Ask/Bid 价格，则返回 `"unknown"`

## ⚙️ 指标状态说明

指标会在不同的 NinjaTrader 8 状态下执行不同操作：

- **State.SetDefaults**：设置指标默认属性
- **State.Configure**：配置阶段
- **State.DataLoaded**：数据加载完成，初始化 HTTP 客户端
- **State.Historical**：历史数据状态，跳过 Tick 推送（只处理实时数据）
- **State.Realtime**：实时数据状态，确保 HTTP 客户端已初始化
- **State.Terminated**：指标终止，清理 HTTP 客户端资源

## ⚠️ 注意事项

1. **数据频率**
   - 每个 `Last` 类型的 Tick 都会发送
   - 如果 Tick 数据非常频繁，可能会产生大量网络流量
   - 建议监控网络带宽使用情况

2. **性能影响**
   - 发送 Tick 数据会消耗一定的 CPU 和网络资源
   - 如果图表上有多个指标实例，资源消耗会叠加
   - 建议只在需要推送数据的图表上添加指标

3. **连接稳定性**
   - HTTP 请求是异步发送的，不会阻塞 NT8
   - 如果网络连接失败，会在 NT8 Output 窗口显示错误信息
   - 指标不会自动重连，但每次收到新 Tick 时会尝试发送

4. **防火墙设置**
   - 确保防火墙允许 NinjaTrader 8 访问服务端端口
   - 如果服务端在其他机器，确保网络连通

5. **服务端可用性**
   - 如果服务端未运行或不可访问，HTTP 请求会失败
   - 失败信息会记录在 NT8 Output 窗口，但不会影响 NT8 的正常运行

## 🐛 故障排查

### 无法连接服务端

**症状：** NT8 Output 窗口显示连接错误

**排查步骤：**
1. 检查 SignalForwarder 服务端是否运行
2. 检查服务端端口是否正确（默认 8500）
3. 检查 WebhookUrl 配置是否正确
4. 检查防火墙设置
5. 如果服务端在其他机器，检查网络连通性：
   ```bash
   ping 服务器IP
   telnet 服务器IP 8500
   ```

### 连接后立即失败

**症状：** HTTP 客户端初始化成功，但发送数据时失败

**排查步骤：**
1. 检查 Webhook URL 格式是否正确（必须以 `http://` 或 `https://` 开头）
2. 检查服务端 `/tick` 接口是否正常
3. 查看服务端日志确认是否收到请求
4. 检查网络连接稳定性

### 没有收到 Tick 数据

**症状：** 服务端没有收到任何 Tick 数据

**排查步骤：**
1. 确保图表上已加载数据
2. 确保合约有实时数据源（不是历史数据）
3. 检查指标是否已启用（在指标列表中确认）
4. 确认指标状态为 `Realtime`（不是 `Historical`）
5. 查看 NT8 Output 窗口，确认指标状态信息
6. 查看服务端日志确认是否收到数据

### 编译错误

#### 错误：找不到类型或命名空间名 "HttpClient"

**解决方案：**
- 确保 NinjaTrader 8 使用的是 .NET Framework 4.7.2 或更高版本
- `System.Net.Http` 应该已经包含在 .NET Framework 中

#### 错误：找不到类型或命名空间名 "System.Threading.Tasks"

**解决方案：**
- 确保代码文件顶部有正确的 `using` 语句
- 检查 NinjaTrader 8 版本是否支持异步编程

#### 警告：CS1704 - 程序集重复引用

**解决方案：**
- 这是 NinjaTrader 8 编译环境的已知问题，不影响功能
- 代码中已使用 `#pragma warning disable CS1704` 禁用此警告
- 可以忽略此警告

## 📝 代码说明

### 关键方法

- **`OnMarketData`**：当收到市场数据时调用，只处理 `Last` 类型的 Tick
- **`SendTickData`**：构建 JSON 数据并通过 HTTP POST 发送
- **`InitializeHttpClient`**：初始化 HTTP 客户端（线程安全）
- **`DisposeHttpClient`**：清理 HTTP 客户端资源
- **`EscapeJsonString`**：转义 JSON 字符串中的特殊字符

### 线程安全

- 使用 `lock` 确保 HTTP 客户端的线程安全访问
- HTTP 请求在 `Task.Run` 中异步执行，不阻塞主线程

## 🔗 相关文档

- [主项目 README](README.md) - 服务端使用说明
- [NinjaTrader 8 官方文档](https://ninjatrader.com/support/helpGuides/nt8/)

---

**注意：** 本指标仅供学习和研究使用。使用本指标进行实盘交易前，请充分测试并了解相关风险。
