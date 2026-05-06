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
/// 实时价格加速度指标（Tick）。
/// 先在固定时间窗口上估计滑动斜率速度 v ≈ dp/dt（与连续小波中尺度上的趋势一致）；
/// 再对速度求时间差分 a ≈ dv/dt（二阶效应，对应曲率/爆发性移动，与 WTMM 捕捉奇异性导数阶的思想一致）。
/// </summary>
[DisplayName("Tick Price Acceleration / Tick价格加速度")]
[Category(IndicatorCategories.Other)]
[Display(ResourceType = typeof(Strings), Description = "Price acceleration from tick data: rate of change of windowed velocity (second derivative proxy)")]
[HelpLink("https://docs.atas.net")]
public class TickPriceAcceleration : Indicator
{
    #region 私有字段

    private readonly Queue<(decimal Price, DateTime Time)> _tickHistory = new();

    private decimal _currentVelocity;
    private decimal _currentAcceleration;

    private decimal _lastVelocity;
    private DateTime _lastVelocitySampleUtc;
    private bool _hasVelocitySample;

    private readonly ValueDataSeries _accelerationSeries = new("AccelerationSeries", "Price Acceleration")
    {
        VisualType = VisualMode.Histogram,
        UseMinimizedModeIfEnabled = true,
        ResetAlertsOnNewBar = true,
        DescriptionKey = "Price acceleration (d(velocity)/dt) scaled for display"
    };

    private readonly ValueDataSeries _smoothedSeries = new("SmoothedSeries", "Smoothed Acceleration")
    {
        VisualType = VisualMode.Line,
        Width = 2,
        UseMinimizedModeIfEnabled = true,
        IgnoredByAlerts = true
    };

    private DrawingColor _positiveColor = DrawingColor.FromArgb(255, 0, 200, 100);
    private DrawingColor _negativeColor = DrawingColor.FromArgb(255, 220, 80, 80);
    private DrawingColor _smoothLineColor = DrawingColor.FromArgb(255, 100, 149, 237);

    private RenderFont _font = new("Roboto", 12);

    private decimal _emaValue;
    private bool _isEmaInitialized;

    #endregion

    #region 可配置属性

    [Display(Name = "Window (ms) / 时间窗口(毫秒)",
             GroupName = "Settings / 设置",
             Description = "Time window for estimating velocity v = Δprice/Δtime (first derivative proxy)",
             Order = 10)]
    [Range(100, 60000)]
    public int WindowMilliseconds { get; set; } = 1000;

    [Display(Name = "Multiplier / 放大系数",
             GroupName = "Settings / 设置",
             Description = "Scale factor for acceleration display",
             Order = 20)]
    [Range(1, 10000)]
    public int Multiplier { get; set; } = 100;

    [Display(Name = "Min dt (ms) / 加速度最小时距(毫秒)",
             GroupName = "Settings / 设置",
             Description = "Minimum Δt between velocity samples used for dv/dt; avoids spike from near-simultaneous ticks",
             Order = 25)]
    [Range(1, 500)]
    public int MinAccelerationDtMilliseconds { get; set; } = 16;

    [Display(Name = "Show Smoothed Line / 显示平滑线",
             GroupName = "Visualization / 可视化",
             Description = "Show EMA smoothed acceleration line",
             Order = 100)]
    public bool ShowSmoothedLine
    {
        get => _smoothedSeries.VisualType == VisualMode.Line;
        set => _smoothedSeries.VisualType = value ? VisualMode.Line : VisualMode.Hide;
    }

    [Display(Name = "EMA Period / EMA周期",
             GroupName = "Visualization / 可视化",
             Description = "EMA smoothing period on acceleration",
             Order = 110)]
    [Range(2, 100)]
    public int EmaPeriod { get; set; } = 10;

    [Display(Name = "Positive Color / 正加速度颜色",
             GroupName = "Colors / 颜色",
             Description = "Color when acceleration ≥ 0 (velocity rising)",
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

    [Display(Name = "Negative Color / 负加速度颜色",
             GroupName = "Colors / 颜色",
             Description = "Color when acceleration is negative (velocity falling)",
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

    [Display(Name = "Smooth Line Color / 平滑线颜色",
             GroupName = "Colors / 颜色",
             Description = "Color for smoothed acceleration line",
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

    [Display(Name = "Show Current Value / 显示当前值",
             GroupName = "Visualization / 可视化",
             Description = "Display current acceleration value on chart",
             Order = 120)]
    public bool ShowCurrentValue { get; set; } = true;

    #endregion

    #region 构造函数

    public TickPriceAcceleration()
        : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DenyToChangePanel = true;

        DataSeries[0] = _accelerationSeries;
        DataSeries.Add(_smoothedSeries);

        _smoothedSeries.Color = ToMediaColor(_smoothLineColor);

        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    #endregion

    #region 核心计算方法

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0)
        {
            _tickHistory.Clear();
            _currentVelocity = 0;
            _currentAcceleration = 0;
            _lastVelocity = 0;
            _hasVelocitySample = false;
            _emaValue = 0;
            _isEmaInitialized = false;
        }

        if (bar == CurrentBar - 1)
            return;

        if (!_isEmaInitialized)
        {
            _emaValue = _accelerationSeries[bar];
            _isEmaInitialized = true;
        }
        else
        {
            var k = 2.0m / (EmaPeriod + 1);
            _emaValue = _accelerationSeries[bar] * k + _emaValue * (1 - k);
        }

        _smoothedSeries[bar] = _emaValue;
    }

    protected override void OnNewTrade(MarketDataArg trade)
    {
        base.OnNewTrade(trade);

        var currentTime = DateTime.UtcNow;
        var currentPrice = trade.Price;

        _tickHistory.Enqueue((currentPrice, currentTime));

        var windowStart = currentTime.AddMilliseconds(-WindowMilliseconds);
        while (_tickHistory.Count > 0 && _tickHistory.Peek().Time < windowStart)
            _tickHistory.Dequeue();

        if (_tickHistory.Count >= 2)
        {
            var oldestTick = _tickHistory.Peek();
            var timeSpanSeconds = (currentTime - oldestTick.Time).TotalSeconds;
            if (timeSpanSeconds > 0)
                _currentVelocity = (currentPrice - oldestTick.Price) / (decimal)timeSpanSeconds;
        }
        else
            _currentVelocity = 0;

        var minDt = TimeSpan.FromMilliseconds(MinAccelerationDtMilliseconds);
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
                _currentAcceleration = (_currentVelocity - _lastVelocity) / (decimal)dtSec;
            _lastVelocity = _currentVelocity;
            _lastVelocitySampleUtc = currentTime;
        }

        var currentBarIndex = CurrentBar - 1;
        if (currentBarIndex < 0)
            return;

        var mult = Multiplier == 0 ? 1 : Multiplier;
        var recorded = _accelerationSeries[currentBarIndex] / mult;

        if (Math.Abs(_currentAcceleration) > Math.Abs(recorded))
        {
            _accelerationSeries[currentBarIndex] = Math.Abs(_currentAcceleration) * mult;
            _accelerationSeries.Colors[currentBarIndex] = _currentAcceleration >= 0 ? _positiveColor : _negativeColor;
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (!ShowCurrentValue || CurrentBar <= 0)
            return;

        if (Container == null)
            return;

        var text = $"Accel: {_currentAcceleration * Multiplier:F2}  (v={_currentVelocity * Multiplier:F2})";
        var size = context.MeasureString(text, _font);

        var x = Container.Region.Width - size.Width - 20;
        var y = 20;

        var textColor = _currentAcceleration >= 0 ? _positiveColor : _negativeColor;
        var bgColor = DrawingColor.FromArgb(180, 30, 30, 30);

        var rect = new System.Drawing.Rectangle(x - 5, y - 2, size.Width + 10, size.Height + 4);
        context.FillRectangle(bgColor, rect);
        context.DrawString(text, _font, textColor, rect,
            new RenderStringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            });
    }

    #endregion

    #region 辅助方法

    private static MediaColor ToMediaColor(DrawingColor color)
    {
        return MediaColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    public override string ToString()
    {
        return $"Tick Price Acceleration ({WindowMilliseconds}ms)";
    }

    #endregion
}
