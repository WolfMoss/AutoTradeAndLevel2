using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ATAS.Indicators;
using Newtonsoft.Json;
using OFT.Attributes;

namespace ATASOrderFlowExporter
{
    /// <summary>
    /// ATAS 订单流K线数据导出指标
    /// 功能：获取订单流K线行情数据并存入CSV文件
    /// 数据包括：OHLCV、Bid/Ask成交量、Delta、持仓量(OI)、Footprint数据等
    /// </summary>
    [DisplayName("订单流数据导出器")]
    [Category("自定义指标")]
    [Description("将订单流K线数据导出到CSV文件，包括OHLCV、Bid/Ask量、Delta、OI等数据")]
    public class OrderFlowDataExporter : Indicator
    {
        #region 字段

        private readonly object _fileLock = new object();
        private string _lastExportedSymbol = string.Empty;
        private int _lastExportedBar = -1;
        private bool _mainHeaderWritten = false;
        private bool _footprintHeaderWritten = false;
        private long _mainLastBarOffset = 0;
        private long _fpLastBarOffset = 0;

        // WebSocket相关字段
        private HttpListener _httpListener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly List<WebSocket> _webSocketClients = new List<WebSocket>();
        private readonly object _wsClientsLock = new object();
        private bool _lastEnableWebSocketValue = true;

        // 日志相关字段
        private readonly object _logLock = new object();

        #endregion

        #region 参数设置

        /// <summary>
        /// CSV文件保存路径 (主数据)
        /// </summary>
        [Display(Name = "主数据导出路径", GroupName = "导出设置", Order = 10)]
        public string ExportFilePath { get; set; } = @"D:\ATASData\OrderFlowData_Main.csv";

        /// <summary>
        /// CSV文件保存路径 (Footprint数据)
        /// </summary>
        [Display(Name = "Footprint导出路径", GroupName = "导出设置", Order = 11)]
        public string FootprintExportFilePath { get; set; } = @"D:\ATASData\OrderFlowData_Footprint.csv";

        /// <summary>
        /// 是否导出Footprint详细数据（每个价格层级的Bid/Ask）
        /// </summary>
        [Display(Name = "导出Footprint详情", GroupName = "导出设置", Order = 20)]
        public bool ExportFootprintDetails { get; set; } = true;

        /// <summary>
        /// 是否每次启动时清空文件
        /// </summary>
        [Display(Name = "启动时清空文件", GroupName = "导出设置", Order = 30)]
        public bool ClearOnStart { get; set; } = true;

        /// <summary>
        /// 是否只导出实时数据（忽略历史数据）
        /// </summary>
        [Display(Name = "仅导出实时数据", GroupName = "导出设置", Order = 40)]
        public bool OnlyRealTimeData { get; set; } = false;

        /// <summary>
        /// 是否导出POC（成交量最大的价格点）
        /// </summary>
        [Display(Name = "导出POC数据", GroupName = "数据选项", Order = 50)]
        public bool ExportPOC { get; set; } = true;

        /// <summary>
        /// 是否导出Value Area数据
        /// </summary>
        [Display(Name = "导出Value Area", GroupName = "数据选项", Order = 60)]
        public bool ExportValueArea { get; set; } = false;

        /// <summary>
        /// 是否导出最大Delta价格信息
        /// </summary>
        [Display(Name = "导出Max Delta信息", GroupName = "数据选项", Order = 70)]
        public bool ExportMaxDeltaInfo { get; set; } = true;

        /// <summary>
        /// WebSocket服务端口
        /// </summary>
        [Display(Name = "WebSocket端口", GroupName = "WebSocket设置", Order = 80)]
        public int WebSocketPort { get; set; } = 9527;

        /// <summary>
        /// 是否启用WebSocket服务
        /// </summary>
        [Display(Name = "启用WebSocket服务", GroupName = "WebSocket设置", Order = 81)]
        public bool EnableWebSocket { get; set; } = true;

        /// <summary>
        /// 日志文件路径
        /// </summary>
        [Display(Name = "日志文件路径", GroupName = "日志设置", Order = 90)]
        public string LogFilePath { get; set; } = @"D:\ATASData\OrderFlowExporter.log";

        /// <summary>
        /// 是否启用文件日志
        /// </summary>
        [Display(Name = "启用文件日志", GroupName = "日志设置", Order = 91)]
        public bool EnableFileLog { get; set; } = true;

        #endregion

        #region 构造函数

        public OrderFlowDataExporter()
        {
            // 启用订单流数据
            EnableCustomDrawing = false;
            DataSeries[0].IsHidden = true;
        }

        #endregion

        #region 核心方法

        /// <summary>
        /// 初始化时调用，用于清空文件
        /// </summary>
        protected override void OnInitialize()
        {
            base.OnInitialize();

            _mainHeaderWritten = false;
            _footprintHeaderWritten = false;
            _lastExportedBar = -1;
            _mainLastBarOffset = 0;
            _fpLastBarOffset = 0;

            if (ClearOnStart)
            {
                try
                {
                    if (File.Exists(ExportFilePath))
                    {
                        File.Delete(ExportFilePath);
                    }
                    if (File.Exists(FootprintExportFilePath))
                    {
                        File.Delete(FootprintExportFilePath);
                    }
                }
                catch (Exception ex)
                {
                    AddInfoLog($"清空文件失败: {ex.Message}");
                }
            }

            // 确保目录存在
            EnsureDirectoryExists();

            // 记录初始值
            _lastEnableWebSocketValue = EnableWebSocket;

            // 启动WebSocket服务（如果启用）
            UpdateWebSocketServerState();
        }

        /// <summary>
        /// 每根K线计算时调用
        /// </summary>
        /// <param name="bar">当前K线索引</param>
        /// <param name="isNewBar">是否是新K线</param>
        protected override void OnCalculate(int bar, decimal value)
        {
            // 检查WebSocket服务状态是否变化
            if (_lastEnableWebSocketValue != EnableWebSocket)
            {
                _lastEnableWebSocketValue = EnableWebSocket;
                UpdateWebSocketServerState();
            }

            // 如果只导出实时数据，则只处理最后一根K线
            if (OnlyRealTimeData && bar < CurrentBar - 1)
                return;

            // 避免重复导出同一根K线
            // 修改：对于历史K线，避免重复导出；对于实时K线（最后一根），允许重复导出以更新数据
            if (bar == _lastExportedBar && bar < CurrentBar - 1 && 
                InstrumentInfo?.Instrument != null && 
                InstrumentInfo.Instrument == _lastExportedSymbol)
                return;

            try
            {
                var candle = GetCandle(bar);
                var data = ExtractOrderFlowData(bar, candle);
                ExportDataToCSV(data);

                _lastExportedBar = bar;
                _lastExportedSymbol = InstrumentInfo?.Instrument ?? string.Empty;
            }
            catch (Exception ex)
            {
                AddInfoLog($"导出数据时出错 (Bar={bar}): {ex.Message}");
            }
        }

        #endregion

        #region 数据提取

        /// <summary>
        /// 从K线中提取订单流数据
        /// </summary>
        private OrderFlowData ExtractOrderFlowData(int bar, IndicatorCandle candle)
        {
            var data = new OrderFlowData
            {
                // 基础信息
                Symbol = InstrumentInfo?.Instrument ?? "Unknown",
                BarIndex = bar,
                Time = candle.Time,
                LastTime = candle.LastTime,

                // OHLCV数据
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close,
                Volume = candle.Volume,

                // 订单流核心数据
                Bid = candle.Bid,
                Ask = candle.Ask,
                Delta = candle.Delta,
                Betweens = candle.Betweens,

                // Delta极值
                MaxDelta = candle.MaxDelta,
                MinDelta = candle.MinDelta,

                // 持仓量数据
                OI = candle.OI,
                MaxOI = candle.MaxOI,
                MinOI = candle.MinOI,

                // Tick数据
                Ticks = candle.Ticks
            };

            // 提取POC（Point of Control）数据 - 成交量最大的价格
            if (ExportPOC && candle.MaxVolumePriceInfo != null)
            {
                var poc = candle.MaxVolumePriceInfo;
                data.POCPrice = poc.Price;
                data.POCVolume = poc.Volume;
                data.POCBid = poc.Bid;
                data.POCAsk = poc.Ask;
            }

            // 提取Value Area数据
            if (ExportValueArea && candle.ValueArea != null)
            {
                var va = candle.ValueArea;
                data.VAH = va.ValueAreaHigh;
                data.VAL = va.ValueAreaLow;
                // 注意：VWAP 需要通过ValueArea或其他方式计算
                // data.VWAP = va.VWAP;  // 如果ValueArea有VWAP属性可以启用
            }

            // 提取最大Delta价格信息
            if (ExportMaxDeltaInfo)
            {
                if (candle.MaxPositiveDeltaPriceInfo != null)
                {
                    data.MaxPosDeltaPrice = candle.MaxPositiveDeltaPriceInfo.Price;
                    data.MaxPosDeltaVolume = candle.MaxPositiveDeltaPriceInfo.Volume;
                }
                if (candle.MaxNegativeDeltaPriceInfo != null)
                {
                    data.MaxNegDeltaPrice = candle.MaxNegativeDeltaPriceInfo.Price;
                    data.MaxNegDeltaVolume = candle.MaxNegativeDeltaPriceInfo.Volume;
                }
            }

            // 提取Footprint详情（可选）
            if (ExportFootprintDetails)
            {
                data.FootprintLevels = new List<FootprintLevel>();
                var priceLevels = candle.GetAllPriceLevels();
                if (priceLevels != null)
                {
                    foreach (var level in priceLevels)
                    {
                        data.FootprintLevels.Add(new FootprintLevel
                        {
                            Price = level.Price,
                            Volume = level.Volume,
                            Bid = level.Bid,
                            Ask = level.Ask,
                            Delta = level.Ask - level.Bid,
                            Ticks = level.Ticks
                        });
                    }
                }
            }

            return data;
        }

        #endregion

        #region CSV导出

        /// <summary>
        /// 确保导出目录存在
        /// </summary>
        private void EnsureDirectoryExists()
        {
            try
            {
                var mainDir = Path.GetDirectoryName(ExportFilePath);
                if (!string.IsNullOrEmpty(mainDir) && !Directory.Exists(mainDir))
                {
                    Directory.CreateDirectory(mainDir);
                }

                var fpDir = Path.GetDirectoryName(FootprintExportFilePath);
                if (!string.IsNullOrEmpty(fpDir) && !Directory.Exists(fpDir))
                {
                    Directory.CreateDirectory(fpDir);
                }
            }
            catch (Exception ex)
            {
                AddInfoLog($"创建目录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 将数据导出到CSV文件
        /// </summary>
        private void ExportDataToCSV(OrderFlowData data)
        {
            lock (_fileLock)
            {
                // 1. 导出主数据
                try
                {
                    UpdateFileContent(ExportFilePath, GetMainCSVHeader(), GetMainCSVDataLine(data), data.BarIndex, ref _mainHeaderWritten, ref _mainLastBarOffset);
                }
                catch (Exception ex)
                {
                    AddInfoLog($"写入主CSV失败: {ex.Message}");
                }

                // 2. 导出Footprint数据
                if (ExportFootprintDetails && data.FootprintLevels != null && data.FootprintLevels.Count > 0)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (var level in data.FootprintLevels)
                        {
                            sb.AppendLine(GetFootprintCSVDataLine(data, level));
                        }
                        // 去掉最后一个换行符，因为UpdateFileContent会用WriteLine
                        string fpContent = sb.ToString().TrimEnd('\r', '\n');
                        UpdateFileContent(FootprintExportFilePath, GetFootprintCSVHeader(), fpContent, data.BarIndex, ref _footprintHeaderWritten, ref _fpLastBarOffset);
                    }
                    catch (Exception ex)
                    {
                        AddInfoLog($"写入Footprint CSV失败: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 核心写入逻辑：如果是新Bar则追加，如果是同一个Bar则覆盖最后一部分
        /// </summary>
        private void UpdateFileContent(string path, string header, string content, int barIndex, ref bool headerWritten, ref long lastBarOffset)
        {
            // 使用 FileStream 以支持 Seek 和 SetLength
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                // 如果文件是空的或者还没写过表头
                if (stream.Length == 0 || !headerWritten)
                {
                    stream.SetLength(0); // 确保清空
                    using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                    {
                        writer.WriteLine(header);
                    }
                    headerWritten = true;
                    lastBarOffset = stream.Position;
                }

                if (barIndex > _lastExportedBar)
                {
                    // 这是一个新Bar：在文件末尾追加
                    stream.Seek(0, SeekOrigin.End);
                    lastBarOffset = stream.Position;
                    using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                    {
                        writer.WriteLine(content);
                    }
                }
                else if (barIndex == _lastExportedBar)
                {
                    // 这是同一个Bar的更新：回到该Bar开始的位置，重写数据
                    stream.SetLength(lastBarOffset);
                    stream.Seek(lastBarOffset, SeekOrigin.Begin);
                    using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                    {
                        writer.WriteLine(content);
                    }
                }
                // 如果 barIndex < _lastExportedBar，通常是历史数据重算，这里不做处理以保持文件顺序
            }
        }

        /// <summary>
        /// 获取主数据CSV表头
        /// </summary>
        private string GetMainCSVHeader()
        {
            var headers = new List<string>
            {
                "Symbol",
                "BarIndex",
                "Time",
                "LastTime",
                "Open",
                "High",
                "Low",
                "Close",
                "Volume",
                "Bid",
                "Ask",
                "Delta",
                "Betweens",
                "MaxDelta",
                "MinDelta",
                "OI",
                "MaxOI",
                "MinOI",
                "Ticks"
            };

            if (ExportPOC)
            {
                headers.AddRange(new[] { "POCPrice", "POCVolume", "POCBid", "POCAsk" });
            }

            if (ExportValueArea)
            {
                headers.AddRange(new[] { "VAH", "VAL", "VWAP" });
            }

            if (ExportMaxDeltaInfo)
            {
                headers.AddRange(new[] 
                { 
                    "MaxPosDeltaPrice", "MaxPosDeltaVolume",
                    "MaxNegDeltaPrice", "MaxNegDeltaVolume"
                });
            }

            return string.Join(",", headers);
        }

        /// <summary>
        /// 获取Footprint数据CSV表头
        /// </summary>
        private string GetFootprintCSVHeader()
        {
            return string.Join(",", new[] 
            { 
                "Symbol", "BarIndex", "Time",
                "LevelPrice", "LevelVolume", "LevelBid", "LevelAsk", "LevelDelta", "LevelTicks" 
            });
        }

        /// <summary>
        /// 获取主CSV数据行
        /// </summary>
        private string GetMainCSVDataLine(OrderFlowData data)
        {
            var values = new List<string>
            {
                data.Symbol,
                data.BarIndex.ToString(),
                data.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                data.LastTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                FormatDecimal(data.Open),
                FormatDecimal(data.High),
                FormatDecimal(data.Low),
                FormatDecimal(data.Close),
                FormatDecimal(data.Volume),
                FormatDecimal(data.Bid),
                FormatDecimal(data.Ask),
                FormatDecimal(data.Delta),
                FormatDecimal(data.Betweens),
                FormatDecimal(data.MaxDelta),
                FormatDecimal(data.MinDelta),
                FormatDecimal(data.OI),
                FormatDecimal(data.MaxOI),
                FormatDecimal(data.MinOI),
                FormatDecimal(data.Ticks)
            };

            if (ExportPOC)
            {
                values.AddRange(new[] 
                { 
                    FormatDecimal(data.POCPrice), 
                    FormatDecimal(data.POCVolume),
                    FormatDecimal(data.POCBid),
                    FormatDecimal(data.POCAsk)
                });
            }

            if (ExportValueArea)
            {
                values.AddRange(new[] 
                { 
                    FormatDecimal(data.VAH), 
                    FormatDecimal(data.VAL),
                    FormatDecimal(data.VWAP)
                });
            }

            if (ExportMaxDeltaInfo)
            {
                values.AddRange(new[] 
                { 
                    FormatDecimal(data.MaxPosDeltaPrice),
                    FormatDecimal(data.MaxPosDeltaVolume),
                    FormatDecimal(data.MaxNegDeltaPrice),
                    FormatDecimal(data.MaxNegDeltaVolume)
                });
            }

            return string.Join(",", values);
        }

        /// <summary>
        /// 获取Footprint CSV数据行
        /// </summary>
        private string GetFootprintCSVDataLine(OrderFlowData data, FootprintLevel level)
        {
            return string.Join(",", new[]
            {
                data.Symbol,
                data.BarIndex.ToString(),
                data.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                FormatDecimal(level.Price),
                FormatDecimal(level.Volume),
                FormatDecimal(level.Bid),
                FormatDecimal(level.Ask),
                FormatDecimal(level.Delta),
                FormatDecimal(level.Ticks)
            });
        }

        /// <summary>
        /// 格式化decimal值
        /// </summary>
        private string FormatDecimal(decimal value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region WebSocket服务

        /// <summary>
        /// 更新WebSocket服务器状态（根据EnableWebSocket参数）
        /// </summary>
        private void UpdateWebSocketServerState()
        {
            if (EnableWebSocket)
            {
                // 如果服务未启动，则启动
                if (_httpListener == null || !_httpListener.IsListening)
                {
                    StartWebSocketServer();
                }
            }
            else
            {
                // 如果服务已启动，则停止
                if (_httpListener != null && _httpListener.IsListening)
                {
                    StopWebSocketServer();
                }
            }
        }

        /// <summary>
        /// 启动WebSocket服务器
        /// </summary>
        private void StartWebSocketServer()
        {
            try
            {
                _httpListener = new HttpListener();
                
                // 尝试监听所有接口（需要管理员权限或URL预留）
                // 如果失败，则回退到localhost
                bool useAllInterfaces = false;
                try
                {
                    _httpListener.Prefixes.Add($"http://+:{WebSocketPort}/");
                    _httpListener.Start();
                    useAllInterfaces = true;
                    AddInfoLog($"WebSocket服务器已启动，监听所有接口，端口: {WebSocketPort}");
                }
                catch (HttpListenerException)
                {
                    // 如果没有权限，回退到localhost
                    if (_httpListener != null)
                    {
                        _httpListener.Close();
                        _httpListener = null;
                    }
                    
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://localhost:{WebSocketPort}/");
                    _httpListener.Prefixes.Add($"http://127.0.0.1:{WebSocketPort}/");
                    _httpListener.Start();
                    AddInfoLog($"WebSocket服务器已启动（仅本地），端口: {WebSocketPort}");
                    AddInfoLog($"提示：如需外部访问，请以管理员身份运行ATAS，或执行命令：");
                    AddInfoLog($"netsh http add urlacl url=http://+:{WebSocketPort}/ user=Everyone");
                }

                _cancellationTokenSource = new CancellationTokenSource();

                // 异步处理WebSocket连接
                Task.Run(async () => await AcceptWebSocketConnections(_cancellationTokenSource.Token));
            }
            catch (Exception ex)
            {
                AddInfoLog($"启动WebSocket服务器失败: {ex.Message}");
                AddInfoLog($"详细错误: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    AddInfoLog($"内部错误: {ex.InnerException.Message}");
                }
            }
        }

        /// <summary>
        /// 接受WebSocket连接
        /// </summary>
        private async Task AcceptWebSocketConnections(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        ProcessWebSocketRequest(context);
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // HttpListener已关闭，正常退出
                    break;
                }
                catch (Exception ex)
                {
                    AddInfoLog($"接受WebSocket连接时出错: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 处理WebSocket请求
        /// </summary>
        private async void ProcessWebSocketRequest(HttpListenerContext context)
        {
            WebSocketContext webSocketContext = null;
            try
            {
                webSocketContext = await context.AcceptWebSocketAsync(null);
                var webSocket = webSocketContext.WebSocket;

                lock (_wsClientsLock)
                {
                    _webSocketClients.Add(webSocket);
                }
                AddInfoLog($"WebSocket客户端已连接，当前连接数: {_webSocketClients.Count}");

                // 保持连接并处理消息（如果需要接收客户端消息）
                await ReceiveMessages(webSocket);
            }
            catch (Exception ex)
            {
                AddInfoLog($"处理WebSocket请求失败: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        /// <summary>
        /// 接收WebSocket消息（保持连接活跃）
        /// </summary>
        private async Task ReceiveMessages(WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AddInfoLog($"接收WebSocket消息时出错: {ex.Message}");
            }
            finally
            {
                lock (_wsClientsLock)
                {
                    _webSocketClients.Remove(webSocket);
                }
                AddInfoLog($"WebSocket客户端已断开，当前连接数: {_webSocketClients.Count}");
            }
        }

        /// <summary>
        /// 停止WebSocket服务器
        /// </summary>
        private void StopWebSocketServer()
        {
            try
            {
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                }

                if (_httpListener != null)
                {
                    _httpListener.Stop();
                    _httpListener.Close();
                    _httpListener = null;
                }

                lock (_wsClientsLock)
                {
                    foreach (var client in _webSocketClients.ToList())
                    {
                        try
                        {
                            if (client.State == WebSocketState.Open)
                            {
                                client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None).Wait(1000);
                            }
                        }
                        catch { }
                    }
                    _webSocketClients.Clear();
                }

                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                AddInfoLog("WebSocket服务器已停止");
            }
            catch (Exception ex)
            {
                AddInfoLog($"停止WebSocket服务器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 推送数据到所有WebSocket客户端
        /// </summary>
        private void BroadcastToWebSocketClients(string jsonData)
        {
            if (_httpListener == null || !EnableWebSocket)
                return;

            var data = Encoding.UTF8.GetBytes(jsonData);
            lock (_wsClientsLock)
            {
                var disconnectedClients = new List<WebSocket>();
                foreach (var client in _webSocketClients)
                {
                    try
                    {
                        if (client.State == WebSocketState.Open)
                        {
                            client.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        else
                        {
                            disconnectedClients.Add(client);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddInfoLog($"发送WebSocket消息失败: {ex.Message}");
                        disconnectedClients.Add(client);
                    }
                }

                // 移除已断开的客户端
                foreach (var client in disconnectedClients)
                {
                    _webSocketClients.Remove(client);
                }
            }
        }

        #endregion

        #region Tick数据处理

        /// <summary>
        /// 重写OnNewTrade方法，获取tick数据并推送给WebSocket客户端
        /// </summary>
        protected override void OnNewTrade(MarketDataArg arg)
        {
            try
            {
                if (arg == null)
                    return;

                // 创建Tick数据对象
                var tickData = new TickData
                {
                    Symbol = InstrumentInfo?.Instrument ?? "Unknown",
                    Time = arg.Time,
                    Price = arg.Price,
                    Volume = arg.Volume,
                    Direction = arg.Direction.ToString() ?? "Unknown"
                };

                // 序列化为JSON
                var jsonData = JsonConvert.SerializeObject(tickData, Formatting.None);
                AddInfoLog($"处理Tick数据成功: {jsonData}");
                // 推送给所有WebSocket客户端
                BroadcastToWebSocketClients(jsonData);
            }
            catch (Exception ex)
            {
                AddInfoLog($"处理Tick数据失败: {ex.Message}");
            }
        }

        #endregion

        #region 资源清理

        /// <summary>
        /// 指标移除时调用，清理资源
        /// </summary>
        protected override void OnDispose()
        {
            // 停止WebSocket服务器
            //StopWebSocketServer();

            base.OnDispose();
        }

        #endregion

        #region 日志

        /// <summary>
        /// 添加日志信息
        /// </summary>
        private void AddInfoLog(string message)
        {
            var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [OrderFlowExporter] {message}";
            


            // 写入日志文件（如果启用）
            if (EnableFileLog)
            {
                WriteLogToFile(logMessage);
            }
        }

        /// <summary>
        /// 写入日志到文件
        /// </summary>
        private void WriteLogToFile(string message)
        {
            try
            {
                lock (_logLock)
                {
                    // 确保日志目录存在
                    var logDir = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }

                    // 追加写入日志文件
                    File.AppendAllText(LogFilePath, message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // 如果写入日志文件失败，至少输出到Debug窗口
                System.Diagnostics.Debug.WriteLine($"[OrderFlowExporter] 写入日志文件失败: {ex.Message}");
            }
        }

        #endregion
    }

    #region 数据模型

    /// <summary>
    /// 订单流数据结构
    /// </summary>
    public class OrderFlowData
    {
        // 基础信息
        public string Symbol { get; set; }
        public int BarIndex { get; set; }
        public DateTime Time { get; set; }
        public DateTime LastTime { get; set; }

        // OHLCV
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }

        // 订单流数据
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal Delta { get; set; }
        public decimal Betweens { get; set; }
        public decimal MaxDelta { get; set; }
        public decimal MinDelta { get; set; }

        // 持仓量
        public decimal OI { get; set; }
        public decimal MaxOI { get; set; }
        public decimal MinOI { get; set; }

        // Tick数据
        public decimal Ticks { get; set; }

        // POC数据
        public decimal POCPrice { get; set; }
        public decimal POCVolume { get; set; }
        public decimal POCBid { get; set; }
        public decimal POCAsk { get; set; }

        // Value Area
        public decimal VAH { get; set; }
        public decimal VAL { get; set; }
        public decimal VWAP { get; set; }

        // Max Delta信息
        public decimal MaxPosDeltaPrice { get; set; }
        public decimal MaxPosDeltaVolume { get; set; }
        public decimal MaxNegDeltaPrice { get; set; }
        public decimal MaxNegDeltaVolume { get; set; }

        // Footprint详情
        public List<FootprintLevel> FootprintLevels { get; set; }
    }

    /// <summary>
    /// Footprint价格层级数据
    /// </summary>
    public class FootprintLevel
    {
        public decimal Price { get; set; }
        public decimal Volume { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal Delta { get; set; }
        public decimal Ticks { get; set; }
    }

    /// <summary>
    /// Tick数据结构（用于WebSocket推送）
    /// </summary>
    public class TickData
    {
        /// <summary>
        /// 合约代码
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// 成交时间
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// 成交价格
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// 成交量
        /// </summary>
        public decimal Volume { get; set; }

        /// <summary>
        /// 买价
        /// </summary>
        public decimal Bid { get; set; }

        /// <summary>
        /// 卖价
        /// </summary>
        public decimal Ask { get; set; }

        /// <summary>
        /// 交易所订单ID
        /// </summary>
        public string ExchangeOrderId { get; set; }

        /// <summary>
        /// 主动成交订单ID
        /// </summary>
        public string AggressorExchangeOrderId { get; set; }

        /// <summary>
        /// 成交方向（Buy/Sell）
        /// </summary>
        public string Direction { get; set; }
    }

    #endregion
}
