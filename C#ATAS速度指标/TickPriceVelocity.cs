namespace ATAS.Indicators.Technical;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Indicators.Drawing;

using OFT.Attributes;
using OFT.Localization;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

/// <summary>
/// 实时价格速度指标 - 基于Tick数据计算价格变化速度
/// Real-time Price Velocity Indicator - Calculates price change velocity based on Tick data
/// </summary>
[DisplayName("Tick Price Velocity / Tick价格速度")]
[Category(IndicatorCategories.Other)]
[Display(ResourceType = typeof(Strings), Description = "Measures real-time price change velocity based on tick data")]
[HelpLink("https://docs.atas.net")]
public class TickPriceVelocity : Indicator
{
    #region 私有字段

    // 用于存储最近的Tick价格和时间
    private readonly Queue<(decimal Price, DateTime Time)> _tickHistory = new();
    
    // 当前计算的速度值
    private decimal _currentVelocity;
    
    // 上一次的价格（用于计算变化）
    private decimal _lastPrice;
    
    // 速度显示的数据系列
    private readonly ValueDataSeries _velocitySeries = new("VelocitySeries", "Price Velocity")
    {
        VisualType = VisualMode.Histogram,
        UseMinimizedModeIfEnabled = true,
        ResetAlertsOnNewBar = true,
        DescriptionKey = "Price change velocity per second"
    };

    // 平滑后的速度系列（可选EMA平滑）
    private readonly ValueDataSeries _smoothedSeries = new("SmoothedSeries", "Smoothed Velocity")
    {
        VisualType = VisualMode.Line,
        Width = 2,
        UseMinimizedModeIfEnabled = true,
        IgnoredByAlerts = true
    };

    // 颜色设置 (使用Drawing.Color用于渲染，Media.Color用于DataSeries)
    private DrawingColor _positiveColor = DrawingColor.FromArgb(255, 0, 200, 100);  // 绿色 - 价格上涨
    private DrawingColor _negativeColor = DrawingColor.FromArgb(255, 220, 80, 80);  // 红色 - 价格下跌
    private DrawingColor _smoothLineColor = DrawingColor.FromArgb(255, 100, 149, 237); // 蓝色

    // 绘图相关
    private RenderFont _font = new("Roboto", 12);
    
    // EMA计算相关
    private decimal _emaValue;
    private bool _isEmaInitialized;
    
    #endregion

    #region 可配置属性

    /// <summary>
    /// 计算窗口时间（毫秒）- 在此时间窗口内计算速度
    /// </summary>
    [Display(Name = "Window (ms) / 时间窗口(毫秒)", 
             GroupName = "Settings / 设置", 
             Description = "Time window in milliseconds for velocity calculation",
             Order = 10)]
    [Range(100, 60000)]
    public int WindowMilliseconds { get; set; } = 1000;

    /// <summary>
    /// 速度放大系数（用于更好的可视化）
    /// </summary>
    [Display(Name = "Multiplier / 放大系数", 
             GroupName = "Settings / 设置", 
             Description = "Multiplier for better visualization",
             Order = 20)]
    [Range(1, 10000)]
    public int Multiplier { get; set; } = 100;

    /// <summary>
    /// 是否显示平滑线
    /// </summary>
    [Display(Name = "Show Smoothed Line / 显示平滑线", 
             GroupName = "Visualization / 可视化", 
             Description = "Show EMA smoothed velocity line",
             Order = 100)]
    public bool ShowSmoothedLine
    {
        get => _smoothedSeries.VisualType == VisualMode.Line;
        set => _smoothedSeries.VisualType = value ? VisualMode.Line : VisualMode.Hide;
    }

    /// <summary>
    /// EMA平滑周期
    /// </summary>
    [Display(Name = "EMA Period / EMA周期", 
             GroupName = "Visualization / 可视化", 
             Description = "EMA smoothing period",
             Order = 110)]
    [Range(2, 100)]
    public int EmaPeriod { get; set; } = 10;

    /// <summary>
    /// 上涨颜色
    /// </summary>
    [Display(Name = "Positive Color / 上涨颜色", 
             GroupName = "Colors / 颜色", 
             Description = "Color for positive velocity (price rising)",
             Order = 200)]
    public DrawingColor PositiveColor
    {
        get => _positiveColor;
        set
        {
            _positiveColor = value;
            RecalculateValues();
        }
    }

    /// <summary>
    /// 下跌颜色
    /// </summary>
    [Display(Name = "Negative Color / 下跌颜色", 
             GroupName = "Colors / 颜色", 
             Description = "Color for negative velocity (price falling)",
             Order = 210)]
    public DrawingColor NegativeColor
    {
        get => _negativeColor;
        set
        {
            _negativeColor = value;
            RecalculateValues();
        }
    }

    /// <summary>
    /// 平滑线颜色
    /// </summary>
    [Display(Name = "Smooth Line Color / 平滑线颜色", 
             GroupName = "Colors / 颜色", 
             Description = "Color for smoothed velocity line",
             Order = 220)]
    public DrawingColor SmoothLineColor
    {
        get => _smoothLineColor;
        set
        {
            _smoothLineColor = value;
            _smoothedSeries.Color = ToMediaColor(_smoothLineColor);
        }
    }

    /// <summary>
    /// 是否在图表上显示当前速度值
    /// </summary>
    [Display(Name = "Show Current Value / 显示当前值", 
             GroupName = "Visualization / 可视化", 
             Description = "Display current velocity value on chart",
             Order = 120)]
    public bool ShowCurrentValue { get; set; } = true;

    #endregion

    #region 构造函数

    public TickPriceVelocity()
        : base(true) // true = 启用实时Tick数据订阅
    {
        Panel = IndicatorDataProvider.NewPanel;
        DenyToChangePanel = true;

        DataSeries[0] = _velocitySeries;
        DataSeries.Add(_smoothedSeries);

        _smoothedSeries.Color = ToMediaColor(_smoothLineColor);

        // 启用自定义绘图
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    #endregion

    #region 核心计算方法

    /// <summary>
    /// K线计算回调 - 每个K线和每个Tick都会触发
    /// </summary>
    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0)
        {
            _tickHistory.Clear();
            _currentVelocity = 0;
            _lastPrice = value;
            _emaValue = 0;
            _isEmaInitialized = false;
        }

        // 注意：历史K线无法回溯Tick级最高速度，只能显示为0或平均速度。
        // 这里我们对历史数据不做处理（默认为0），仅从实时阶段开始记录峰值。
        
        // 如果是实时Bar，逻辑主要由OnNewTrade接管
        if (bar == CurrentBar - 1)
        {
            // 这是当前正在形成的Bar，OnNewTrade会更新它
            // 我们不需要在这里重置，否则会丢失OnNewTrade记录的峰值
            return;
        }
        
        // 历史K线：如果有数据（比如重载时），保留原值；否则为0
        // 如果需要估算历史平均速度，可以用 (Close - Open) / Time，但这可以作为后续优化


        // 计算EMA平滑
        if (!_isEmaInitialized)
        {
            _emaValue = _velocitySeries[bar];
            _isEmaInitialized = true;
        }
        else
        {
            var k = 2.0m / (EmaPeriod + 1);
            _emaValue = _velocitySeries[bar] * k + _emaValue * (1 - k);
        }
        
        _smoothedSeries[bar] = _emaValue;
    }

    /// <summary>
    /// 新Tick数据回调 - 实时处理每个Tick
    /// </summary>
    protected override void OnNewTrade(MarketDataArg trade)
    {
        base.OnNewTrade(trade);

        var currentTime = DateTime.UtcNow;
        var currentPrice = trade.Price;

        // 添加新的Tick到历史队列
        _tickHistory.Enqueue((currentPrice, currentTime));

        // 移除超出时间窗口的旧Tick
        var windowStart = currentTime.AddMilliseconds(-WindowMilliseconds);
        while (_tickHistory.Count > 0 && _tickHistory.Peek().Time < windowStart)
        {
            _tickHistory.Dequeue();
        }

        // 计算速度：(当前价格 - 窗口起始价格) / 时间间隔(秒)
        if (_tickHistory.Count >= 2)
        {
            var oldestTick = _tickHistory.Peek();
            var timeSpanSeconds = (currentTime - oldestTick.Time).TotalSeconds;
            
            if (timeSpanSeconds > 0)
            {
                // 速度 = 价格变化 / 时间
                var priceChange = currentPrice - oldestTick.Price;
                _currentVelocity = priceChange / (decimal)timeSpanSeconds;
            }
        }
        else
        {
            _currentVelocity = 0;
        }

        _lastPrice = currentPrice;

        // --- 核心修改：记录当前K线的最高速度 ---
        
        // 获取当前K线已记录的速度值（除以系数还原）
        int currentBarIndex = CurrentBar - 1;
        if (currentBarIndex < 0) return;

        decimal recordedMaxVelocity = _velocitySeries[currentBarIndex] / (Multiplier == 0 ? 1 : Multiplier);
        
        // 比较：如果当前瞬时速度的绝对值 > 已记录的最大速度绝对值，则更新
        // 注意：为了方便比较，柱子始终显示绝对值（朝上），通过颜色区分方向
        if (Math.Abs(_currentVelocity) > Math.Abs(recordedMaxVelocity))
        {
            // 始终存储绝对值
            _velocitySeries[currentBarIndex] = Math.Abs(_currentVelocity) * Multiplier;
            
            // 颜色依然根据原始速度的正负来区分
            _velocitySeries.Colors[currentBarIndex] = _currentVelocity >= 0 ? _positiveColor : _negativeColor;
            
            // 计算平滑线（针对当前Bar）
            // Redraw(); 
        }
    }

    /// <summary>
    /// 自定义渲染 - 在图表上显示当前速度值
    /// </summary>
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (!ShowCurrentValue || CurrentBar <= 0)
            return;

        if (Container == null)
            return;

        // 在右上角显示当前速度值
        var velocityText = $"Velocity: {_currentVelocity * Multiplier:F2}";
        var size = context.MeasureString(velocityText, _font);
        
        var x = Container.Region.Width - size.Width - 20;
        var y = 20;
        
        var textColor = _currentVelocity >= 0 ? _positiveColor : _negativeColor;
        var bgColor = DrawingColor.FromArgb(180, 30, 30, 30);
        
        var rect = new System.Drawing.Rectangle(x - 5, y - 2, size.Width + 10, size.Height + 4);
        context.FillRectangle(bgColor, rect);
        context.DrawString(velocityText, _font, textColor, rect, 
            new RenderStringFormat 
            { 
                Alignment = System.Drawing.StringAlignment.Center, 
                LineAlignment = System.Drawing.StringAlignment.Center 
            });
    }

    #endregion

    #region 辅助方法

    /// <summary>
    /// 将System.Drawing.Color转换为System.Windows.Media.Color
    /// </summary>
    private static MediaColor ToMediaColor(DrawingColor color)
    {
        return MediaColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    public override string ToString()
    {
        return $"Tick Price Velocity ({WindowMilliseconds}ms)";
    }

    #endregion
}
