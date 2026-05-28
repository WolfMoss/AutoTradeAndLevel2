using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ATASOrderFlowExporter
{
    /// <summary>
    /// BAR 级订单流 SQLite 存储（WAL 模式，支持读写并发）
    /// </summary>
    public sealed class SqliteBarStore : IDisposable
    {
        private readonly object _lock = new object();
        private readonly string _dbPath;
        private SqliteConnection _connection;
        private bool _schemaReady;

        public SqliteBarStore(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            SqliteBootstrap.EnsureInitialized();
        }

        public void EnsureReady(bool clearOnStart)
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (clearOnStart && File.Exists(_dbPath))
                {
                    CloseConnection();
                    File.Delete(_dbPath);
                    _schemaReady = false;
                }

                EnsureConnectionReady();
            }
        }

        private void EnsureConnectionReady()
        {
            OpenConnection();
            if (_schemaReady)
            {
                return;
            }

            ApplySchema();
            ApplyMigrations();
            _schemaReady = true;
        }

        public void UpsertBar(OrderFlowData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            lock (_lock)
            {
                EnsureConnectionReady();
                using var tx = _connection.BeginTransaction();
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO bars (
    Symbol, BarIndex, Time, LastTime, Hour,
    Open, High, Low, Close, Volume,
    Bid, Ask, Delta, Betweens, MaxDelta, MinDelta,
    OI, MaxOI, MinOI, Ticks,
    POCPrice, POCVolume, POCBid, POCAsk,
    VAH, VAL, VWAP,
    MaxPosDeltaPrice, MaxPosDeltaVolume, MaxNegDeltaPrice, MaxNegDeltaVolume,
    CloseChgPct, VolumeChgPct, DeltaChgPct, MaxDeltaChgPct, MinDeltaChgPct, TicksChgPct,
    POCPriceChgPct, POCVolumeChgPct,
    MaxPosDeltaPriceChgPct, MaxPosDeltaVolumeChgPct,
    MaxNegDeltaPriceChgPct, MaxNegDeltaVolumeChgPct,
    MaxBarAcceleration,
    UpdatedAt
) VALUES (
    $symbol, $barIndex, $time, $lastTime, $hour,
    $open, $high, $low, $close, $volume,
    $bid, $ask, $delta, $betweens, $maxDelta, $minDelta,
    $oi, $maxOi, $minOi, $ticks,
    $pocPrice, $pocVolume, $pocBid, $pocAsk,
    $vah, $val, $vwap,
    $maxPosDeltaPrice, $maxPosDeltaVolume, $maxNegDeltaPrice, $maxNegDeltaVolume,
    $closeChgPct, $volumeChgPct, $deltaChgPct, $maxDeltaChgPct, $minDeltaChgPct, $ticksChgPct,
    $pocPriceChgPct, $pocVolumeChgPct,
    $maxPosDeltaPriceChgPct, $maxPosDeltaVolumeChgPct,
    $maxNegDeltaPriceChgPct, $maxNegDeltaVolumeChgPct,
    $maxBarAcceleration,
    $updatedAt
)
ON CONFLICT(Symbol, BarIndex) DO UPDATE SET
    Time = excluded.Time,
    LastTime = excluded.LastTime,
    Hour = excluded.Hour,
    Open = excluded.Open,
    High = excluded.High,
    Low = excluded.Low,
    Close = excluded.Close,
    Volume = excluded.Volume,
    Bid = excluded.Bid,
    Ask = excluded.Ask,
    Delta = excluded.Delta,
    Betweens = excluded.Betweens,
    MaxDelta = excluded.MaxDelta,
    MinDelta = excluded.MinDelta,
    OI = excluded.OI,
    MaxOI = excluded.MaxOI,
    MinOI = excluded.MinOI,
    Ticks = excluded.Ticks,
    POCPrice = excluded.POCPrice,
    POCVolume = excluded.POCVolume,
    POCBid = excluded.POCBid,
    POCAsk = excluded.POCAsk,
    VAH = excluded.VAH,
    VAL = excluded.VAL,
    VWAP = excluded.VWAP,
    MaxPosDeltaPrice = excluded.MaxPosDeltaPrice,
    MaxPosDeltaVolume = excluded.MaxPosDeltaVolume,
    MaxNegDeltaPrice = excluded.MaxNegDeltaPrice,
    MaxNegDeltaVolume = excluded.MaxNegDeltaVolume,
    CloseChgPct = excluded.CloseChgPct,
    VolumeChgPct = excluded.VolumeChgPct,
    DeltaChgPct = excluded.DeltaChgPct,
    MaxDeltaChgPct = excluded.MaxDeltaChgPct,
    MinDeltaChgPct = excluded.MinDeltaChgPct,
    TicksChgPct = excluded.TicksChgPct,
    POCPriceChgPct = excluded.POCPriceChgPct,
    POCVolumeChgPct = excluded.POCVolumeChgPct,
    MaxPosDeltaPriceChgPct = excluded.MaxPosDeltaPriceChgPct,
    MaxPosDeltaVolumeChgPct = excluded.MaxPosDeltaVolumeChgPct,
    MaxNegDeltaPriceChgPct = excluded.MaxNegDeltaPriceChgPct,
    MaxNegDeltaVolumeChgPct = excluded.MaxNegDeltaVolumeChgPct,
    MaxBarAcceleration = excluded.MaxBarAcceleration,
    UpdatedAt = excluded.UpdatedAt;
";
                    BindBarParameters(cmd, data);
                    cmd.ExecuteNonQuery();
                }

                using (var meta = _connection.CreateCommand())
                {
                    meta.Transaction = tx;
                    meta.CommandText = @"
INSERT INTO feed_meta (Symbol, FormingBarIndex, FormingBarTime, UpdatedAt)
VALUES ($symbol, $barIndex, $time, $updatedAt)
ON CONFLICT(Symbol) DO UPDATE SET
    FormingBarIndex = excluded.FormingBarIndex,
    FormingBarTime = excluded.FormingBarTime,
    UpdatedAt = excluded.UpdatedAt;
";
                    meta.Parameters.AddWithValue("$symbol", data.Symbol ?? "Unknown");
                    meta.Parameters.AddWithValue("$barIndex", data.BarIndex);
                    meta.Parameters.AddWithValue("$time", FormatTime(data.Time));
                    meta.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    meta.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }

        public void UpsertOrderBookLevels(OrderFlowData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.OrderBookLevels == null || data.OrderBookLevels.Count == 0)
            {
                return;
            }

            lock (_lock)
            {
                EnsureConnectionReady();
                using var tx = _connection.BeginTransaction();

                using (var deleteCmd = _connection.CreateCommand())
                {
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = @"
DELETE FROM order_book_levels
WHERE Symbol = $symbol AND BarIndex = $barIndex;
";
                    deleteCmd.Parameters.AddWithValue("$symbol", data.Symbol ?? "Unknown");
                    deleteCmd.Parameters.AddWithValue("$barIndex", data.BarIndex);
                    deleteCmd.ExecuteNonQuery();
                }

                foreach (var level in data.OrderBookLevels)
                {
                    using var insertCmd = _connection.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = @"
INSERT INTO order_book_levels (
    Symbol, BarIndex, Time, LevelPrice, Side, LevelVolume, TickOffset, UpdatedAt
) VALUES (
    $symbol, $barIndex, $time, $levelPrice, $side, $levelVolume, $tickOffset, $updatedAt
);
";
                    insertCmd.Parameters.AddWithValue("$symbol", data.Symbol ?? "Unknown");
                    insertCmd.Parameters.AddWithValue("$barIndex", data.BarIndex);
                    insertCmd.Parameters.AddWithValue("$time", FormatTime(data.Time));
                    insertCmd.Parameters.AddWithValue("$levelPrice", (double)level.Price);
                    insertCmd.Parameters.AddWithValue("$side", level.Side ?? string.Empty);
                    insertCmd.Parameters.AddWithValue("$levelVolume", (double)level.Volume);
                    insertCmd.Parameters.AddWithValue("$tickOffset", level.TickOffset);
                    insertCmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    insertCmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }

        public void Dispose()
        {
            CloseConnection();
        }

        private void OpenConnection()
        {
            if (_connection != null)
            {
                return;
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            _connection = new SqliteConnection(builder.ConnectionString);
            _connection.Open();
        }

        private void CloseConnection()
        {
            if (_connection == null)
            {
                return;
            }

            _connection.Dispose();
            _connection = null;
            _schemaReady = false;
        }

        private void ApplySchema()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS bars (
    Symbol                      TEXT    NOT NULL,
    BarIndex                    INTEGER NOT NULL,
    Time                        TEXT    NOT NULL,
    LastTime                    TEXT,
    Hour                        INTEGER,
    Open                        REAL,
    High                        REAL,
    Low                         REAL,
    Close                       REAL,
    Volume                      REAL,
    Bid                         REAL,
    Ask                         REAL,
    Delta                       REAL,
    Betweens                    REAL,
    MaxDelta                    REAL,
    MinDelta                    REAL,
    OI                          REAL,
    MaxOI                       REAL,
    MinOI                       REAL,
    Ticks                       REAL,
    POCPrice                    REAL,
    POCVolume                   REAL,
    POCBid                      REAL,
    POCAsk                      REAL,
    VAH                         REAL,
    VAL                         REAL,
    VWAP                        REAL,
    MaxPosDeltaPrice            REAL,
    MaxPosDeltaVolume           REAL,
    MaxNegDeltaPrice            REAL,
    MaxNegDeltaVolume           REAL,
    CloseChgPct                 REAL,
    VolumeChgPct                REAL,
    DeltaChgPct                 REAL,
    MaxDeltaChgPct              REAL,
    MinDeltaChgPct              REAL,
    TicksChgPct                 REAL,
    POCPriceChgPct              REAL,
    POCVolumeChgPct             REAL,
    MaxPosDeltaPriceChgPct      REAL,
    MaxPosDeltaVolumeChgPct     REAL,
    MaxNegDeltaPriceChgPct      REAL,
    MaxNegDeltaVolumeChgPct     REAL,
    MaxBarAcceleration          REAL,
    UpdatedAt                   TEXT    NOT NULL,
    PRIMARY KEY (Symbol, BarIndex)
);

CREATE INDEX IF NOT EXISTS idx_bars_symbol_barindex ON bars (Symbol, BarIndex);
CREATE INDEX IF NOT EXISTS idx_bars_symbol_time ON bars (Symbol, Time);

CREATE TABLE IF NOT EXISTS order_book_levels (
    Symbol          TEXT    NOT NULL,
    BarIndex        INTEGER NOT NULL,
    Time            TEXT    NOT NULL,
    LevelPrice      REAL    NOT NULL,
    Side            TEXT    NOT NULL,
    LevelVolume     REAL    NOT NULL,
    TickOffset      INTEGER NOT NULL,
    UpdatedAt       TEXT    NOT NULL,
    PRIMARY KEY (Symbol, BarIndex, LevelPrice, Side)
);

CREATE INDEX IF NOT EXISTS idx_order_book_symbol_barindex ON order_book_levels (Symbol, BarIndex);
CREATE INDEX IF NOT EXISTS idx_order_book_symbol_time ON order_book_levels (Symbol, Time);

CREATE TABLE IF NOT EXISTS feed_meta (
    Symbol              TEXT PRIMARY KEY,
    FormingBarIndex     INTEGER NOT NULL,
    FormingBarTime      TEXT    NOT NULL,
    UpdatedAt           TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS order_events (
    Symbol           TEXT    NOT NULL,
    SignalBarIndex   INTEGER NOT NULL,
    SignalBarTime    TEXT    NOT NULL,
    Side             TEXT    NOT NULL,
    LimitPrice       REAL    NOT NULL,
    TakeProfitPrice  REAL    NOT NULL,
    Mt5Ticket        INTEGER,
    Mt5Retcode       INTEGER,
    Status           TEXT    NOT NULL,
    CreatedAt        TEXT    NOT NULL,
    PRIMARY KEY (Symbol, SignalBarIndex)
);

CREATE TABLE IF NOT EXISTS trade_state (
    Symbol           TEXT PRIMARY KEY,
    State            TEXT NOT NULL,
    Direction        INTEGER NOT NULL DEFAULT 0,
    EntryPrice       REAL,
    SignalTime       TEXT,
    SignalBarIndex   INTEGER,
    PendingTicket    INTEGER,
    UpdatedAt        TEXT NOT NULL
);
";
            cmd.ExecuteNonQuery();
        }

        private void ApplyMigrations()
        {
            EnsureColumn("bars", "MaxBarAcceleration", "REAL");
        }

        private void EnsureColumn(string table, string column, string type)
        {
            if (!TableExists(table))
            {
                return;
            }

            using var check = _connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            alter.ExecuteNonQuery();
        }

        private bool TableExists(string table)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
            cmd.Parameters.AddWithValue("$name", table);
            return cmd.ExecuteScalar() != null;
        }

        private static void BindBarParameters(SqliteCommand cmd, OrderFlowData data)
        {
            cmd.Parameters.AddWithValue("$symbol", data.Symbol ?? "Unknown");
            cmd.Parameters.AddWithValue("$barIndex", data.BarIndex);
            cmd.Parameters.AddWithValue("$time", FormatTime(data.Time));
            cmd.Parameters.AddWithValue("$lastTime", FormatTime(data.LastTime));
            cmd.Parameters.AddWithValue("$hour", data.Hour);
            cmd.Parameters.AddWithValue("$open", (double)data.Open);
            cmd.Parameters.AddWithValue("$high", (double)data.High);
            cmd.Parameters.AddWithValue("$low", (double)data.Low);
            cmd.Parameters.AddWithValue("$close", (double)data.Close);
            cmd.Parameters.AddWithValue("$volume", (double)data.Volume);
            cmd.Parameters.AddWithValue("$bid", (double)data.Bid);
            cmd.Parameters.AddWithValue("$ask", (double)data.Ask);
            cmd.Parameters.AddWithValue("$delta", (double)data.Delta);
            cmd.Parameters.AddWithValue("$betweens", (double)data.Betweens);
            cmd.Parameters.AddWithValue("$maxDelta", (double)data.MaxDelta);
            cmd.Parameters.AddWithValue("$minDelta", (double)data.MinDelta);
            cmd.Parameters.AddWithValue("$oi", (double)data.OI);
            cmd.Parameters.AddWithValue("$maxOi", (double)data.MaxOI);
            cmd.Parameters.AddWithValue("$minOi", (double)data.MinOI);
            cmd.Parameters.AddWithValue("$ticks", (double)data.Ticks);
            cmd.Parameters.AddWithValue("$pocPrice", (double)data.POCPrice);
            cmd.Parameters.AddWithValue("$pocVolume", (double)data.POCVolume);
            cmd.Parameters.AddWithValue("$pocBid", (double)data.POCBid);
            cmd.Parameters.AddWithValue("$pocAsk", (double)data.POCAsk);
            cmd.Parameters.AddWithValue("$vah", (double)data.VAH);
            cmd.Parameters.AddWithValue("$val", (double)data.VAL);
            cmd.Parameters.AddWithValue("$vwap", (double)data.VWAP);
            cmd.Parameters.AddWithValue("$maxPosDeltaPrice", (double)data.MaxPosDeltaPrice);
            cmd.Parameters.AddWithValue("$maxPosDeltaVolume", (double)data.MaxPosDeltaVolume);
            cmd.Parameters.AddWithValue("$maxNegDeltaPrice", (double)data.MaxNegDeltaPrice);
            cmd.Parameters.AddWithValue("$maxNegDeltaVolume", (double)data.MaxNegDeltaVolume);
            cmd.Parameters.AddWithValue("$closeChgPct", ToDbNullable(data.CloseChgPct));
            cmd.Parameters.AddWithValue("$volumeChgPct", ToDbNullable(data.VolumeChgPct));
            cmd.Parameters.AddWithValue("$deltaChgPct", ToDbNullable(data.DeltaChgPct));
            cmd.Parameters.AddWithValue("$maxDeltaChgPct", ToDbNullable(data.MaxDeltaChgPct));
            cmd.Parameters.AddWithValue("$minDeltaChgPct", ToDbNullable(data.MinDeltaChgPct));
            cmd.Parameters.AddWithValue("$ticksChgPct", ToDbNullable(data.TicksChgPct));
            cmd.Parameters.AddWithValue("$pocPriceChgPct", ToDbNullable(data.POCPriceChgPct));
            cmd.Parameters.AddWithValue("$pocVolumeChgPct", ToDbNullable(data.POCVolumeChgPct));
            cmd.Parameters.AddWithValue("$maxPosDeltaPriceChgPct", ToDbNullable(data.MaxPosDeltaPriceChgPct));
            cmd.Parameters.AddWithValue("$maxPosDeltaVolumeChgPct", ToDbNullable(data.MaxPosDeltaVolumeChgPct));
            cmd.Parameters.AddWithValue("$maxNegDeltaPriceChgPct", ToDbNullable(data.MaxNegDeltaPriceChgPct));
            cmd.Parameters.AddWithValue("$maxNegDeltaVolumeChgPct", ToDbNullable(data.MaxNegDeltaVolumeChgPct));
            cmd.Parameters.AddWithValue("$maxBarAcceleration", (double)data.MaxBarAcceleration);
            cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        private static object ToDbNullable(decimal? value)
        {
            return value.HasValue ? (object)(double)value.Value : DBNull.Value;
        }

        private static string FormatTime(DateTime value)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
