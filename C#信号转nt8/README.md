# 信号转发服务 (Signal Forwarder)

一个基于 .NET 8 的信号转发服务，用于连接 TradingView 和 NinjaTrader 8，实现自动化交易和实时数据推送。

## 📋 项目概述

本项目包含两个核心组件：

1. **服务端程序** (`Program.cs`) - 运行在服务器上的 HTTP 服务
   - 接收 TradingView Webhook 信号，自动转发到 NinjaTrader 8 下单
   - 接收 NinjaTrader 8 推送的 Tick 数据

2. **NT8 指标** (`TickWebSocketSender.cs`) - 运行在 NinjaTrader 8 中的自定义指标
   - 实时获取市场 Tick 数据
   - 通过 HTTP POST 将 Tick 数据推送到服务端

## 🚀 功能特性

### 服务端功能

- ✅ **接收 TradingView Webhook 信号**
  - 支持 `buy` 和 `sell` 信号
  - 自动品种映射（支持配置文件）
  - 多账号并发下单
  - 支持市价单和限价单

- ✅ **接收 NT8 Tick 数据**
  - HTTP POST 接口接收实时 Tick 数据
  - 记录日志便于分析
  - 可扩展自定义处理逻辑

- ✅ **配置管理**
  - 品种映射配置（`ticker_mapping.txt`）
  - 账号配置（`account_config.txt`）
  - 配置文件热重载（修改后自动生效）

- ✅ **日志记录**
  - 文件日志（按日期分割）
  - 控制台日志
  - 详细的错误信息

### NT8 指标功能

- ✅ **实时 Tick 数据推送**
  - 通过 `OnMarketData` 方法获取实时数据
  - 自动判断买卖方向（基于 Ask/Bid 价格）
  - HTTP POST 异步推送，不阻塞 NT8

- ✅ **连接管理**
  - 自动初始化 HTTP 客户端
  - 状态检测和错误处理
  - 资源自动清理

## 📦 项目结构

```
.
├── Program.cs                    # 服务端主程序
├── TickWebSocketSender.cs       # NT8 指标（需复制到 NT8 目录）
├── NinjaTraderOrderService.cs    # NT8 订单服务
├── AccountConfig.cs              # 账号配置类
├── FileLoggerProvider.cs         # 文件日志提供者
├── SignalForwarder.csproj       # 项目文件
├── ticker_mapping.txt           # 品种映射配置
├── account_config.txt            # 账号配置
└── README.md                     # 本文档
```

## 🔧 安装和配置

### 前置要求

- .NET 8 SDK（开发环境）或 .NET 8 Runtime（运行环境）
- Windows 操作系统
- NinjaTrader 8 已安装并运行
- NinjaTrader.Client.dll（通常位于 `C:\Program Files\NinjaTrader 8\bin\`）

### 1. 编译服务端

```bash
# 克隆或下载项目
cd C#信号转nt8

# 恢复依赖
dotnet restore

# 编译项目
dotnet build

# 运行（开发环境）
dotnet run
```

### 2. 发布服务端

```bash
# Windows x64 自包含版本（推荐，包含 .NET 运行时）
dotnet publish -c Release -r win-x64 --self-contained

# Windows x64 框架依赖版本（需要安装 .NET 8 运行时）
dotnet publish -c Release -r win-x64
```

发布后的可执行文件位于 `bin/Release/net8.0-windows/win-x64/publish/` 目录。

### 3. 安装 NT8 指标

1. **复制指标文件**
   - 将 `TickWebSocketSender.cs` 复制到：
     ```
     Documents\NinjaTrader 8\bin\Custom\Indicators\
     ```

2. **编译指标**
   - 打开 NinjaTrader 8
   - 菜单栏：`Tools` -> `Compile`
   - 确保编译成功，没有错误

3. **添加到图表**
   - 右键点击图表 -> `Indicators`
   - 找到 `TickWebSocketSender` 并添加
   - 配置 Webhook URL（默认：`http://localhost:8500/tick`）

### 4. 配置 NinjaTrader 8

1. **启用 ATI 接口**
   - 打开 NinjaTrader 8
   - `Tools` -> `Options` -> `Automated Trading Interface (ATI)`
   - 勾选启用 ATI

2. **验证连接**
   - 确保 NinjaTrader 8 正在运行
   - 服务端启动时会自动尝试连接

## ⚙️ 配置文件说明

### 品种映射配置 (`ticker_mapping.txt`)

用于将 TradingView 的品种名称映射到 NinjaTrader 8 的品种名称。

**格式：**
```
# 品种映射配置
# 格式：源品种=目标品种
# 支持注释行（以 # 开头）

GC=MGC      # 黄金：GC -> MGC
CL=MCL      # 原油：CL -> MCL
ES=MES      # 标普500：ES -> MES
```

**说明：**
- 每行一个映射规则
- 格式：`源品种=目标品种`
- 支持注释（以 `#` 开头）
- 空行会被忽略
- 修改后自动重载（延迟约 2 秒）

### 账号配置 (`account_config.txt`)

配置多个 NinjaTrader 8 账号，每个账号可以独立设置手数和订单类型。

**格式：**
```
# 账号配置
# 格式：账号=手数,订单类型,是否启用
# 订单类型：Market（市价）或 Limit（限价）
# 是否启用：true 或 false（可选，默认为 true）

Account1=1,Market,true
Account2=2,Limit,true
Account3=1,Market,false
```

**配置项说明：**
- **账号**：NinjaTrader 8 账号名称（必须与 NT8 中的账号名称完全一致）
- **手数**：每次交易的手数（整数）
- **订单类型**：`Market`（市价）或 `Limit`（限价）
- **是否启用**：`true`（启用）或 `false`（禁用），可选，默认为 `true`

**示例：**
- `Account1=1,Market,true` - 账号 Account1，每次交易 1 手，使用市价单，已启用
- `Account2=2,Limit,true` - 账号 Account2，每次交易 2 手，使用限价单，已启用
- `Account3=1,Market,false` - 账号 Account3，已禁用，不会发送订单

**注意：**
- 修改配置文件后会自动重载（延迟约 2 秒）
- 如果账号配置发生变化，订单服务会自动更新
- 如果之前没有配置账号，添加配置后会自动初始化订单服务

## 🌐 API 接口

### 1. 接收 TradingView 信号

**端点：** `POST /webhook`

**请求格式：**
```json
{
  "ticker": "GC",
  "action": "buy",
  "price": 2050.50,
  "interval": 15
}
```

**字段说明：**
- `ticker`：品种名称（会被映射到目标品种）
- `action`：交易动作，`buy`（买入）或 `sell`（卖出）
- `price`：价格（可选，限价单时使用）
- `interval`：周期（可选，单位：分钟）

**响应格式：**
```json
{
  "status": "success",
  "message": "Signal received"
}
```

### 2. 接收 NT8 Tick 数据

**端点：** `POST /tick`

**请求格式（由 NT8 指标发送）：**
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
- `Instrument`：合约名称
- `Price`：成交价格
- `Volume`：成交量
- `Time`：时间戳（格式：`yyyy-MM-dd HH:mm:ss.fff`）
- `MarketDataType`：市场数据类型（通常为 `"Last"`）
- `Direction`：买卖方向，`"buy"`、`"sell"` 或 `"unknown"`

**响应格式：**
```json
{
  "status": "success",
  "message": "Tick received"
}
```

## 🔄 工作流程

### 信号转发流程

```
TradingView 
    ↓ (Webhook POST)
服务端 (/webhook)
    ↓ (解析信号)
品种映射
    ↓ (转换为 NT8 品种)
账号配置
    ↓ (多账号并发)
NinjaTrader 8 (ATI)
    ↓
订单执行
```

### Tick 数据推送流程

```
NinjaTrader 8
    ↓ (OnMarketData 事件)
TickWebSocketSender 指标
    ↓ (HTTP POST)
服务端 (/tick)
    ↓ (记录日志)
可扩展处理逻辑
```

## 📝 使用示例

### 1. TradingView Webhook 配置

在 TradingView 的 Alert 设置中，使用以下 Webhook URL：

```
http://你的服务器IP:8500/webhook
```

Alert Message 示例：
```json
{
  "ticker": "{{ticker}}",
  "action": "{{strategy.order.action}}",
  "price": "{{close}}",
  "interval": "{{interval}}"
}
```

### 2. NT8 指标配置

1. 在图表上添加 `TickWebSocketSender` 指标
2. 配置 Webhook URL（如果服务端不在本机，修改为服务器地址）：
   ```
   http://你的服务器IP:8500/tick
   ```
3. 指标会自动开始推送 Tick 数据

## 📊 日志说明

### 日志位置

- 日志目录：`logs/`
- 日志文件：`webhook_YYYYMMDD.log`（按日期分割）
- 同时输出到控制台

### 日志级别

- **Information**：正常操作信息（信号接收、订单发送、Tick 接收等）
- **Warning**：警告信息（配置错误、连接失败等）
- **Error**：错误信息（异常详情）

### 查看日志

```bash
# Windows PowerShell
Get-Content logs\webhook_20240129.log -Tail 50

# 实时监控
Get-Content logs\webhook_20240129.log -Wait -Tail 20
```

## 🐛 故障排查

### 服务端无法启动

1. **检查端口占用**
   ```bash
   netstat -ano | findstr :8500
   ```
   如果端口被占用，可以修改 `Program.cs` 中的端口号

2. **检查 .NET 运行时**
   ```bash
   dotnet --version
   ```
   确保已安装 .NET 8

### 无法连接到 NinjaTrader 8

1. **检查 NinjaTrader 8 是否运行**
   - 确保 NT8 已打开

2. **检查 ATI 是否启用**
   - `Tools` -> `Options` -> `Automated Trading Interface (ATI)` 必须勾选

3. **检查账号配置**
   - 确保 `account_config.txt` 中的账号名称与 NT8 中的完全一致
   - 查看服务端日志中的错误信息

### NT8 指标无法推送数据

1. **检查服务端是否运行**
   - 确保服务端正在运行并监听 8500 端口

2. **检查 Webhook URL 配置**
   - 在指标参数中确认 URL 正确
   - 如果服务端不在本机，使用服务器 IP 地址

3. **检查网络连接**
   - 确保 NT8 可以访问服务端地址
   - 检查防火墙设置

4. **查看 NT8 Output 窗口**
   - 在 NT8 中打开 `Tools` -> `Output`
   - 查看指标输出的日志信息

### 订单未执行

1. **检查账号配置**
   - 确认账号已启用（`Enabled=true`）
   - 确认账号名称正确

2. **检查信号格式**
   - 确认 `action` 为 `buy` 或 `sell`
   - 查看服务端日志确认信号已接收

3. **检查品种映射**
   - 确认 TradingView 的品种已正确映射到 NT8 品种
   - 查看服务端日志中的映射信息

## 🔒 安全注意事项

1. **网络安全**
   - 建议在生产环境中使用 HTTPS
   - 配置防火墙规则，限制访问来源
   - 考虑添加身份验证

2. **账号安全**
   - 妥善保管 `account_config.txt` 文件
   - 不要将配置文件提交到公共代码仓库

3. **日志安全**
   - 日志文件可能包含敏感信息，注意保护

## 📄 许可证

本项目仅供学习和研究使用。使用本软件进行实盘交易的风险由使用者自行承担。

## 🤝 贡献

欢迎提交 Issue 和 Pull Request。

## 📞 支持

如有问题，请查看：
- 项目 Issues
- NinjaTrader 8 官方文档
- .NET 8 官方文档

---

**注意：** 本软件仅供学习和研究使用。使用本软件进行实盘交易前，请充分测试并了解相关风险。
