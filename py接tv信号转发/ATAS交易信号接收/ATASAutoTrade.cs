using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using ATAS.Strategies;
using ATAS.DataFeedsCore;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using ATAS.Strategies.Chart;
using Utils.Common.Logging;
using System.ComponentModel.DataAnnotations;

using Newtonsoft.Json;

namespace ATASAutoTradeStrategies
{
    /// <summary>
    /// 外部信号接收策略
    /// 通过WebSocket接收来自Python服务的交易信号并执行交易
    /// </summary>
    [DisplayName("外部信号接收策略")]
    public class ATASAutoTrade : ChartStrategy
    {
        #region 策略参数

        /// <summary>
        /// 是否启用WebSocket信号接收服务
        /// </summary>
        [Parameter]
        [Display(Name = "启用信号接收", GroupName = "信号接收设置", Order = 1)]
        public bool EnableSignalReceiver { get; set; } = true;

        /// <summary>
        /// WebSocket服务器地址
        /// </summary>
        [Parameter]
        [Display(Name = "WebSocket服务器地址", GroupName = "信号接收设置", Order = 2)]
        public string WebSocketHost { get; set; } = "localhost";

        /// <summary>
        /// WebSocket服务器端口
        /// </summary>
        [Parameter]
        [Display(Name = "WebSocket端口", GroupName = "信号接收设置", Order = 3)]
        public int WebSocketPort { get; set; } = 9528;

        /// <summary>
        /// 交易数量（手数）
        /// </summary>
        [Parameter]
        [Display(Name = "交易数量", GroupName = "交易设置", Order = 4)]
        public int Quantity { get; set; } = 1;

        /// <summary>
        /// 是否使用信号中的价格
        /// </summary>
        [Parameter]
        [Display(Name = "使用信号价格", GroupName = "交易设置", Order = 5)]
        public bool UseSignalPrice { get; set; } = false;

        #endregion

        #region 私有字段

        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _webSocketClientTask;
        private readonly object _lockObject = new object();
        private bool _isClientRunning = false;
        private static readonly object _logLock = new object();
        private const string LogFilePath = @"D:\ATASData\ATASAutoTrade.log";
        
        // 信号去重：记录最近处理的信号，避免重复执行
        private readonly Dictionary<string, DateTime> _recentSignals = new Dictionary<string, DateTime>();
        private readonly object _signalDedupLock = new object();
        private const int SignalDedupWindowSeconds = 2; // 2秒内的相同信号视为重复
        
        // JSON反序列化设置（复用，避免每次创建）
        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.None
        };

        #endregion

        #region 日志记录

        /// <summary>
        /// 自定义日志记录方法，将日志写入文件
        /// </summary>
        private void WriteLog(string message, LoggingLevel level = LoggingLevel.Info)
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

                    // 格式化日志消息
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var levelStr = level.ToString().ToUpper();
                    var logMessage = $"[{timestamp}] [{levelStr}] {message}";

                    // 追加写入日志文件
                    File.AppendAllText(LogFilePath, logMessage + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 如果日志写入失败，静默处理，避免影响主程序运行
            }
        }

        #endregion

        #region 策略生命周期

        protected override void OnStarted()
        {
            base.OnStarted();

            // 如果启用了信号接收，启动WebSocket客户端
            if (EnableSignalReceiver)
            {
                StartWebSocketClient();
            }

            WriteLog("策略已启动", LoggingLevel.Info);
        }

        protected override void OnStopping()
        {
            // 停止WebSocket客户端
            StopWebSocketClient();

            base.OnStopping();
            WriteLog("策略已停止", LoggingLevel.Info);
        }

        /// <summary>
        /// 指标计算方法（继承自BaseIndicator的抽象方法）
        /// 对于策略类，此方法可以留空
        /// </summary>
        protected override void OnCalculate(int bar, decimal value)
        {
            // 策略类不需要计算指标，留空即可
        }

        #endregion

        #region WebSocket客户端

        /// <summary>
        /// 启动WebSocket客户端
        /// </summary>
        private void StartWebSocketClient()
        {
            if (_isClientRunning)
            {
                WriteLog($"WebSocket客户端已在运行中 ({WebSocketHost}:{WebSocketPort})", LoggingLevel.Warning);
                return;
            }

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _webSocket = new ClientWebSocket();
                _isClientRunning = true;
                _webSocketClientTask = Task.Run(async () => await WebSocketClientLoop(_cancellationTokenSource.Token));

                WriteLog($"WebSocket客户端已启动，连接到 {WebSocketHost}:{WebSocketPort}", LoggingLevel.Info);
            }
            catch (Exception ex)
            {
                WriteLog($"启动WebSocket客户端失败: {ex.Message}", LoggingLevel.Error);
                _isClientRunning = false;
            }
        }

        /// <summary>
        /// 停止WebSocket客户端
        /// </summary>
        private void StopWebSocketClient()
        {
            if (!_isClientRunning)
                return;

            try
            {
                _cancellationTokenSource?.Cancel();
                
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "策略停止", CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
                    }
                    catch { }
                }
                
                _webSocket?.Dispose();
                _webSocket = null;
                _isClientRunning = false;

                if (_webSocketClientTask != null && !_webSocketClientTask.IsCompleted)
                {
                    _webSocketClientTask.Wait(TimeSpan.FromSeconds(5));
                }

                WriteLog("WebSocket客户端已停止", LoggingLevel.Info);
            }
            catch (Exception ex)
            {
                WriteLog($"停止WebSocket客户端时出错: {ex.Message}", LoggingLevel.Error);
            }
        }

        /// <summary>
        /// WebSocket客户端主循环（包含自动重连）
        /// </summary>
        private async Task WebSocketClientLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var uri = new Uri($"ws://{WebSocketHost}:{WebSocketPort}");
                    WriteLog($"正在连接到WebSocket服务器: {uri}", LoggingLevel.Info);

                    await _webSocket.ConnectAsync(uri, cancellationToken);
                    WriteLog("已连接到WebSocket服务器", LoggingLevel.Info);

                    // 接收消息循环（使用更大的缓冲区提高性能）
                    var buffer = new byte[8192];
                    var messageBuilder = new StringBuilder();
                    
                    while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                    {
                        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                            
                            // 如果消息接收完整（EndOfMessage），处理信号
                            if (result.EndOfMessage)
                            {
                                var message = messageBuilder.ToString();
                                messageBuilder.Clear();
                                
                                // 异步处理信号，不阻塞接收循环
                                _ = Task.Run(() => ProcessSignal(message));
                            }
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            WriteLog("服务器关闭了连接", LoggingLevel.Warning);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，退出循环
                    break;
                }
                catch (WebSocketException ex)
                {
                    WriteLog($"WebSocket连接错误: {ex.Message}", LoggingLevel.Error);
                }
                catch (Exception ex)
                {
                    WriteLog($"WebSocket客户端错误: {ex.Message}", LoggingLevel.Error);
                }

                // 如果连接断开且未取消，等待后重连
                if (!cancellationToken.IsCancellationRequested)
                {
                    WriteLog("等待5秒后重新连接...", LoggingLevel.Info);
                    await Task.Delay(5000, cancellationToken);
                    
                    // 重新创建WebSocket对象
                    _webSocket?.Dispose();
                    _webSocket = new ClientWebSocket();
                }
            }
        }

        #endregion

        #region 信号处理

        /// <summary>
        /// 处理接收到的交易信号
        /// </summary>
        private void ProcessSignal(string message)
        {
            try
            {
                // 使用Newtonsoft.Json反序列化（复用设置提高性能）
                var signal = JsonConvert.DeserializeObject<SignalData>(message, _jsonSettings);

                if (signal == null)
                {
                    WriteLog($"无法解析信号: {message}", LoggingLevel.Warning);
                    return;
                }

                // 信号去重：检查是否在短时间内收到相同信号
                var signalKey = $"{signal.Ticker}_{signal.Action}_{signal.Interval}";
                lock (_signalDedupLock)
                {
                    var now = DateTime.Now;
                    if (_recentSignals.TryGetValue(signalKey, out var lastTime))
                    {
                        if ((now - lastTime).TotalSeconds < SignalDedupWindowSeconds)
                        {
                            // 重复信号，忽略
                            return;
                        }
                    }
                    // 更新信号时间戳
                    _recentSignals[signalKey] = now;
                    
                    // 清理过期的信号记录（避免内存泄漏）
                    var expiredKeys = _recentSignals.Where(kvp => (now - kvp.Value).TotalSeconds > SignalDedupWindowSeconds * 2)
                                                     .Select(kvp => kvp.Key)
                                                     .ToList();
                    foreach (var key in expiredKeys)
                    {
                        _recentSignals.Remove(key);
                    }
                }

                // 减少日志输出频率（只在DEBUG或重要信息时记录）
                // WriteLog($"收到信号: {signal.Ticker} | {signal.Action} | 价格: {signal.Price} | 周期: {signal.Interval}分钟", LoggingLevel.Info);

                // 检查信号是否匹配当前品种
                if (!string.IsNullOrEmpty(signal.Ticker) && Security != null)
                {
                    var securityName = Security.Code ?? Security.ToString() ?? "";
                    if (!securityName.Contains(signal.Ticker, StringComparison.OrdinalIgnoreCase))
                    {
                        // 品种不匹配，静默忽略（减少日志）
                        return;
                    }
                }

                // 执行交易
                ExecuteTrade(signal);
            }
            catch (Exception ex)
            {
                WriteLog($"处理信号时出错: {ex.Message}", LoggingLevel.Error);
            }
        }

        /// <summary>
        /// 执行交易
        /// </summary>
        private void ExecuteTrade(SignalData signal)
        {
            try
            {
                lock (_lockObject)
                {
                    // 确定交易方向（使用StringComparison优化性能）
                    OrderDirections orderDirection;
                    if (string.Equals(signal.Action, "buy", StringComparison.OrdinalIgnoreCase))
                    {
                        orderDirection = OrderDirections.Buy;
                    }
                    else if (string.Equals(signal.Action, "sell", StringComparison.OrdinalIgnoreCase))
                    {
                        orderDirection = OrderDirections.Sell;
                    }
                    else
                    {
                        WriteLog($"未知的交易动作: {signal.Action}", LoggingLevel.Warning);
                        return;
                    }

                    // 直接发出信号对应的指令，不考虑持仓
                    var order = new Order
                    {
                        Portfolio = Portfolio,
                        Security = Security,
                        Direction = orderDirection,
                        Type = OrderTypes.Market,
                        QuantityToFill = Quantity
                    };
                    OpenOrder(order);
                    // 减少日志输出，只在关键操作时记录
                    WriteLog($"订单已提交: {orderDirection} {Quantity}手", LoggingLevel.Info);
                }
            }
            catch (Exception ex)
            {
                WriteLog($"执行交易时出错: {ex.Message}", LoggingLevel.Error);
            }
        }

        #endregion

        #region 信号数据模型

        /// <summary>
        /// 交易信号数据模型
        /// </summary>
        private class SignalData
        {
            public string Ticker { get; set; }
            public string Action { get; set; }
            public decimal? Price { get; set; }
            public int? Interval { get; set; }
        }

        #endregion
    }
}

