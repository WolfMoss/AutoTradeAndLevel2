using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text;
using ATAS.DataFeedsCore;
using ATAS.Strategies.Chart;
using OFT.Attributes;
using Utils.Common.Logging;

namespace ATASScheduledOrder
{
    public enum TradeDirectionOption
    {
        [Display(Name = "买入")]
        Buy = 0,

        [Display(Name = "卖出")]
        Sell = 1
    }

    public enum OrderTypeOption
    {
        [Display(Name = "市价单")]
        Market = 0,

        [Display(Name = "挂单(限价)")]
        Limit = 1
    }

    /// <summary>
    /// 在策略参数中设定预定时间，到达时间后按配置方向与订单类型自动下单。
    /// </summary>
    [DisplayName("定时下单策略")]
    public class ScheduledOrderStrategy : ChartStrategy
    {
        private const string LogFilePath = @"D:\ATASData\ScheduledOrderStrategy.log";

        private bool _orderPlacedToday;
        private DateTime _trackedDate = DateTime.MinValue;
        private readonly object _lockObject = new object();

        #region 策略参数

        [Parameter]
        [Display(Name = "预定小时", GroupName = "定时设置", Order = 1)]
        [Range(0, 23)]
        public int ScheduledHour { get; set; } = 9;

        [Parameter]
        [Display(Name = "预定分钟", GroupName = "定时设置", Order = 2)]
        [Range(0, 59)]
        public int ScheduledMinute { get; set; } = 30;

        [Parameter]
        [Display(Name = "预定秒", GroupName = "定时设置", Order = 3)]
        [Range(0, 59)]
        public int ScheduledSecond { get; set; } = 0;

        [Parameter]
        [Display(Name = "交易方向", GroupName = "订单设置", Order = 10)]
        public TradeDirectionOption TradeDirection { get; set; } = TradeDirectionOption.Buy;

        [Parameter]
        [Display(Name = "订单类型", GroupName = "订单设置", Order = 11)]
        public OrderTypeOption OrderKind { get; set; } = OrderTypeOption.Market;

        [Parameter]
        [Display(Name = "限价", Description = "挂单模式下必填", GroupName = "订单设置", Order = 12)]
        public decimal LimitPrice { get; set; } = 0m;

        [Parameter]
        [Display(Name = "交易数量", GroupName = "订单设置", Order = 13)]
        public decimal Quantity { get; set; } = 1m;

        #endregion

        protected override void OnStarted()
        {
            base.OnStarted();
            ResetDailyState(DateTime.Now.Date);
            var nextScheduledTime = GetNextScheduledTime(DateTime.Now);
            WriteLog($"策略已启动，下次下单时间 {FormatScheduledDateTime(nextScheduledTime)}");
            RaiseShowNotification($"定时下单策略已启动，下次下单 {FormatScheduledDateTime(nextScheduledTime)}");
        }

        protected override void OnStopping()
        {
            WriteLog("策略已停止");
            base.OnStopping();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar != CurrentBar - 1)
                return;

            var now = DateTime.Now;
            var today = now.Date;

            if (_trackedDate != today)
                ResetDailyState(today);

            if (_orderPlacedToday)
                return;

            var scheduledTime = GetNextScheduledTime(now);

            if (now < scheduledTime)
                return;

            PlaceScheduledOrder(scheduledTime);
        }

        private void PlaceScheduledOrder(DateTime scheduledTime)
        {
            lock (_lockObject)
            {
                if (_orderPlacedToday)
                    return;

                if (Portfolio == null || Security == null)
                {
                    WriteLog("Portfolio 或 Security 未设置，无法下单", LoggingLevel.Error);
                    RaiseShowNotification("定时下单失败：未选择账户或合约");
                    return;
                }

                if (Quantity <= 0m)
                {
                    WriteLog("交易数量必须大于 0", LoggingLevel.Error);
                    RaiseShowNotification("定时下单失败：交易数量无效");
                    return;
                }

                var direction = TradeDirection == TradeDirectionOption.Buy
                    ? OrderDirections.Buy
                    : OrderDirections.Sell;

                var orderType = OrderKind == OrderTypeOption.Market
                    ? OrderTypes.Market
                    : OrderTypes.Limit;

                if (orderType == OrderTypes.Limit && LimitPrice <= 0m)
                {
                    WriteLog("挂单模式需要设置有效限价", LoggingLevel.Error);
                    RaiseShowNotification("定时下单失败：请设置限价");
                    return;
                }

                try
                {
                    var order = new Order
                    {
                        Portfolio = Portfolio,
                        Security = Security,
                        Direction = direction,
                        Type = orderType,
                        QuantityToFill = Quantity
                    };

                    if (orderType == OrderTypes.Limit)
                        order.Price = ShrinkPrice(LimitPrice);

                    OpenOrder(order);
                    _orderPlacedToday = true;

                    var message = orderType == OrderTypes.Limit
                        ? $"定时下单已提交: {GetDirectionLabel(direction)} {Quantity} 手，限价 {order.Price}，触发时间 {FormatScheduledDateTime(scheduledTime)}"
                        : $"定时下单已提交: {GetDirectionLabel(direction)} {Quantity} 手，市价单，触发时间 {FormatScheduledDateTime(scheduledTime)}";

                    WriteLog(message);
                    RaiseShowNotification(message);
                }
                catch (Exception ex)
                {
                    WriteLog($"下单异常: {ex.Message}", LoggingLevel.Error);
                    RaiseShowNotification($"定时下单失败: {ex.Message}");
                }
            }
        }

        private void ResetDailyState(DateTime date)
        {
            _trackedDate = date;
            _orderPlacedToday = false;
        }

        private DateTime GetScheduledTimeOnDate(DateTime date)
        {
            return date.Date
                .AddHours(ScheduledHour)
                .AddMinutes(ScheduledMinute)
                .AddSeconds(ScheduledSecond);
        }

        /// <summary>
        /// 若当日预定时间已过，则顺延至次日同一时刻。
        /// </summary>
        private DateTime GetNextScheduledTime(DateTime now)
        {
            var scheduledTime = GetScheduledTimeOnDate(now);

            if (scheduledTime <= now)
                scheduledTime = scheduledTime.AddDays(1);

            return scheduledTime;
        }

        private static string FormatScheduledDateTime(DateTime scheduledTime)
        {
            return scheduledTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static string GetDirectionLabel(OrderDirections direction)
        {
            return direction == OrderDirections.Buy ? "买入" : "卖出";
        }

        private static void WriteLog(string message, LoggingLevel level = LoggingLevel.Info)
        {
            try
            {
                var logDir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logMessage = $"[{timestamp}] [{level.ToString().ToUpper()}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, logMessage, Encoding.UTF8);
            }
            catch
            {
                // 日志写入失败不影响策略运行
            }
        }
    }
}
