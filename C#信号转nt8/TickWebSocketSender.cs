// 禁用程序集重复引用警告（CS1704）
// 这是 NinjaTrader 8 编译环境的已知问题，不影响功能
#pragma warning disable CS1704
#pragma warning disable 1704

#region Using declarations
using System;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// This namespace holds Indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// Tick 数据 HTTP Webhook 推送指标
    /// 通过 HTTP POST 将 tick 数据推送到外部服务器
    /// </summary>
    public class TickWebSocketSender : Indicator
    {
        #region Variables
        private HttpClient _httpClient;
        private string _webhookUrl = "http://localhost:8500/tick";
        private readonly object _lockObject = new object();
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"通过 HTTP POST 将 tick 数据推送到外部服务器";
                Name = "TickWebSocketSender";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                DisplayInDataBox = true;
                DrawOnPricePanel = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;
            }
            else if (State == State.Configure)
            {
                // 配置阶段
            }
            else if (State == State.DataLoaded)
            {
                // 数据加载完成，初始化 HTTP 客户端
                InitializeHttpClient();
            }
            else if (State == State.Historical)
            {
                // 历史数据状态 - 不推送，只处理实时数据
                Print("指标状态: Historical，跳过 Tick 推送");
            }
            else if (State == State.Realtime)
            {
                // 实时数据状态 - 确保 HTTP 客户端已初始化
                Print("指标状态: Realtime，检查 HTTP 客户端状态");
                lock (_lockObject)
                {
                    if (_httpClient == null)
                    {
                        Print("实时状态下 HTTP 客户端未初始化，尝试初始化...");
                        InitializeHttpClient();
                    }
                }
            }
            else if (State == State.Terminated)
            {
                // 指标终止，清理 HTTP 客户端
                Print("指标状态: Terminated，清理 HTTP 客户端");
                DisposeHttpClient();
            }
            else
            {
                Print(string.Format("指标状态变化: {0}", State));
            }
        }

        protected override void OnBarUpdate()
        {
            // 不需要更新任何图表数据，只用于推送 tick 数据
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // 当收到市场数据（tick）时调用
            if (e.MarketDataType == MarketDataType.Last)
            {
                // 只处理 Last 类型的 tick 数据
                SendTickData(e);
            }
        }

        /// <summary>
        /// 初始化 HTTP 客户端
        /// </summary>
        private void InitializeHttpClient()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_httpClient != null)
                    {
                        return; // 已经初始化
                    }

                    _httpClient = new HttpClient
                    {
                        Timeout = TimeSpan.FromSeconds(5) // 5秒超时
                    };
                    Print(string.Format("HTTP 客户端已初始化，Webhook URL: {0}", _webhookUrl));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("初始化 HTTP 客户端失败: {0}", ex.Message));
            }
        }

        /// <summary>
        /// 清理 HTTP 客户端
        /// </summary>
        private void DisposeHttpClient()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_httpClient != null)
                    {
                        _httpClient.Dispose();
                        _httpClient = null;
                        Print("HTTP 客户端已清理");
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("清理 HTTP 客户端时出错: {0}", ex.Message));
            }
        }

        /// <summary>
        /// 发送 tick 数据
        /// </summary>
        private void SendTickData(MarketDataEventArgs e)
        {
            // 检查指标状态
            if (State == State.Terminated || State == State.Historical)
            {
                return;
            }

            HttpClient client;
            
            lock (_lockObject)
            {
                if (_httpClient == null)
                {
                    // 如果未初始化，尝试初始化
                    InitializeHttpClient();
                    if (_httpClient == null)
                    {
                        return; // 初始化失败
                    }
                }
                client = _httpClient;
            }

            try
            {
                // 判断买卖方向：使用 OrderFlow 方法
                // 1. 优先使用 Ask/Bid 价格关系（更准确）
                // 2. 如果无法获取 Ask/Bid，则使用价格变化判断
                string direction = "unknown";
                
                try
                {
                    // 获取当前的 Ask 和 Bid 价格
                    double ask = GetCurrentAsk(0);
                    double bid = GetCurrentBid(0);
                    
                    if (!double.IsNaN(ask) && !double.IsNaN(bid) && ask > bid)
                    {
                        // 使用 Ask/Bid 判断：成交价更接近 Ask 为买入，更接近 Bid 为卖出
                        double askDiff = Math.Abs(e.Price - ask);
                        double bidDiff = Math.Abs(e.Price - bid);
                        double midPrice = (ask + bid) / 2.0;
                        
                        if (e.Price >= midPrice)
                        {
                            // 成交价在中价以上，更可能是买入
                            direction = "buy";
                        }
                        else
                        {
                            // 成交价在中价以下，更可能是卖出
                            direction = "sell";
                        }
                    }
                }
                catch
                {
                }
                
                
                // 构建 tick 数据 JSON 字符串（手动构建，避免依赖 Newtonsoft.Json）
                StringBuilder jsonBuilder = new StringBuilder();
                jsonBuilder.Append("{");
                jsonBuilder.AppendFormat("\"Type\":\"Tick\",");
                jsonBuilder.AppendFormat("\"Instrument\":\"{0}\",", EscapeJsonString(Instrument.FullName));
                jsonBuilder.AppendFormat("\"Price\":{0},", e.Price);
                jsonBuilder.AppendFormat("\"Volume\":{0},", e.Volume);
                jsonBuilder.AppendFormat("\"Time\":\"{0}\",", e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                jsonBuilder.AppendFormat("\"MarketDataType\":\"{0}\",", e.MarketDataType.ToString());
                jsonBuilder.AppendFormat("\"Direction\":\"{0}\"", direction);
                jsonBuilder.Append("}");
                
                string json = jsonBuilder.ToString();
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // 异步发送数据（不等待完成，避免阻塞）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = await client.PostAsync(_webhookUrl, content);
                        if (!response.IsSuccessStatusCode)
                        {
                            Print(string.Format("发送 tick 数据失败: HTTP {0}", response.StatusCode));
                        }
                    }
                    catch (HttpRequestException httpEx)
                    {
                        Print(string.Format("发送 tick 数据失败 (HTTP错误): {0}", httpEx.Message));
                    }
                    catch (TaskCanceledException)
                    {
                        Print("发送 tick 数据超时");
                    }
                    catch (Exception ex)
                    {
                        Print(string.Format("发送 tick 数据失败: {0} (类型: {1})", ex.Message, ex.GetType().Name));
                    }
                });
            }
            catch (Exception ex)
            {
                Print(string.Format("准备 tick 数据失败: {0}", ex.Message));
            }
        }

        /// <summary>
        /// 转义 JSON 字符串中的特殊字符
        /// </summary>
        private string EscapeJsonString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;
            
            return input.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\n", "\\n")
                       .Replace("\r", "\\r")
                       .Replace("\t", "\\t");
        }

        #region Properties
        [NinjaScriptProperty]
        public string WebhookUrl
        {
            get { return _webhookUrl; }
            set { _webhookUrl = value; }
        }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TickWebSocketSender[] cacheTickWebSocketSender;
		public TickWebSocketSender TickWebSocketSender(string webhookUrl)
		{
			return TickWebSocketSender(Input, webhookUrl);
		}

		public TickWebSocketSender TickWebSocketSender(ISeries<double> input, string webhookUrl)
		{
			if (cacheTickWebSocketSender != null)
				for (int idx = 0; idx < cacheTickWebSocketSender.Length; idx++)
					if (cacheTickWebSocketSender[idx] != null && cacheTickWebSocketSender[idx].WebhookUrl == webhookUrl && cacheTickWebSocketSender[idx].EqualsInput(input))
						return cacheTickWebSocketSender[idx];
			return CacheIndicator<TickWebSocketSender>(new TickWebSocketSender(){ WebhookUrl = webhookUrl }, input, ref cacheTickWebSocketSender);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TickWebSocketSender TickWebSocketSender(string webhookUrl)
		{
			return indicator.TickWebSocketSender(Input, webhookUrl);
		}

		public Indicators.TickWebSocketSender TickWebSocketSender(ISeries<double> input , string webhookUrl)
		{
			return indicator.TickWebSocketSender(input, webhookUrl);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TickWebSocketSender TickWebSocketSender(string webhookUrl)
		{
			return indicator.TickWebSocketSender(Input, webhookUrl);
		}

		public Indicators.TickWebSocketSender TickWebSocketSender(ISeries<double> input , string webhookUrl)
		{
			return indicator.TickWebSocketSender(input, webhookUrl);
		}
	}
}

#endregion
