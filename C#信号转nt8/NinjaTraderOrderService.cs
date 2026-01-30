using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NinjaTrader.Client;

namespace SignalForwarder;

/// <summary>
/// NinjaTrader 订单服务
/// </summary>
public class NinjaTraderOrderService : IDisposable
{
    private readonly ILogger _logger;
    private List<AccountConfig> _accounts;
    private readonly object _accountsLock = new();
    private Client? _client; // NinjaTrader.Client 对象
    private readonly object _clientLock = new();

    public NinjaTraderOrderService(ILogger logger, List<AccountConfig> accounts)
    {
        _logger = logger;
        _accounts = new List<AccountConfig>(accounts);
    }

    /// <summary>
    /// 更新账号配置
    /// </summary>
    public void UpdateAccounts(List<AccountConfig> newAccounts)
    {
        lock (_accountsLock)
        {
            _accounts = new List<AccountConfig>(newAccounts);
            _logger.LogInformation("账号配置已更新，共 {Count} 个账号", _accounts.Count);
        }
    }

    /// <summary>
    /// 初始化 NinjaTrader 客户端连接
    /// </summary>
    public void Initialize()
    {
        try
        {
            lock (_clientLock)
            {
                _client = new Client();
                _logger.LogInformation("NinjaTrader 客户端已初始化");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化 NinjaTrader 客户端失败");
        }
    }

    /// <summary>
    /// 连接 NinjaTrader
    /// </summary>
    public bool Connect()
    {
        try
        {
            lock (_clientLock)
            {
                if (_client == null)
                {
                    _logger.LogWarning("NinjaTrader 客户端未初始化");
                    return false;
                }

                // Connected(0) 返回 0 表示成功，-1 表示失败
                int connectionStatus = _client.Connected(0);
                if (connectionStatus == 0)
                {
                    _logger.LogInformation("已成功连接到 NinjaTrader 8");
                    return true;
                }
                else
                {
                    _logger.LogWarning("无法连接到 NinjaTrader 8。请检查：1. NinjaTrader 8 是否已打开？2. Tools -> Options -> Automated Trading Interface (ATI) 是否已勾选？");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "连接 NinjaTrader 失败");
            return false;
        }
    }

    /// <summary>
    /// 发送订单
    /// </summary>
    public void SubmitOrder(string account, string instrument, string action, int quantity, string orderType, double? price = null)
    {
        try
        {
            lock (_clientLock)
            {
                if (_client == null)
                {
                    _logger.LogWarning("NinjaTrader 客户端未初始化，无法发送订单");
                    return;
                }

                // 检查连接状态
                int connectionStatus = _client.Connected(0);
                if (connectionStatus != 0)
                {
                    _logger.LogWarning("NinjaTrader 客户端未连接，无法发送订单");
                    return;
                }

                // 确定订单方向：BUY 或 SELL
                var orderAction = action.ToLowerInvariant() == "buy" ? "BUY" : "SELL";

                // 确定订单类型：MARKET 或 LIMIT
                var ntOrderType = orderType.ToUpperInvariant() == "LIMIT" ? "LIMIT" : "MARKET";

                // 限价单价格，市价单填 0
                double limitPrice = (ntOrderType == "LIMIT" && price.HasValue) ? price.Value : 0;

                // 止损价（市价单填 0）
                double stopPrice = 0;

                // 有效期：DAY（日内有效）
                string tif = "DAY";

                // Command 参数：Command, Account, Instrument, Action, Quantity, OrderType, LimitPrice, StopPrice, TIF, OcoId, OrderId, StrategyId, StrategyName
                int result = _client.Command(
                    "PLACE",           // 命令：PLACE（下单）
                    account,            // 账户名
                    instrument,         // 合约名称
                    orderAction,        // BUY 或 SELL
                    quantity,          // 数量
                    ntOrderType,       // MARKET 或 LIMIT
                    limitPrice,         // 限价（市价单填 0）
                    stopPrice,          // 止损价（市价单填 0）
                    tif,               // 有效期
                    "",                 // OcoId
                    "",                 // OrderId
                    "",                 // StrategyId
                    ""                  // StrategyName
                );

                _logger.LogInformation("订单已提交: 账号={Account}, 品种={Instrument}, 方向={Action}, 手数={Quantity}, 类型={OrderType}, 价格={Price}, 结果={Result}",
                    account, instrument, orderAction, quantity, ntOrderType, 
                    limitPrice > 0 ? limitPrice.ToString("F2") : "市价", result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送订单失败: 账号={Account}, 品种={Instrument}, 方向={Action}", account, instrument, action);
        }
    }

    /// <summary>
    /// 处理交易信号（并发发送多个账号的订单）
    /// </summary>
    public void ProcessSignal(string ticker, string action, double? price)
    {
        List<AccountConfig> accountsCopy;
        lock (_accountsLock)
        {
            if (_accounts == null || _accounts.Count == 0)
            {
                _logger.LogDebug("没有配置账号，跳过订单发送");
                return;
            }
            accountsCopy = new List<AccountConfig>(_accounts);
        }

        // 过滤启用的账号
        var enabledAccounts = accountsCopy.Where(a => a.Enabled).ToList();
        if (enabledAccounts.Count == 0)
        {
            _logger.LogDebug("没有启用的账号，跳过订单发送");
            return;
        }

        // 并发发送订单到所有账号
        Parallel.ForEach(enabledAccounts, account =>
        {
            try
            {
                var orderType = account.OrderType;
                var orderPrice = orderType == "Limit" && price.HasValue ? price : null;

                SubmitOrder(account.Account, ticker, action, account.Quantity, orderType, orderPrice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "账号 {Account} 发送订单失败", account.Account);
            }
        });
    }

    public void Dispose()
    {
        try
        {
            lock (_clientLock)
            {
                if (_client != null)
                {
                    _client.TearDown();
                    _client = null;
                    _logger.LogInformation("NinjaTrader 客户端连接已断开");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "断开 NinjaTrader 连接时出错");
        }
    }
}

