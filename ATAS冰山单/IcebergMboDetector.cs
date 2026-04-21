namespace ATAS.Indicators.Technical;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;

using ATAS.DataFeedsCore;
using ATAS.Indicators.Drawing;

using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using DrawingColor = System.Drawing.Color;

/// <summary>
/// 基于 MBO（Market By Order）事件序列，用「限价单撤单/删除后，极短时间内在同价同侧出现量级相近的新单」
/// 作为冰山单补单刷新的启发式信号，并在独立面板用柱状图累计每根 K 线上的命中次数。
/// </summary>
[DisplayName("Iceberg MBO / 冰山单(MBO)")]
[Category(IndicatorCategories.Other)]
[Description("基于MBO撤单后短时同价同侧近似等量补单的冰山启发式 / MBO iceberg refresh heuristic")]
[HelpLink("https://docs.atas.net")]
public class IcebergMboDetector : Indicator
{
    private readonly object _mboLock = new();

    private readonly Dictionary<string, ActiveOrder> _activeOrders = new();

    /// <summary>同价同侧最近一次「可见单被撤/删」时的时间与量级，用于与随后的 New 比对。</summary>
    private readonly Dictionary<(decimal Price, MarketDataType Side), RefillHint> _refillHints = new();
    private readonly List<IcebergMarker> _markers = new();

    // 主图模式下不使用可见数据序列，避免对K线渲染产生影响。
    private readonly ValueDataSeries _dummySeries = new("IcebergDummy")
    {
        VisualType = VisualMode.Hide,
        IsHidden = true
    };

    private DrawingColor _bidColor = DrawingColor.FromArgb(255, 80, 180, 120);
    private DrawingColor _askColor = DrawingColor.FromArgb(255, 220, 100, 100);

    public IcebergMboDetector()
        : base(false)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = false;
        DataSeries[0] = _dummySeries;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Refill window (ms) / 补单时间窗(毫秒)", GroupName = "Detection / 检测", Order = 10)]
    [Range(1, 60000)]
    public int RefillWindowMilliseconds { get; set; } = 400;

    [Display(Name = "Volume tolerance (%) / 量级容差(%)", GroupName = "Detection / 检测", Order = 20)]
    [Range(1, 100)]
    public int VolumeTolerancePercent { get; set; } = 35;

    [Display(Name = "Min visible volume / 最小可见量", GroupName = "Detection / 检测", Order = 30)]
    [Range(0, 1000000000)]
    public decimal MinVisibleVolume { get; set; } = 1m;

    [Display(Name = "Bid color / 买侧颜色", GroupName = "Colors / 颜色", Order = 100)]
    public DrawingColor BidHistogramColor
    {
        get => _bidColor;
        set
        {
            _bidColor = value;
        }
    }

    [Display(Name = "Ask color / 卖侧颜色", GroupName = "Colors / 颜色", Order = 110)]
    public DrawingColor AskHistogramColor
    {
        get => _askColor;
        set
        {
            _askColor = value;
        }
    }

    [Display(Name = "Marker size / 标记大小", GroupName = "Visualization / 可视化", Order = 120)]
    [Range(4, 30)]
    public int MarkerSize { get; set; } = 10;

    protected override void OnInitialize()
    {
        base.OnInitialize();
        _ = SubscribeMboAsync();
    }

    private async Task SubscribeMboAsync()
    {
        try
        {
            await SubscribeMarketByOrderData().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IcebergMboDetector] MBO subscribe failed: {ex}");
        }
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // 历史 K 线无 MBO 回放：保持 0；实时命中在 OnMarketByOrdersChanged 中写入当前柱。
    }

    protected override void OnMarketByOrdersChanged(IEnumerable<MarketByOrder> values)
    {
        base.OnMarketByOrdersChanged(values);

        if (values == null)
            return;

        lock (_mboLock)
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
            {
                _activeOrders.Clear();
                _refillHints.Clear();
            }

            foreach (var m in values)
            {
                switch (m.Type)
                {
                    case MarketByOrderUpdateTypes.Snapshot:
                        IngestSnapshotOrNew(m);
                        break;
                    case MarketByOrderUpdateTypes.New:
                        TryDetectFromNew(m);
                        IngestSnapshotOrNew(m);
                        break;
                    case MarketByOrderUpdateTypes.Change:
                        ApplyChange(m);
                        break;
                    case MarketByOrderUpdateTypes.Delete:
                        ApplyDelete(m);
                        break;
                }
            }
        }
    }

    private void IngestSnapshotOrNew(MarketByOrder m)
    {
        if (m.Side != MarketDataType.Bid && m.Side != MarketDataType.Ask)
            return;

        if (m.Volume < MinVisibleVolume)
            return;

        _activeOrders[GetOrderKey(m)] = new ActiveOrder(m.Price, m.Side, m.Volume);
    }

    private void TryDetectFromNew(MarketByOrder m)
    {
        if (m.Side != MarketDataType.Bid && m.Side != MarketDataType.Ask)
            return;

        if (m.Volume < MinVisibleVolume)
            return;

        var key = (m.Price, m.Side);
        PruneStaleHints(m.Time);

        if (!_refillHints.TryGetValue(key, out var hint))
            return;

        var elapsedMs = (m.Time - hint.EventTime).TotalMilliseconds;
        if (elapsedMs < 0 || elapsedMs > RefillWindowMilliseconds)
        {
            _refillHints.Remove(key);
            return;
        }

        if (!IsSimilarVolume(hint.LastVolume, m.Volume))
            return;

        var bar = BarIndexForTime(m.Time);
        _markers.Add(new IcebergMarker(bar, m.Price, m.Side, m.Time));
        TrimMarkers();

        _refillHints.Remove(key);
    }

    private void ApplyChange(MarketByOrder m)
    {
        var id = GetOrderKey(m);

        if (!_activeOrders.TryGetValue(id, out var prev))
        {
            if (m.Volume >= MinVisibleVolume && (m.Side == MarketDataType.Bid || m.Side == MarketDataType.Ask))
                _activeOrders[id] = new ActiveOrder(m.Price, m.Side, m.Volume);
            return;
        }

        if (m.Volume <= 0)
        {
            RegisterRefillHint(prev.Price, prev.Side, prev.Volume, m.Time);
            _activeOrders.Remove(id);
            return;
        }

        _activeOrders[id] = new ActiveOrder(prev.Price, prev.Side, m.Volume);
    }

    private void ApplyDelete(MarketByOrder m)
    {
        var id = GetOrderKey(m);

        if (!_activeOrders.TryGetValue(id, out var prev))
            return;

        if (prev.Volume >= MinVisibleVolume)
            RegisterRefillHint(prev.Price, prev.Side, prev.Volume, m.Time);

        _activeOrders.Remove(id);
    }

    private void RegisterRefillHint(decimal price, MarketDataType side, decimal lastVolume, DateTime eventTime)
    {
        if (lastVolume < MinVisibleVolume)
            return;

        _refillHints[(price, side)] = new RefillHint(eventTime, lastVolume);
    }

    private void PruneStaleHints(DateTime referenceTime)
    {
        if (_refillHints.Count == 0)
            return;

        var maxMs = RefillWindowMilliseconds;
        var remove = new List<(decimal Price, MarketDataType Side)>();
        foreach (var kv in _refillHints)
        {
            if ((referenceTime - kv.Value.EventTime).TotalMilliseconds > maxMs)
                remove.Add(kv.Key);
        }

        foreach (var k in remove)
            _refillHints.Remove(k);
    }

    private bool IsSimilarVolume(decimal a, decimal b)
    {
        if (a <= 0 || b <= 0)
            return false;

        var max = Math.Max(a, b);
        var tol = VolumeTolerancePercent / 100m;
        return Math.Abs(a - b) <= max * tol;
    }

    private int BarIndexForTime(DateTime eventTime)
    {
        var last = CurrentBar - 1;
        if (last < 0)
            return 0;

        for (var i = last; i >= 0; i--)
        {
            var c = GetCandle(i);
            if (c == null)
                continue;

            if (eventTime < c.Time)
                continue;

            if (i < last)
            {
                var next = GetCandle(i + 1);
                if (next != null && eventTime >= next.Time)
                    continue;
            }

            return i;
        }

        return 0;
    }

    private static string GetOrderKey(MarketByOrder m)
    {
        if (m.ExchangeOrderId > 0)
            return $"E:{m.ExchangeOrderId}";

        // 某些连接/阶段可能不给 ExchangeOrderId，使用 side+price+priority 作为降级键。
        return $"P:{(int)m.Side}:{m.Price}:{m.Priority}";
    }

    private void TrimMarkers()
    {
        // 限制内存占用，保留最近的标记
        const int maxMarkers = 5000;
        if (_markers.Count <= maxMarkers)
            return;

        _markers.RemoveRange(0, _markers.Count - maxMarkers);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _markers.Count == 0)
            return;

        var markerHalf = Math.Max(2, MarkerSize / 2);

        foreach (var marker in _markers)
        {
            if (marker.Bar < 0 || marker.Bar >= CurrentBar)
                continue;

            var x = ChartInfo.GetXByBar(marker.Bar);
            var y = ChartInfo.GetYByPrice(marker.Price);
            var color = marker.Side == MarketDataType.Bid ? _bidColor : _askColor;
            var points = marker.Side == MarketDataType.Bid
                ? new[]
                {
                    new Point(x, y - markerHalf),
                    new Point(x - markerHalf, y + markerHalf),
                    new Point(x + markerHalf, y + markerHalf)
                }
                : new[]
                {
                    new Point(x, y + markerHalf),
                    new Point(x - markerHalf, y - markerHalf),
                    new Point(x + markerHalf, y - markerHalf)
                };

            context.FillPolygon(color, points);
        }
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

    private readonly struct RefillHint
    {
        public RefillHint(DateTime eventTime, decimal lastVolume)
        {
            EventTime = eventTime;
            LastVolume = lastVolume;
        }

        public DateTime EventTime { get; }
        public decimal LastVolume { get; }
    }

    private readonly struct IcebergMarker
    {
        public IcebergMarker(int bar, decimal price, MarketDataType side, DateTime eventTime)
        {
            Bar = bar;
            Price = price;
            Side = side;
            EventTime = eventTime;
        }

        public int Bar { get; }
        public decimal Price { get; }
        public MarketDataType Side { get; }
        public DateTime EventTime { get; }
    }

    public override string ToString()
    {
        return $"Iceberg MBO ({RefillWindowMilliseconds}ms, ±{VolumeTolerancePercent}%)";
    }
}
