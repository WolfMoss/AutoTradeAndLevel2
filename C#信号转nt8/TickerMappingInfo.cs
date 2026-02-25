namespace SignalForwarder;

/// <summary>
/// 品种映射信息类
/// </summary>
public class TickerMappingInfo
{
    /// <summary>
    /// 目标品种名称
    /// </summary>
    public string TargetTicker { get; set; } = string.Empty;

    /// <summary>
    /// 交易手数
    /// </summary>
    public int Quantity { get; set; } = 1;
}

