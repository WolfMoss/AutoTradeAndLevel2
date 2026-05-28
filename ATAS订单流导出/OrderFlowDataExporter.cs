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
        private bool _orderBookHeaderWritten = false;
        private long _mainLastBarOffset = 0;
        private long _fpLastBarOffset = 0;
        private long _obLastBarOffset = 0;
        private int _lastOrderBookExportedBar = -1;

        // WebSocket相关字段
        private HttpListener _httpListener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly List<WebSocket> _webSocketClients = new List<WebSocket>();
        private readonly object _wsClientsLock = new object();
        private bool _lastEnableWebSocketValue = true;

        // 日志相关字段
        private readonly object _logLock = new object();

        // SQLite 相关字段
        private SqliteBarStore _sqliteStore;
        private bool _sqliteInitWarned;

        // bar_closed 推送：跳过图表首次加载时的假过渡
        private bool _formingBarSeen;

        // Tick 价格加速度（每根 bar 记录 |a| 最大时的带符号加速度）
        private readonly Queue<(decimal Price, DateTime Time)> _tickHistory = new Queue<(decimal Price, DateTime Time)>();
        private readonly Dictionary<int, decimal> _barMaxAccelerations = new Dictionary<int, decimal>();
        private decimal _currentVelocity;
        private decimal _currentAcceleration;
        private decimal _lastVelocity;
        private DateTime _lastVelocitySampleUtc;
        private bool _hasVelocitySample;

        #endregion

        #region 参数设置

        /// <summary>
        /// 是否导出到 SQLite（推荐：Python 侧读库，避免 CSV 文件锁）
        /// </summary>
        [Display(Name = "启用SQLite导出", GroupName = "SQLite设置", Order = 5)]
        public bool EnableSqliteExport { get; set; } = true;

        /// <summary>
        /// SQLite 数据库路径
        /// </summary>
        [Display(Name = "SQLite数据库路径", GroupName = "SQLite设置", Order = 6)]
        public string SqliteExportFilePath { get; set; } = @"D:\ATASData\OrderFlowData.db";

        /// <summary>
        /// 是否导出 CSV（关闭后仅写 SQLite，减少磁盘 IO）
        /// </summary>
        [Display(Name = "启用CSV导出", GroupName = "导出设置", Order = 9)]
        public bool EnableCsvExport { get; set; } = false;

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
        /// CSV文件保存路径 (订单簿数据)
        /// </summary>
        [Display(Name = "订单簿导出路径", GroupName = "导出设置", Order = 12)]
        public string OrderBookExportFilePath { get; set; } = @"D:\ATASData\OrderFlowData_OrderBook.csv";

        /// <summary>
        /// 是否导出Footprint详细数据（每个价格层级的Bid/Ask）
        /// </summary>
        [Display(Name = "导出Footprint详情", GroupName = "导出设置", Order = 20)]
        public bool ExportFootprintDetails { get; set; } = false;

        /// <summary>
        /// 是否在 BAR 收盘时导出订单簿快照（每个价位一行，关联 Symbol + BarIndex）
        /// </summary>
        [Display(Name = "收K导出订单簿", GroupName = "导出设置", Order = 21)]
        public bool ExportOrderBookOnBarClose { get; set; } = true;

        /// <summary>
        /// 以收K收盘价为中心，上下各 N 个 tick 范围内的订单簿档位
        /// </summary>
        [Display(Name = "订单簿Tick范围", GroupName = "导出设置", Order = 22)]
        [Range(1, 1000)]
        public int OrderBookTickRange { get; set; } = 100;

        /// <summary>
        /// 订单簿档位最小挂单量（手数）
        /// </summary>
        [Display(Name = "订单簿最小手数", GroupName = "导出设置", Order = 23)]
        [Range(1, 1000000000)]
        public decimal OrderBookMinVolume { get; set; } = 10m;

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
        /// 是否计算并导出相对上一根K线的涨跌百分比列（CloseChgPct、VolumeChgPct、DeltaChgPct 等 *ChgPct 列）
        /// </summary>
        [Display(Name = "导出Pct变化列", GroupName = "数据选项", Order = 75)]
        public bool ExportPctChangeColumns { get; set; } = true;

        /// <summary>
        /// 是否导出每根 bar 内 Tick 价格加速度峰值（算法同 TickPriceAcceleration）
        /// </summary>
        [Display(Name = "导出Bar最大加速度", GroupName = "数据选项", Order = 76)]
        public bool ExportBarAcceleration { get; set; } = true;

        [Display(Name = "加速度时间窗口(毫秒)", GroupName = "数据选项", Order = 77,
            Description = "估计速度 v=Δprice/Δtime 的滑动时间窗口")]
        [Range(100, 60000)]
        public int AccelerationWindowMilliseconds { get; set; } = 1000;

        [Display(Name = "加速度最小时距(毫秒)", GroupName = "数据选项", Order = 78,
            Description = "计算 dv/dt 时两次速度采样的最小时间间隔，避免 tick 过密产生尖峰")]
        [Range(1, 500)]
        public int AccelerationMinDtMilliseconds { get; set; } = 16;

        [Display(Name = "加速度放大系数", GroupName = "数据选项", Order = 79,
            Description = "导出值 = 原始加速度 × 系数，与 TickPriceAcceleration 显示一致")]
        [Range(1, 10000)]
        public int AccelerationMultiplier { get; set; } = 100;

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
        /// BAR 收盘时推送 bar_closed 事件（Python 可事件驱动读 SQLite）
        /// </summary>
        [Display(Name = "推送BAR收盘事件", GroupName = "WebSocket设置", Order = 82)]
        public bool EnableBarClosedWebSocket { get; set; } = true;

        /// <summary>
        /// 是否通过 WebSocket 推送逐笔 Tick（无 event 字段；Python bar_closed 模式可关闭以减少流量）
        /// </summary>
        [Display(Name = "推送Tick数据", GroupName = "WebSocket设置", Order = 83)]
        public bool EnableTickWebSocket { get; set; } = false;

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
            _orderBookHeaderWritten = false;
            _lastExportedBar = -1;
            _lastOrderBookExportedBar = -1;
            _mainLastBarOffset = 0;
            _fpLastBarOffset = 0;
            _obLastBarOffset = 0;
            _formingBarSeen = false;
            ResetAccelerationState();

            if (ClearOnStart && EnableCsvExport)
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
                    if (File.Exists(OrderBookExportFilePath))
                    {
                        File.Delete(OrderBookExportFilePath);
                    }
                }
                catch (Exception ex)
                {
                    AddInfoLog($"清空文件失败: {ex.Message}");
                }
            }

            if (EnableSqliteExport)
            {
                try
                {
                    _sqliteStore?.Dispose();
                    _sqliteStore = new SqliteBarStore(SqliteExportFilePath);
                    _sqliteStore.EnsureReady(ClearOnStart);
                }
                catch (Exception ex)
                {
                    var detail = ex.InnerException != null ? $"{ex.Message} | Inner: {ex.InnerException.Message}" : ex.Message;
                    AddInfoLog($"初始化SQLite失败: {detail}");
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
            if (bar == 0)
            {
                ResetAccelerationState();
            }

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
                var previousExportedBar = _lastExportedBar;
                var candle = GetCandle(bar);
                var data = ExtractOrderFlowData(bar, candle);
                if (bar > 0 && ExportPctChangeColumns)
                {
                    var prevCandle = GetCandle(bar - 1);
                    ComputeChangePercentages(data, candle, prevCandle);
                }

                if (EnableCsvExport)
                {
                    ExportDataToCSV(data);
                }

                ExportDataToSqlite(data);
                TryHandleBarClosed(bar, previousExportedBar, data);

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
                Hour = candle.Time.Hour,

                // OHLCV数据
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close,
                Volume = candle.Volume,

                // 订单流核心数据
                Bid = candle.Bid,
                Ask = candle.Ask,
                Delta = candle.Delta == 0 ? 1 : candle.Delta,
                Betweens = candle.Betweens,

                // Delta极值
                MaxDelta = candle.MaxDelta == 0 ? 1 : candle.MaxDelta,
                MinDelta = candle.MinDelta == 0 ? 1 : candle.MinDelta,

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

            if (ExportBarAcceleration && _barMaxAccelerations.TryGetValue(bar, out var maxAcceleration))
            {
                var multiplier = AccelerationMultiplier == 0 ? 1 : AccelerationMultiplier;
                data.MaxBarAcceleration = maxAcceleration * multiplier;
            }

            return data;
        }

        private void ResetAccelerationState()
        {
            _tickHistory.Clear();
            _barMaxAccelerations.Clear();
            _currentVelocity = 0;
            _currentAcceleration = 0;
            _lastVelocity = 0;
            _hasVelocitySample = false;
        }

        /// <summary>
        /// 基于 Tick 流更新当前 bar 的最大加速度（逻辑同 TickPriceAcceleration.OnNewTrade）
        /// </summary>
        private void UpdateBarAcceleration(MarketDataArg trade)
        {
            var currentTime = DateTime.UtcNow;
            var currentPrice = trade.Price;

            _tickHistory.Enqueue((currentPrice, currentTime));

            var windowStart = currentTime.AddMilliseconds(-AccelerationWindowMilliseconds);
            while (_tickHistory.Count > 0 && _tickHistory.Peek().Time < windowStart)
            {
                _tickHistory.Dequeue();
            }

            if (_tickHistory.Count >= 2)
            {
                var oldestTick = _tickHistory.Peek();
                var timeSpanSeconds = (currentTime - oldestTick.Time).TotalSeconds;
                if (timeSpanSeconds > 0)
                {
                    _currentVelocity = (currentPrice - oldestTick.Price) / (decimal)timeSpanSeconds;
                }
            }
            else
            {
                _currentVelocity = 0;
            }

            var minDt = TimeSpan.FromMilliseconds(AccelerationMinDtMilliseconds);
            if (!_hasVelocitySample)
            {
                _lastVelocity = _currentVelocity;
                _lastVelocitySampleUtc = currentTime;
                _hasVelocitySample = true;
                _currentAcceleration = 0;
            }
            else if (currentTime - _lastVelocitySampleUtc >= minDt)
            {
                var dtSec = (currentTime - _lastVelocitySampleUtc).TotalSeconds;
                if (dtSec > 0)
                {
                    _currentAcceleration = (_currentVelocity - _lastVelocity) / (decimal)dtSec;
                }

                _lastVelocity = _currentVelocity;
                _lastVelocitySampleUtc = currentTime;
            }

            var currentBarIndex = CurrentBar - 1;
            if (currentBarIndex < 0)
            {
                return;
            }

            if (!_barMaxAccelerations.TryGetValue(currentBarIndex, out var recorded))
            {
                recorded = 0;
            }

            if (Math.Abs(_currentAcceleration) > Math.Abs(recorded))
            {
                _barMaxAccelerations[currentBarIndex] = _currentAcceleration;
            }
        }

        /// <summary>
        /// 计算相对上一根K线的涨跌百分比并写入 data
        /// </summary>
        private void ComputeChangePercentages(OrderFlowData data, IndicatorCandle current, IndicatorCandle prev)
        {
            data.CloseChgPct = ChangePct(current.Close, prev.Close);
            data.VolumeChgPct = ChangePct(current.Volume, prev.Volume);
            // Delta/MaxDelta/MinDelta 可能为负，用“变化量/上期绝对值”计算百分比；与 ExtractOrderFlowData 一致，上期为0时按1算避免空值
            decimal prevDelta = prev.Delta == 0 ? 1 : prev.Delta;
            decimal prevMaxDelta = prev.MaxDelta == 0 ? 1 : prev.MaxDelta;
            decimal prevMinDelta = prev.MinDelta == 0 ? 1 : prev.MinDelta;
            data.DeltaChgPct = ChangePctSigned(current.Delta, prevDelta);
            data.MaxDeltaChgPct = ChangePctSigned(current.MaxDelta, prevMaxDelta);
            data.MinDeltaChgPct = ChangePctSigned(current.MinDelta, prevMinDelta);
            data.TicksChgPct = ChangePct(current.Ticks, prev.Ticks);

            if (ExportPOC && current.MaxVolumePriceInfo != null && prev.MaxVolumePriceInfo != null)
            {
                data.POCPriceChgPct = ChangePct(current.MaxVolumePriceInfo.Price, prev.MaxVolumePriceInfo.Price);
                data.POCVolumeChgPct = ChangePct(current.MaxVolumePriceInfo.Volume, prev.MaxVolumePriceInfo.Volume);
            }

            if (ExportMaxDeltaInfo)
            {
                if (current.MaxPositiveDeltaPriceInfo != null && prev.MaxPositiveDeltaPriceInfo != null)
                {
                    data.MaxPosDeltaPriceChgPct = ChangePct(current.MaxPositiveDeltaPriceInfo.Price, prev.MaxPositiveDeltaPriceInfo.Price);
                    data.MaxPosDeltaVolumeChgPct = ChangePct(current.MaxPositiveDeltaPriceInfo.Volume, prev.MaxPositiveDeltaPriceInfo.Volume);
                }
                if (current.MaxNegativeDeltaPriceInfo != null && prev.MaxNegativeDeltaPriceInfo != null)
                {
                    data.MaxNegDeltaPriceChgPct = ChangePct(current.MaxNegativeDeltaPriceInfo.Price, prev.MaxNegativeDeltaPriceInfo.Price);
                    data.MaxNegDeltaVolumeChgPct = ChangePct(current.MaxNegativeDeltaPriceInfo.Volume, prev.MaxNegativeDeltaPriceInfo.Volume);
                }
            }
        }

        /// <summary>
        /// 计算涨跌百分比：(当前 - 上期) / 上期 * 100，上期为0时返回 null。适用于价格、成交量等非负值。
        /// </summary>
        private static decimal? ChangePct(decimal current, decimal prev)
        {
            if (prev == 0) return null;
            return (current - prev) / prev * 100;
        }

        /// <summary>
        /// 适用于可正可负的指标（如 Delta、MaxDelta、MinDelta）：(当前 - 上期) / |上期| * 100。
        /// 上期为 0 时返回 null，避免除零；分母用绝对值，正负号表示增减方向。
        /// </summary>
        private static decimal? ChangePctSigned(decimal current, decimal prev)
        {
            if (prev == 0) return null;
            decimal absPrev = Math.Abs(prev);
            return (current - prev) / absPrev * 100;
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

                var obDir = Path.GetDirectoryName(OrderBookExportFilePath);
                if (!string.IsNullOrEmpty(obDir) && !Directory.Exists(obDir))
                {
                    Directory.CreateDirectory(obDir);
                }
            }
            catch (Exception ex)
            {
                AddInfoLog($"创建目录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 新 forming BAR 出现时：收K导出订单簿，并可选推送 bar_closed
        /// </summary>
        private void TryHandleBarClosed(int bar, int previousExportedBar, OrderFlowData formingData)
        {
            var isFormingBar = bar == CurrentBar - 1;
            var isNewFormingTransition = isFormingBar
                && bar > previousExportedBar
                && previousExportedBar >= 0
                && previousExportedBar == bar - 1;

            if (isNewFormingTransition && _formingBarSeen)
            {
                try
                {
                    var completedCandle = GetCandle(previousExportedBar);
                    var completedData = ExtractOrderFlowData(previousExportedBar, completedCandle);
                    if (previousExportedBar > 0 && ExportPctChangeColumns)
                    {
                        var prevCandle = GetCandle(previousExportedBar - 1);
                        ComputeChangePercentages(completedData, completedCandle, prevCandle);
                    }

                    if (ExportOrderBookOnBarClose)
                    {
                        completedData.OrderBookLevels = CaptureOrderBookLevels(completedCandle.Close);
                        ExportOrderBookData(completedData);
                    }

                    if (EnableWebSocket && EnableBarClosedWebSocket)
                    {
                        BroadcastBarClosed(completedData, formingData);
                    }
                }
                catch (Exception ex)
                {
                    AddInfoLog($"处理收K事件失败 (Bar={previousExportedBar}): {ex.Message}");
                }
            }

            if (isFormingBar)
            {
                _formingBarSeen = true;
            }
        }

        /// <summary>
        /// 抓取当前 DOM 快照，过滤后作为已完成 BAR 的订单簿明细
        /// </summary>
        private List<OrderBookLevel> CaptureOrderBookLevels(decimal referencePrice)
        {
            var levels = new List<OrderBookLevel>();
            var tickSize = InstrumentInfo?.TickSize ?? 0m;
            if (tickSize <= 0m)
            {
                AddInfoLog("订单簿导出跳过：TickSize 无效");
                return levels;
            }

            var minPrice = referencePrice - OrderBookTickRange * tickSize;
            var maxPrice = referencePrice + OrderBookTickRange * tickSize;
            var snapshot = MarketDepthInfo?.GetMarketDepthSnapshot();
            if (snapshot == null)
            {
                return levels;
            }

            foreach (var depth in snapshot)
            {
                if (depth == null || depth.Volume < OrderBookMinVolume)
                {
                    continue;
                }

                if (depth.Price < minPrice || depth.Price > maxPrice)
                {
                    continue;
                }

                string side;
                if (depth.IsBid)
                {
                    side = "Bid";
                }
                else if (depth.IsAsk)
                {
                    side = "Ask";
                }
                else
                {
                    continue;
                }

                var tickOffset = (int)Math.Round((depth.Price - referencePrice) / tickSize, MidpointRounding.AwayFromZero);
                levels.Add(new OrderBookLevel
                {
                    Price = depth.Price,
                    Side = side,
                    Volume = depth.Volume,
                    TickOffset = tickOffset
                });
            }

            return levels
                .OrderBy(l => l.Price)
                .ThenBy(l => l.Side, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 导出订单簿明细（CSV + SQLite，关联 Symbol/BarIndex/Time）
        /// </summary>
        private void ExportOrderBookData(OrderFlowData data)
        {
            if (data?.OrderBookLevels == null || data.OrderBookLevels.Count == 0)
            {
                return;
            }

            if (EnableCsvExport)
            {
                ExportOrderBookToCsv(data);
            }

            ExportOrderBookToSqlite(data);
        }

        private void ExportOrderBookToCsv(OrderFlowData data)
        {
            lock (_fileLock)
            {
                try
                {
                    var sb = new StringBuilder();
                    foreach (var level in data.OrderBookLevels)
                    {
                        sb.AppendLine(GetOrderBookCSVDataLine(data, level));
                    }

                    var content = sb.ToString().TrimEnd('\r', '\n');
                    UpdateDetailFileContent(
                        OrderBookExportFilePath,
                        GetOrderBookCSVHeader(),
                        content,
                        data.BarIndex,
                        ref _orderBookHeaderWritten,
                        ref _obLastBarOffset,
                        ref _lastOrderBookExportedBar);
                }
                catch (Exception ex)
                {
                    AddInfoLog($"写入订单簿CSV失败: {ex.Message}");
                }
            }
        }

        private void ExportOrderBookToSqlite(OrderFlowData data)
        {
            if (!EnableSqliteExport || _sqliteStore == null)
            {
                return;
            }

            try
            {
                _sqliteStore.UpsertOrderBookLevels(data);
            }
            catch (Exception ex)
            {
                var detail = ex.InnerException != null ? $"{ex.Message} | Inner: {ex.InnerException.Message}" : ex.Message;
                AddInfoLog($"写入订单簿SQLite失败: {detail}");
            }
        }

        /// <summary>
        /// 推送 bar_closed 到 WebSocket 客户端
        /// </summary>
        private void BroadcastBarClosed(OrderFlowData completed, OrderFlowData forming)
        {
            var payload = new BarClosedEvent
            {
                Symbol = completed.Symbol,
                BarIndex = completed.BarIndex,
                Time = completed.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                FormingBarIndex = forming.BarIndex,
                FormingBarTime = forming.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Close = completed.Close,
                Volume = completed.Volume,
                Delta = completed.Delta
            };

            var json = JsonConvert.SerializeObject(payload, Formatting.None);
            AddInfoLog($"推送bar_closed: {json}");
            BroadcastToWebSocketClients(json);
        }

        /// <summary>
        /// 将 BAR 数据 UPSERT 到 SQLite
        /// </summary>
        private void ExportDataToSqlite(OrderFlowData data)
        {
            if (!EnableSqliteExport)
            {
                return;
            }

            if (_sqliteStore == null)
            {
                if (!_sqliteInitWarned)
                {
                    AddInfoLog("SQLite未初始化，跳过写入。请将 Microsoft.Data.Sqlite 相关 DLL 部署到 ATAS Indicators 目录后重启 ATAS。");
                    _sqliteInitWarned = true;
                }
                return;
            }

            try
            {
                _sqliteStore.UpsertBar(data);
            }
            catch (Exception ex)
            {
                var detail = ex.InnerException != null ? $"{ex.Message} | Inner: {ex.InnerException.Message}" : ex.Message;
                AddInfoLog($"写入SQLite失败: {detail}");
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
                else
                {
                    // 读取文件中的表头，比较是否与当前表头一致
                    stream.Seek(0, SeekOrigin.Begin);
                    string existingHeader = null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true))
                    {
                        existingHeader = reader.ReadLine();
                    }
                    
                    // 如果表头不一致（配置改变了），需要重新写入整个文件
                    if (existingHeader != header)
                    {
                        // 读取所有现有数据行（除了表头）
                        stream.Seek(0, SeekOrigin.Begin);
                        List<string> existingLines = new List<string>();
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true))
                        {
                            string firstLine = reader.ReadLine(); // 跳过旧表头
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                existingLines.Add(line);
                            }
                        }
                        
                        // 如果是更新同一个Bar，移除最后一行（当前Bar的旧数据）
                        if (barIndex == _lastExportedBar && existingLines.Count > 0)
                        {
                            existingLines.RemoveAt(existingLines.Count - 1);
                        }
                        
                        // 重新写入表头、之前的数据行和新数据
                        stream.SetLength(0);
                        stream.Seek(0, SeekOrigin.Begin);
                        using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                        {
                            writer.WriteLine(header);
                            foreach (var line in existingLines)
                            {
                                writer.WriteLine(line);
                            }
                            writer.WriteLine(content);
                        }
                        headerWritten = true;
                        // 更新lastBarOffset为新数据行的位置
                        lastBarOffset = Encoding.UTF8.GetByteCount(header + Environment.NewLine);
                        foreach (var line in existingLines)
                        {
                            lastBarOffset += Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                        }
                        return; // 已经处理完成，直接返回
                    }
                }

                // 表头一致的情况：直接追加或覆盖
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
        /// 明细文件写入（Footprint/订单簿）：按各自 lastBarIndex 追加或覆盖
        /// </summary>
        private void UpdateDetailFileContent(
            string path,
            string header,
            string content,
            int barIndex,
            ref bool headerWritten,
            ref long lastBarOffset,
            ref int lastDetailBarIndex)
        {
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                if (stream.Length == 0 || !headerWritten)
                {
                    stream.SetLength(0);
                    using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                    {
                        writer.WriteLine(header);
                    }
                    headerWritten = true;
                    lastBarOffset = stream.Position;
                }
                else
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    string existingHeader;
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true))
                    {
                        existingHeader = reader.ReadLine();
                    }

                    if (existingHeader != header)
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                        var existingLines = new List<string>();
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true))
                        {
                            reader.ReadLine();
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                existingLines.Add(line);
                            }
                        }

                        if (barIndex == lastDetailBarIndex && existingLines.Count > 0)
                        {
                            existingLines.RemoveAt(existingLines.Count - 1);
                        }

                        stream.SetLength(0);
                        stream.Seek(0, SeekOrigin.Begin);
                        using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                        {
                            writer.WriteLine(header);
                            foreach (var line in existingLines)
                            {
                                writer.WriteLine(line);
                            }
                            writer.Write(content);
                            writer.WriteLine();
                        }

                        headerWritten = true;
                        lastBarOffset = Encoding.UTF8.GetByteCount(header + Environment.NewLine);
                        foreach (var line in existingLines)
                        {
                            lastBarOffset += Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                        }
                        lastDetailBarIndex = barIndex;
                        return;
                    }
                }

                if (barIndex > lastDetailBarIndex)
                {
                    stream.Seek(0, SeekOrigin.End);
                    lastBarOffset = stream.Position;
                    using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                    {
                        writer.Write(content);
                        writer.WriteLine();
                    }
                }
                else if (barIndex == lastDetailBarIndex)
                {
                    stream.SetLength(lastBarOffset);
                    stream.Seek(lastBarOffset, SeekOrigin.Begin);
                    using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                    {
                        writer.Write(content);
                        writer.WriteLine();
                    }
                }

                lastDetailBarIndex = barIndex;
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
                "Hour",
                "Open",
                "High",
                "Low",
                "Close"
            };
            if (ExportPctChangeColumns)
                headers.Add("CloseChgPct");
            headers.Add("Volume");
            if (ExportPctChangeColumns)
                headers.Add("VolumeChgPct");
            headers.Add("Delta");
            if (ExportPctChangeColumns)
                headers.Add("DeltaChgPct");
            headers.Add("MaxDelta");
            if (ExportPctChangeColumns)
                headers.Add("MaxDeltaChgPct");
            headers.Add("MinDelta");
            if (ExportPctChangeColumns)
                headers.Add("MinDeltaChgPct");
            headers.Add("Ticks");
            if (ExportPctChangeColumns)
                headers.Add("TicksChgPct");

            if (ExportPOC)
            {
                if (ExportPctChangeColumns)
                    headers.AddRange(new[] { "POCPrice", "POCPriceChgPct", "POCVolume", "POCVolumeChgPct" });
                else
                    headers.AddRange(new[] { "POCPrice", "POCVolume" });
            }

            if (ExportValueArea)
            {
                headers.AddRange(new[] { "VAH", "VAL", "VWAP" });
            }

            if (ExportMaxDeltaInfo)
            {
                if (ExportPctChangeColumns)
                {
                    headers.AddRange(new[]
                    {
                        "MaxPosDeltaPrice", "MaxPosDeltaPriceChgPct",
                        "MaxPosDeltaVolume", "MaxPosDeltaVolumeChgPct",
                        "MaxNegDeltaPrice", "MaxNegDeltaPriceChgPct",
                        "MaxNegDeltaVolume", "MaxNegDeltaVolumeChgPct"
                    });
                }
                else
                {
                    headers.AddRange(new[]
                    {
                        "MaxPosDeltaPrice", "MaxPosDeltaVolume",
                        "MaxNegDeltaPrice", "MaxNegDeltaVolume"
                    });
                }
            }

            if (ExportBarAcceleration)
            {
                headers.Add("MaxBarAcceleration");
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
        /// 获取订单簿 CSV 表头
        /// </summary>
        private string GetOrderBookCSVHeader()
        {
            return string.Join(",", new[]
            {
                "Symbol", "BarIndex", "Time",
                "LevelPrice", "Side", "LevelVolume", "TickOffset"
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
                data.Hour.ToString(),
                FormatDecimal(data.Open),
                FormatDecimal(data.High),
                FormatDecimal(data.Low),
                FormatDecimal(data.Close)
            };
            if (ExportPctChangeColumns)
                values.Add(FormatDecimalNullable(data.CloseChgPct));
            values.Add(FormatDecimal(data.Volume));
            if (ExportPctChangeColumns)
                values.Add(FormatDecimalNullable(data.VolumeChgPct));
            values.Add(FormatDecimal(data.Delta));
            if (ExportPctChangeColumns)
                values.Add(FormatDecimalNullable(data.DeltaChgPct));
            values.Add(FormatDecimal(data.MaxDelta));
            if (ExportPctChangeColumns)
                values.Add(FormatDecimalNullable(data.MaxDeltaChgPct));
            values.Add(FormatDecimal(data.MinDelta));
            if (ExportPctChangeColumns)
                values.Add(FormatDecimalNullable(data.MinDeltaChgPct));
            values.Add(FormatDecimal(data.Ticks));
            if (ExportPctChangeColumns)
                values.Add(FormatDecimalNullable(data.TicksChgPct));

            if (ExportPOC)
            {
                if (ExportPctChangeColumns)
                {
                    values.AddRange(new[]
                    {
                        FormatDecimal(data.POCPrice),
                        FormatDecimalNullable(data.POCPriceChgPct),
                        FormatDecimal(data.POCVolume),
                        FormatDecimalNullable(data.POCVolumeChgPct)
                    });
                }
                else
                {
                    values.AddRange(new[]
                    {
                        FormatDecimal(data.POCPrice),
                        FormatDecimal(data.POCVolume)
                    });
                }
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
                if (ExportPctChangeColumns)
                {
                    values.AddRange(new[]
                    {
                        FormatDecimal(data.MaxPosDeltaPrice),
                        FormatDecimalNullable(data.MaxPosDeltaPriceChgPct),
                        FormatDecimal(data.MaxPosDeltaVolume),
                        FormatDecimalNullable(data.MaxPosDeltaVolumeChgPct),
                        FormatDecimal(data.MaxNegDeltaPrice),
                        FormatDecimalNullable(data.MaxNegDeltaPriceChgPct),
                        FormatDecimal(data.MaxNegDeltaVolume),
                        FormatDecimalNullable(data.MaxNegDeltaVolumeChgPct)
                    });
                }
                else
                {
                    values.AddRange(new[]
                    {
                        FormatDecimal(data.MaxPosDeltaPrice),
                        FormatDecimal(data.MaxPosDeltaVolume),
                        FormatDecimal(data.MaxNegDeltaPrice),
                        FormatDecimal(data.MaxNegDeltaVolume)
                    });
                }
            }

            if (ExportBarAcceleration)
            {
                values.Add(FormatDecimal(data.MaxBarAcceleration));
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
        /// 获取订单簿 CSV 数据行
        /// </summary>
        private string GetOrderBookCSVDataLine(OrderFlowData data, OrderBookLevel level)
        {
            return string.Join(",", new[]
            {
                data.Symbol,
                data.BarIndex.ToString(),
                data.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                FormatDecimal(level.Price),
                level.Side,
                FormatDecimal(level.Volume),
                level.TickOffset.ToString(CultureInfo.InvariantCulture)
            });
        }

        /// <summary>
        /// 格式化decimal值
        /// </summary>
        private string FormatDecimal(decimal value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 格式化可空的decimal值（涨跌百分比等），null 输出空字符串
        /// </summary>
        private string FormatDecimalNullable(decimal? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
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
                if (arg != null && ExportBarAcceleration)
                {
                    UpdateBarAcceleration(arg);
                }

                if (arg == null || !EnableWebSocket || !EnableTickWebSocket)
                    return;

                var tickData = new TickData
                {
                    Symbol = InstrumentInfo?.Instrument ?? "Unknown",
                    Time = arg.Time,
                    Price = arg.Price,
                    Volume = arg.Volume,
                    Direction = arg.Direction.ToString() ?? "Unknown"
                };

                var jsonData = JsonConvert.SerializeObject(tickData, Formatting.None);
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
        /// <summary>Time 的小时部分（0-23）</summary>
        public int Hour { get; set; }

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

        // 涨跌百分比（相对上一根K线）
        public decimal? CloseChgPct { get; set; }
        public decimal? VolumeChgPct { get; set; }
        public decimal? DeltaChgPct { get; set; }
        public decimal? MaxDeltaChgPct { get; set; }
        public decimal? MinDeltaChgPct { get; set; }
        public decimal? TicksChgPct { get; set; }
        public decimal? POCPriceChgPct { get; set; }
        public decimal? POCVolumeChgPct { get; set; }
        public decimal? MaxPosDeltaPriceChgPct { get; set; }
        public decimal? MaxPosDeltaVolumeChgPct { get; set; }
        public decimal? MaxNegDeltaPriceChgPct { get; set; }
        public decimal? MaxNegDeltaVolumeChgPct { get; set; }

        /// <summary>
        /// 该 bar 内 Tick 价格加速度峰值（带符号，已乘放大系数）
        /// </summary>
        public decimal MaxBarAcceleration { get; set; }

        // Footprint详情
        public List<FootprintLevel> FootprintLevels { get; set; }

        // 收K订单簿明细
        public List<OrderBookLevel> OrderBookLevels { get; set; }
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
    /// 收K订单簿价位数据
    /// </summary>
    public class OrderBookLevel
    {
        public decimal Price { get; set; }
        public string Side { get; set; }
        public decimal Volume { get; set; }
        public int TickOffset { get; set; }
    }

    /// <summary>
    /// BAR 收盘 WebSocket 事件
    /// </summary>
    public class BarClosedEvent
    {
        [JsonProperty("event")]
        public string Event { get; set; } = "bar_closed";

        public string Symbol { get; set; }
        public int BarIndex { get; set; }
        public string Time { get; set; }
        public int FormingBarIndex { get; set; }
        public string FormingBarTime { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public decimal Delta { get; set; }
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
