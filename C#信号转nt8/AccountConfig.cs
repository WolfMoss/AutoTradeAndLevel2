namespace SignalForwarder;

/// <summary>
/// 账号配置类
/// </summary>
public class AccountConfig
{
    /// <summary>
    /// 账号名称
    /// </summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>
    /// 交易手数
    /// </summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// 订单类型：Market（市价）或 Limit（限价）
    /// </summary>
    public string OrderType { get; set; } = "Market";

    /// <summary>
    /// 是否启用此账号
    /// </summary>
    public bool Enabled { get; set; } = true;
}

