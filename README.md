# ATAS 订单流数据导出指标

## 简介

这是一个用于 ATAS 交易平台的自定义指标，可以将订单流K线数据导出到CSV文件。

## 功能特性

### 导出的数据包括：

1. **基础K线数据**
   - 时间 (Time, LastTime)
   - OHLCV (开盘价、最高价、最低价、收盘价、成交量)

2. **订单流数据**
   - Bid 成交量 (主动卖出)
   - Ask 成交量 (主动买入)
   - Delta (Ask - Bid，成交量差)
   - Betweens (中间价成交量)
   - MaxDelta / MinDelta (K线周期内的Delta极值)

3. **持仓量数据**
   - OI (持仓量)
   - MaxOI / MinOI (持仓量极值)

4. **POC数据** (可选)
   - POC价格 (成交量最大的价格)
   - POC成交量

5. **Value Area数据** (可选)
   - VAH (价值区高点)
   - VAL (价值区低点)
   - VWAP (成交量加权平均价)

6. **Footprint详情** (可选)
   - 每个价格层级的Bid/Ask/Volume/Delta

## 安装步骤

### 前提条件

1. 安装 [ATAS Platform](https://atas.net/Setup/ATASPlatform.exe) 到默认路径 `C:\Program Files (x86)\ATAS Platform\`
2. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
3. 安装 Visual Studio 2022 或 JetBrains Rider

### 编译步骤

1. 克隆或下载本项目

2. 使用Visual Studio打开 `ATASOrderFlowExporter.csproj`

3. 如果ATAS安装在非默认路径，请修改 `.csproj` 文件中的DLL引用路径

4. 编译项目：
   ```bash
   dotnet build -c Release
   ```

5. 将生成的 `ATASOrderFlowExporter.dll` 复制到ATAS指标目录：
   ```
   %LOCALAPPDATA%\ATAS\Indicators\
   ```
   
   或者使用Release配置编译，会自动复制到该目录。

### 在ATAS中使用

1. 打开ATAS平台
2. 打开图表
3. 右键点击图表 → "添加指标"
4. 在"自定义指标"类别中找到"订单流数据导出器"
5. 配置参数后点击确定

## 参数配置

| 参数名 | 说明 | 默认值 |
|--------|------|--------|
| 导出文件路径 | CSV文件保存位置 | `C:\ATASData\OrderFlowData.csv` |
| 导出Footprint详情 | 是否导出每个价格层级的详细数据 | 否 |
| 启动时清空文件 | 每次启动指标时是否清空已有文件 | 否 |
| 仅导出实时数据 | 只导出实时K线，忽略历史数据 | 否 |
| 导出POC数据 | 是否导出POC相关数据 | 是 |
| 导出Value Area | 是否导出VAH/VAL/VWAP | 是 |
| 导出Max Delta信息 | 是否导出最大Delta价格信息 | 是 |

## CSV输出示例

### 基础数据格式

```csv
Symbol,BarIndex,Time,LastTime,Open,High,Low,Close,Volume,Bid,Ask,Delta,Betweens,MaxDelta,MinDelta,OI,MaxOI,MinOI,Ticks,POCPrice,POCVolume,POCBid,POCAsk,VAH,VAL,VWAP,MaxPosDeltaPrice,MaxPosDeltaVolume,MaxNegDeltaPrice,MaxNegDeltaVolume
ES,100,2024-01-15 09:30:00,2024-01-15 09:34:59,5000.25,5002.50,4999.75,5001.00,15234,7521,7713,192,0,450,-210,125000,126000,124500,3452,5000.50,1850,923,927,5001.75,4999.25,5000.68,5002.00,320,4999.75,285
```

### 包含Footprint详情的格式

当启用"导出Footprint详情"时，每个价格层级会输出单独一行，包含额外的列：
- LevelPrice (价格)
- LevelVolume (该价格成交量)
- LevelBid (该价格Bid成交量)
- LevelAsk (该价格Ask成交量)
- LevelDelta (该价格Delta)
- LevelTicks (该价格Tick数)

## 数据分析应用

导出的CSV数据可用于：

1. **Python数据分析**
   ```python
   import pandas as pd
   df = pd.read_csv('OrderFlowData.csv')
   # 分析Delta与价格变动的关系
   df['PriceChange'] = df['Close'] - df['Open']
   correlation = df['Delta'].corr(df['PriceChange'])
   ```

2. **机器学习特征**
   - Delta, MaxDelta, MinDelta 可作为市场情绪指标
   - Bid/Ask比例可用于判断买卖压力
   - POC变化可用于识别支撑阻力位

3. **回测系统**
   - 使用订单流数据构建更精确的回测模型
   - 结合Footprint数据分析关键价格区域

## 注意事项

1. **性能考虑**：启用Footprint详情导出会显著增加文件大小
2. **磁盘空间**：长期运行建议定期清理或归档数据文件
3. **实时数据**：如果只需要实时分析，建议开启"仅导出实时数据"选项

## 常见问题

### Q: 编译时找不到ATAS.Indicators.dll？
A: 确认ATAS已正确安装，并检查项目文件中的DLL引用路径是否正确。

### Q: 导出的文件是空的？
A: 检查导出路径是否有写入权限，以及图表是否有数据加载。

### Q: 如何只导出某个时间段的数据？
A: 可以在图表设置中限制加载的历史数据范围，或者后期用Python/Excel筛选CSV数据。

## 许可证

MIT License

## 更新日志

### v1.0.0
- 初始版本
- 支持OHLCV、订单流数据、POC、Value Area导出
- 支持Footprint详情导出
