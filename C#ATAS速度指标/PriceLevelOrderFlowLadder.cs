namespace ATAS.Indicators.Technical;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

using ATAS.DataFeedsCore;
using ATAS.Indicators.Drawing;

using OFT.Attributes;
using OFT.Localization;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;

using DrawingColor = System.Drawing.Color;
using RenderControlMouseButtons = OFT.Rendering.Control.RenderControlMouseButtons;
using RenderControlMouseEventArgs = OFT.Rendering.Control.RenderControlMouseEventArgs;

/// <summary>
/// 在最新 BAR 右侧按价格显示加单/撤单流水（基于 MBO）。
/// </summary>
[DisplayName("Price Level OrderFlow Ladder / 价位撤加吃统计")]
[Category(IndicatorCategories.Other)]
[Display(ResourceType = typeof(Strings), Description = "Show add/cancel/executed stats by price level on latest bar right side")]
[HelpLink("https://docs.atas.net")]
public class PriceLevelOrderFlowLadder : Indicator
{
    private readonly object _sync = new();

    private readonly Dictionary<string, ActiveOrder> _activeOrders = new();
    private readonly Dictionary<decimal, PriceLevelStats> _statsByPrice = new();
    private readonly Dictionary<decimal, Queue<TapeEvent>> _tapeByPrice = new();
    private readonly Dictionary<decimal, Queue<PendingReduction>> _pendingReductionsByPrice = new();
    private long _eventSeq;

    private readonly ValueDataSeries _dummySeries = new("PriceLevelOrderFlowLadderDummy")
    {
        VisualType = VisualMode.Hide,
        IsHidden = true
    };

    private DrawingColor _headerColor = DrawingColor.FromArgb(255, 210, 210, 210);
    private DrawingColor _priceTextColor = DrawingColor.FromArgb(255, 240, 240, 240);
    private DrawingColor _addColor = DrawingColor.FromArgb(255, 90, 200, 120);
    private DrawingColor _cancelColor = DrawingColor.FromArgb(255, 255, 160, 80);
    private DrawingColor _bgColor = DrawingColor.FromArgb(170, 20, 20, 20);
    private DrawingColor _buttonBgColor = DrawingColor.FromArgb(220, 55, 55, 55);
    private DrawingColor _buttonTextColor = DrawingColor.FromArgb(255, 230, 230, 230);
    private DrawingColor _buttonBorderColor = DrawingColor.FromArgb(255, 120, 120, 120);

    private RenderFont _headerFont = new("Consolas", 12);
    private RenderFont _rowFont = new("Consolas", 11);
    private RenderFont _buttonFont = new("Consolas", 10);
    private bool _clearHistoryNow;
    private Rectangle _clearButtonRect = Rectangle.Empty;

    public PriceLevelOrderFlowLadder()
        : base(false)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = false;
        DataSeries[0] = _dummySeries;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Min volume filter / 最小统计量", GroupName = "Settings / 设置", Order = 20)]
    [Range(0, 1000000000)]
    public decimal MinVolumeFilter { get; set; } = 1m;

    [Display(Name = "Max rows / 最大显示行数", GroupName = "Settings / 设置", Order = 30)]
    [Range(5, 200)]
    public int MaxRows { get; set; } = 40;

    [Display(Name = "Right offset px / 右侧偏移像素", GroupName = "Settings / 设置", Order = 40)]
    [Range(10, 800)]
    public int ColumnOffsetPx { get; set; } = 200;

    [Display(Name = "Mask max width px / 遮罩最大宽度像素", GroupName = "Settings / 设置", Order = 60)]
    [Range(120, 2400)]
    public int MaskMaxWidthPx { get; set; } = 700;

    [Display(Name = "Show title / 显示标题", GroupName = "Visualization / 可视化", Order = 100)]
    public bool ShowTitle { get; set; } = true;

    [Display(Name = "Show clear button / 显示清空按钮", GroupName = "Visualization / 可视化", Order = 105)]
    public bool ShowClearButton { get; set; } = true;

    [Display(Name = "Clear history now / 一键清空历史", GroupName = "Visualization / 可视化", Order = 110)]
    public bool ClearHistoryNow
    {
        get => _clearHistoryNow;
        set
        {
            if (!_clearHistoryNow && value)
            {
                ClearHistoryState();
                _clearHistoryNow = false;
                RaisePropertyChanged(nameof(ClearHistoryNow));
                RedrawChart();
                return;
            }

            _clearHistoryNow = value;
        }
    }

    [Display(Name = "Header color / 标题颜色", GroupName = "Colors / 颜色", Order = 200)]
    public DrawingColor HeaderColor
    {
        get => _headerColor;
        set => _headerColor = value;
    }

    [Display(Name = "Price text color / 价格文字颜色", GroupName = "Colors / 颜色", Order = 210)]
    public DrawingColor PriceTextColor
    {
        get => _priceTextColor;
        set => _priceTextColor = value;
    }

    [Display(Name = "Add color / 加单颜色", GroupName = "Colors / 颜色", Order = 220)]
    public DrawingColor AddColor
    {
        get => _addColor;
        set => _addColor = value;
    }

    [Display(Name = "Cancel color / 撤单颜色", GroupName = "Colors / 颜色", Order = 230)]
    public DrawingColor CancelColor
    {
        get => _cancelColor;
        set => _cancelColor = value;
    }

    [Display(Name = "Background color / 背景色", GroupName = "Colors / 颜色", Order = 250)]
    public DrawingColor BackgroundColor
    {
        get => _bgColor;
        set => _bgColor = value;
    }

    [Display(Name = "Button background / 按钮背景色", GroupName = "Colors / 颜色", Order = 260)]
    public DrawingColor ButtonBackgroundColor
    {
        get => _buttonBgColor;
        set => _buttonBgColor = value;
    }

    protected override void OnInitialize()
    {
        base.OnInitialize();
        _ = SubscribeMboAsync();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // 实时聚合在事件回调中处理。
    }

    protected override void OnNewTrade(MarketDataArg trade)
    {
        base.OnNewTrade(trade);
    }

    protected override void OnMarketByOrdersChanged(IEnumerable<MarketByOrder> values)
    {
        base.OnMarketByOrdersChanged(values);
        if (values == null)
            return;

        lock (_sync)
        {
            var hasSnapshot = false;
            foreach (var x in values)
            {
                if (x.Type == MarketByOrderUpdateTypes.Snapshot)
                {
                    hasSnapshot = true;
                    break;
                }
            }

            if (hasSnapshot)
                _activeOrders.Clear();

            foreach (var m in values)
                ProcessMboEvent(m);

            var now = DateTime.UtcNow;
            PrunePendingReductions(now);
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0 || Container == null)
            return;

        List<RenderRow> rows;
        lock (_sync)
        {
            var candidates = _statsByPrice
                .Where(kv => kv.Value.Total >= MinVolumeFilter)
                .OrderByDescending(kv => kv.Key)
                .Take(MaxRows)
                .Select(kv => new RenderRow(kv.Key, kv.Value))
                .ToList();

            if (candidates.Count == 0)
                return;

            rows = candidates;
        }

        var latestBarX = ChartInfo.GetXByBar(CurrentBar - 1);
        var startX = latestBarX + ColumnOffsetPx;
        var header = "Price | Flow(+/-)   (从左到右=从新到旧)";
        var headerSize = context.MeasureString(header, _headerFont);
        var rowHeight = Math.Max(14, context.MeasureString("12345", _rowFont).Height + 2);

        var titleHeight = ShowTitle ? headerSize.Height + 6 : 0;
        var availableWidth = Math.Max(240, Container.Region.Width - startX - 12);
        var width = Math.Min(availableWidth, Math.Max(220, MaskMaxWidthPx));
        width = Math.Max(width, headerSize.Width + 8);
        var topY = Math.Max(Container.Region.Top + 6, 6);
        var panelRect = new Rectangle(startX - 4, Container.Region.Top, width + 8, Container.Region.Height);

        context.FillRectangle(_bgColor, panelRect);

        var currentY = topY;
        if (ShowTitle)
        {
            context.DrawString(header, _headerFont, _headerColor, new Rectangle(startX, currentY, width, headerSize.Height),
                new RenderStringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Near
                });
            currentY += titleHeight;
        }

        _clearButtonRect = Rectangle.Empty;
        if (ShowClearButton)
        {
            var buttonText = "清空";
            var btnTextSize = context.MeasureString(buttonText, _buttonFont);
            var buttonWidth = Math.Max(44, btnTextSize.Width + 14);
            var buttonHeight = Math.Max(18, btnTextSize.Height + 6);
            var buttonX = startX + width - buttonWidth - 6;
            var buttonY = topY + 2;
            _clearButtonRect = new Rectangle(buttonX, buttonY, buttonWidth, buttonHeight);

            context.FillRectangle(_buttonBgColor, _clearButtonRect);
            context.DrawRectangle(new RenderPen(_buttonBorderColor), _clearButtonRect);
            context.DrawString(buttonText, _buttonFont, _buttonTextColor, _clearButtonRect,
                new RenderStringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                });
        }

        var maxPriceWidth = 0;
        foreach (var r in rows)
        {
            var w = context.MeasureString(r.Price.ToString("0.########"), _rowFont).Width;
            if (w > maxPriceWidth)
                maxPriceWidth = w;
        }

        var priceColWidth = Math.Min(maxPriceWidth + 10, 120);
        var flowStartX = startX + priceColWidth + 10;
        var flowMaxWidth = Math.Max(60, (startX + width) - flowStartX - 6);

        foreach (var row in rows)
        {
            var textY = ChartInfo.GetYByPrice(row.Price) - (rowHeight / 2);
            if (textY < Container.Region.Top + titleHeight || textY > Container.Region.Bottom - rowHeight)
                continue;

            var priceText = row.Price.ToString("0.########");

            context.DrawString(priceText, _rowFont, _priceTextColor, new Rectangle(startX, textY, priceColWidth, rowHeight),
                new RenderStringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center });

            TapeEvent[] tape;
            lock (_sync)
            {
                tape = _tapeByPrice.TryGetValue(row.Price, out var q) ? q.ToArray() : Array.Empty<TapeEvent>();
            }

            var x = flowStartX;
            for (var i = tape.Length - 1; i >= 0; i--)
            {
                var e = tape[i];
                var token = FormatToken(e.Type, e.Volume);
                var tokenSize = context.MeasureString(token, _rowFont);
                if (x + tokenSize.Width > flowStartX + flowMaxWidth)
                    break;

                var c = e.Type switch
                {
                    EventType.Add => _addColor,
                    EventType.Cancel => _cancelColor,
                    _ => _priceTextColor
                };

                context.DrawString(token, _rowFont, c, new Rectangle(x, textY, tokenSize.Width + 2, rowHeight),
                    new RenderStringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center });

                x += tokenSize.Width + 6;
            }
        }
    }

    public override bool ProcessMouseUp(RenderControlMouseEventArgs e)
    {
        return TryHandleClearButtonClick(e);
    }

    public override bool ProcessMouseDown(RenderControlMouseEventArgs e)
    {
        return TryHandleClearButtonClick(e);
    }

    public override bool ProcessMouseClick(RenderControlMouseEventArgs e)
    {
        return TryHandleClearButtonClick(e);
    }

    public override OFT.Rendering.StdCursor GetCursor(RenderControlMouseEventArgs e)
    {
        if (ShowClearButton && IsPointInsideRectangle(_clearButtonRect, e.Location))
            return OFT.Rendering.StdCursor.Hand;

        return OFT.Rendering.StdCursor.NULL;
    }

    private async Task SubscribeMboAsync()
    {
        try
        {
            await SubscribeMarketByOrderData().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PriceLevelOrderFlowLadder] MBO subscribe failed: {ex}");
        }
    }

    private void ProcessMboEvent(MarketByOrder m)
    {
        if (m.Side != MarketDataType.Bid && m.Side != MarketDataType.Ask)
            return;

        var key = GetOrderKey(m);
        switch (m.Type)
        {
            case MarketByOrderUpdateTypes.Snapshot:
                IngestSnapshot(m);
                break;
            case MarketByOrderUpdateTypes.New:
                RegisterAdd(m.Price, m.Volume, m.Time);
                IngestSnapshot(m);
                break;
            case MarketByOrderUpdateTypes.Delete:
                ApplyDelete(key, m.Time);
                break;
            case MarketByOrderUpdateTypes.Change:
                ApplyChange(key, m, m.Time);
                break;
        }
    }

    private void IngestSnapshot(MarketByOrder m)
    {
        if (m.Volume <= 0)
            return;

        _activeOrders[GetOrderKey(m)] = new ActiveOrder(m.Price, m.Side, m.Volume);
    }

    private void ApplyDelete(string key, DateTime now)
    {
        if (!_activeOrders.TryGetValue(key, out var prev))
            return;

        RegisterCancel(prev.Price, prev.Volume, now);
        _activeOrders.Remove(key);
    }

    private void ApplyChange(string key, MarketByOrder m, DateTime now)
    {
        if (!_activeOrders.TryGetValue(key, out var prev))
        {
            if (m.Volume > 0)
                _activeOrders[key] = new ActiveOrder(m.Price, m.Side, m.Volume);
            return;
        }

        var delta = m.Volume - prev.Volume;
        if (delta > 0)
        {
            RegisterAdd(prev.Price, delta, now);
        }
        else if (delta < 0)
        {
            var reduction = Math.Abs(delta);
            RegisterPendingReduction(prev.Price, reduction, now);
        }

        if (m.Volume <= 0)
            _activeOrders.Remove(key);
        else
            _activeOrders[key] = new ActiveOrder(prev.Price, prev.Side, m.Volume);
    }

    private void RegisterPendingReduction(decimal price, decimal vol, DateTime eventTime)
    {
        if (vol <= 0)
            return;

        if (!_pendingReductionsByPrice.TryGetValue(price, out var q))
        {
            q = new Queue<PendingReduction>();
            _pendingReductionsByPrice[price] = q;
        }

        q.Enqueue(new PendingReduction(NextEventId(), eventTime, vol));
    }

    private void RegisterAdd(decimal price, decimal vol, DateTime eventTime)
    {
        if (vol <= 0)
            return;
        AddEvent(new AggregatedEvent(NextEventId(), eventTime, price, EventType.Add, vol));
    }

    private void RegisterCancel(decimal price, decimal vol, DateTime eventTime)
    {
        if (vol <= 0)
            return;
        AddEvent(new AggregatedEvent(NextEventId(), eventTime, price, EventType.Cancel, vol));
    }

    private void AddEvent(AggregatedEvent e)
    {
        if (!PassesMinVolumeFilter(e.Volume))
            return;

        ApplyStats(e.Price, e.Type, e.Volume);
        AppendTape(e);
    }

    private void PrunePendingReductions(DateTime now)
    {
        if (_pendingReductionsByPrice.Count == 0)
            return;

        var maxLag = TimeSpan.Zero;
        var emptyPrices = new List<decimal>();
        foreach (var kv in _pendingReductionsByPrice)
        {
            var q = kv.Value;
            PrunePendingQueue(kv.Key, q, now, maxLag);
            if (q.Count == 0)
                emptyPrices.Add(kv.Key);
        }

        foreach (var p in emptyPrices)
            _pendingReductionsByPrice.Remove(p);
    }

    private void PrunePendingQueue(decimal price, Queue<PendingReduction> q, DateTime now, TimeSpan maxLag)
    {
        while (q.Count > 0 && now - q.Peek().Time >= maxLag)
        {
            var p = q.Dequeue();
            RegisterCancel(price, p.Volume, p.Time);
        }
    }

    private void ApplyStats(decimal price, EventType type, decimal delta)
    {
        if (!_statsByPrice.TryGetValue(price, out var stats))
            stats = new PriceLevelStats();

        switch (type)
        {
            case EventType.Add:
                stats.AddVolume += delta;
                break;
            case EventType.Cancel:
                stats.CancelVolume += delta;
                break;
        }

        if (stats.Total <= 0)
            _statsByPrice.Remove(price);
        else
            _statsByPrice[price] = stats;
    }

    private static string GetOrderKey(MarketByOrder m)
    {
        if (m.ExchangeOrderId > 0)
            return $"E:{m.ExchangeOrderId}";
        return $"P:{(int)m.Side}:{m.Price}:{m.Priority}";
    }

    public override string ToString()
    {
        return "PriceLevel OrderFlow Ladder";
    }

    private long NextEventId() => ++_eventSeq;

    private void AppendTape(AggregatedEvent e)
    {
        if (!_tapeByPrice.TryGetValue(e.Price, out var q))
        {
            q = new Queue<TapeEvent>();
            _tapeByPrice[e.Price] = q;
        }

        q.Enqueue(new TapeEvent(e.Id, e.Time, e.Type, e.Volume));
    }

    private static string FormatToken(EventType type, decimal volume)
    {
        var v = volume.ToString("0.##");
        return type switch
        {
            EventType.Add => $"+{v}",
            EventType.Cancel => $"-{v}",
            _ => v
        };
    }

    private bool PassesMinVolumeFilter(decimal volume)
    {
        if (MinVolumeFilter <= 0)
            return true;

        return Math.Abs(volume) >= MinVolumeFilter;
    }

    private bool TryHandleClearButtonClick(RenderControlMouseEventArgs e)
    {
        if (!ShowClearButton)
            return false;

        if (_clearButtonRect == Rectangle.Empty)
            return false;

        if (e.Button != RenderControlMouseButtons.Left)
            return false;

        if (!IsPointInsideRectangle(_clearButtonRect, e.Location))
            return false;

        ClearHistoryState();
        RedrawChart();
        return true;
    }

    private static bool IsPointInsideRectangle(Rectangle rectangle, Point point)
    {
        return point.X >= rectangle.X
            && point.X <= rectangle.X + rectangle.Width
            && point.Y >= rectangle.Y
            && point.Y <= rectangle.Y + rectangle.Height;
    }

    private void ClearHistoryState()
    {
        lock (_sync)
        {
            _activeOrders.Clear();
            _statsByPrice.Clear();
            _tapeByPrice.Clear();
            _pendingReductionsByPrice.Clear();
        }
    }

    private enum EventType
    {
        Add,
        Cancel
    }

    private readonly struct ActiveOrder
    {
        public ActiveOrder(decimal price, MarketDataType side, decimal volume)
        {
            Price = price;
            Side = side;
            Volume = volume;
        }

        public decimal Price { get; }
        public MarketDataType Side { get; }
        public decimal Volume { get; }
    }

    private readonly struct PendingReduction
    {
        public PendingReduction(long id, DateTime time, decimal volume)
        {
            Id = id;
            Time = time;
            Volume = volume;
        }

        public long Id { get; }
        public DateTime Time { get; }
        public decimal Volume { get; }
    }

    private readonly struct AggregatedEvent
    {
        public AggregatedEvent(long id, DateTime time, decimal price, EventType type, decimal volume)
        {
            Id = id;
            Time = time;
            Price = price;
            Type = type;
            Volume = volume;
        }

        public long Id { get; }
        public DateTime Time { get; }
        public decimal Price { get; }
        public EventType Type { get; }
        public decimal Volume { get; }
    }

    private readonly struct TapeEvent
    {
        public TapeEvent(long id, DateTime time, EventType type, decimal volume)
        {
            Id = id;
            Time = time;
            Type = type;
            Volume = volume;
        }

        public long Id { get; }
        public DateTime Time { get; }
        public EventType Type { get; }
        public decimal Volume { get; }
    }

    private sealed class PriceLevelStats
    {
        public decimal AddVolume { get; set; }
        public decimal CancelVolume { get; set; }
        public decimal Total => AddVolume + CancelVolume;
    }

    private readonly struct RenderRow
    {
        public RenderRow(decimal price, PriceLevelStats stats)
        {
            Price = price;
            Stats = stats;
        }

        public decimal Price { get; }
        public PriceLevelStats Stats { get; }
    }
}
