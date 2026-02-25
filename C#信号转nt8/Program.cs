using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;

namespace SignalForwarder;

public class Program
{
    private static readonly Dictionary<string, TickerMappingInfo> DefaultTickerMapping = new()
    {
        { "GC", new TickerMappingInfo { TargetTicker = "MGC", Quantity = 1 } } // 黄金：GC -> MGC，默认1手
    };

    private static Dictionary<string, TickerMappingInfo> _tickerMapping = new(DefaultTickerMapping);
    private static string _appDir = string.Empty;
    private static string _configFilePath = string.Empty;
    private static string _accountConfigFilePath = string.Empty;
    private static string _logDir = string.Empty;
    private static List<AccountConfig> _accountConfigs = new();
    private static NinjaTraderOrderService? _orderService;
    private static FileSystemWatcher? _configFileWatcher;
    private static FileSystemWatcher? _accountConfigFileWatcher;
    private static ILogger? _globalLogger;
    private static DateTime _lastTickerConfigChange = DateTime.MinValue;
    private static DateTime _lastAccountConfigChange = DateTime.MinValue;

    public static void Main(string[] args)
    {
        // 设置控制台编码为 UTF-8，解决 Windows Server 上中文显示为问号的问题
        try
        {
            // 设置控制台输出和输入编码为 UTF-8
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 如果设置失败（某些环境不支持），忽略错误
        }

        // 获取应用目录
        _appDir = AppContext.BaseDirectory;
        _configFilePath = Path.Combine(_appDir, "ticker_mapping.txt");
        _accountConfigFilePath = Path.Combine(_appDir, "account_config.txt");
        _logDir = Path.Combine(_appDir, "logs");
        Directory.CreateDirectory(_logDir);

        var builder = WebApplication.CreateBuilder(args);

        // 配置日志
        var logFilePath = Path.Combine(_logDir, $"webhook_{DateTime.Now:yyyyMMdd}.log");
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddProvider(new FileLoggerProvider(logFilePath));
        
        // 禁用 ASP.NET Core 的请求日志（Request starting/finished 和 Executing endpoint）
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);

        var app = builder.Build();

        // 保存全局 logger 引用
        _globalLogger = app.Logger;

        // 加载品种映射配置
        LoadTickerMapping(app.Logger);

        // 加载账号配置
        LoadAccountConfig(app.Logger);

        // 初始化 NinjaTrader 订单服务
        InitializeOrderService(app.Logger);

        // 启动配置文件监控（实时重载）
        StartConfigFileWatcher(app.Logger);

        // 配置路由
        app.MapPost("/webhook", WebhookListener); // TradingView 信号接收
        app.MapPost("/tick", TickWebhookListener); // NT8 Tick 数据接收

        // 配置 Kestrel 端点（从配置文件或环境变量读取端口号）
        var httpPort = GetHttpPort(app.Logger);
        app.Urls.Clear();
        app.Urls.Add($"http://0.0.0.0:{httpPort}");

        // 显示启动信息
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("🚀 信号转发服务已启动");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"📡 Webhook接口地址:");
        Console.WriteLine($"   http://0.0.0.0:{httpPort}/webhook (TradingView 信号)");
        Console.WriteLine($"   http://0.0.0.0:{httpPort}/tick (NT8 Tick 数据)");
        Console.WriteLine($"\n📁 配置文件路径: {_configFilePath}");
        Console.WriteLine($"📝 日志文件路径: {_logDir}");
        Console.WriteLine(new string('=', 60) + "\n");

        app.Logger.LogInformation(new string('=', 60));
        app.Logger.LogInformation("信号转发服务已启动");
        app.Logger.LogInformation($"配置文件路径: {_configFilePath}");
        app.Logger.LogInformation(new string('=', 60));

        app.Run();
    }

    private static void LoadTickerMapping(ILogger logger)
    {
        // 先使用默认配置
        _tickerMapping = new Dictionary<string, TickerMappingInfo>(DefaultTickerMapping);

        // 尝试从配置文件加载
        var fileMapping = new Dictionary<string, TickerMappingInfo>();
        if (File.Exists(_configFilePath))
        {
            try
            {
                var lines = File.ReadAllLines(_configFilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    // 跳过空行和注释行
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                        continue;

                    // 解析 "源品种=目标品种,手数" 或 "源品种=目标品种" 格式
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var source = parts[0].Trim().ToUpperInvariant();
                        var targetPart = parts[1].Trim();
                        
                        if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(targetPart))
                        {
                            // 解析目标品种和手数
                            var targetParts = targetPart.Split(',');
                            var targetTicker = targetParts[0].Trim();
                            var quantity = 1; // 默认手数为1
                            
                            // 如果提供了手数，解析手数
                            if (targetParts.Length >= 2)
                            {
                                if (!int.TryParse(targetParts[1].Trim(), out quantity) || quantity <= 0)
                                {
                                    logger.LogWarning("配置文件第{LineNum}行手数格式错误，使用默认手数1: {Line}", i + 1, line);
                                    quantity = 1;
                                }
                            }
                            
                            if (!string.IsNullOrEmpty(targetTicker))
                            {
                                fileMapping[source] = new TickerMappingInfo
                                {
                                    TargetTicker = targetTicker,
                                    Quantity = quantity
                                };
                            }
                            else
                            {
                                logger.LogWarning("配置文件第{LineNum}行目标品种为空，已跳过: {Line}", i + 1, line);
                            }
                        }
                        else
                        {
                            logger.LogWarning("配置文件第{LineNum}行格式错误，已跳过: {Line}", i + 1, line);
                        }
                    }
                    else
                    {
                        logger.LogWarning("配置文件第{LineNum}行格式错误（缺少=），已跳过: {Line}", i + 1, line);
                    }
                }

                if (fileMapping.Count > 0)
                {
                    foreach (var kvp in fileMapping)
                    {
                        _tickerMapping[kvp.Key] = kvp.Value;
                    }
                    logger.LogInformation("已从配置文件加载品种映射: {Mapping}", 
                        string.Join(", ", fileMapping.Select(kvp => $"{kvp.Key}={kvp.Value.TargetTicker},{kvp.Value.Quantity}手")));
                }
                else
                {
                    logger.LogInformation("配置文件 {ConfigPath} 存在但为空，使用默认配置", _configFilePath);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "读取配置文件失败，使用默认配置");
            }
        }
        else
        {
            logger.LogInformation("配置文件 {ConfigPath} 不存在，使用默认配置", _configFilePath);
        }

        // 环境变量作为备用（保持向后兼容，只支持品种映射，不支持手数）
        var envMappingStr = Environment.GetEnvironmentVariable("TICKER_MAPPING");
        if (!string.IsNullOrEmpty(envMappingStr))
        {
            try
            {
                var envMapping = JsonConvert.DeserializeObject<Dictionary<string, string>>(envMappingStr);
                if (envMapping != null)
                {
                    foreach (var kvp in envMapping)
                    {
                        var source = kvp.Key.ToUpperInvariant();
                        if (!_tickerMapping.ContainsKey(source))
                        {
                            _tickerMapping[source] = new TickerMappingInfo
                            {
                                TargetTicker = kvp.Value,
                                Quantity = 1 // 环境变量不支持手数配置，默认1手
                            };
                        }
                    }
                    logger.LogInformation("已从环境变量加载品种映射: {Mapping}",
                        string.Join(", ", envMapping.Select(kvp => $"{kvp.Key}={kvp.Value}")));
                }
            }
            catch (JsonException e)
            {
                logger.LogError(e, "解析环境变量品种映射配置失败，忽略环境变量配置");
            }
        }

        logger.LogInformation("最终品种映射配置: {Mapping}",
            string.Join(", ", _tickerMapping.Select(kvp => $"{kvp.Key}={kvp.Value.TargetTicker},{kvp.Value.Quantity}手")));
    }

    private static TickerMappingInfo? MapTicker(string? ticker, ILogger logger)
    {
        if (string.IsNullOrEmpty(ticker) || ticker == "未知品种")
            return new TickerMappingInfo { TargetTicker = ticker ?? string.Empty, Quantity = 1 };

        var tickerUpper = ticker.ToUpperInvariant();
        if (_tickerMapping.TryGetValue(tickerUpper, out var mappingInfo))
        {
            if (mappingInfo.TargetTicker != ticker)
            {
                logger.LogInformation("品种映射: {Ticker} -> {MappedTicker} (手数: {Quantity})", 
                    ticker, mappingInfo.TargetTicker, mappingInfo.Quantity);
            }
            return mappingInfo;
        }

        // 如果没有映射，返回原品种，默认1手
        return new TickerMappingInfo { TargetTicker = ticker, Quantity = 1 };
    }

    private static void LoadAccountConfig(ILogger logger)
    {
        _accountConfigs.Clear();

        if (!File.Exists(_accountConfigFilePath))
        {
            logger.LogInformation("账号配置文件 {ConfigPath} 不存在，跳过账号配置加载", _accountConfigFilePath);
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_accountConfigFilePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                // 跳过空行和注释行
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;

                // 解析 "账号=订单类型,是否启用" 格式（手数已移至 ticker_mapping.txt）
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                {
                    var account = parts[0].Trim();
                    var configParts = parts[1].Split(',');
                    
                    if (configParts.Length >= 1)
                    {
                        var config = new AccountConfig
                        {
                            Account = account,
                            Quantity = 1, // 手数不再从账号配置读取，由品种映射配置提供
                            OrderType = configParts[0].Trim()
                        };

                        // 可选的启用/禁用标志
                        if (configParts.Length >= 2)
                        {
                            config.Enabled = bool.TryParse(configParts[1].Trim(), out var enabled) ? enabled : true;
                        }

                        if (!string.IsNullOrEmpty(config.Account))
                        {
                            _accountConfigs.Add(config);
                            logger.LogInformation("已加载账号配置: {Account}, 类型={OrderType}, 启用={Enabled} (手数由品种映射配置)",
                                config.Account, config.OrderType, config.Enabled);
                        }
                        else
                        {
                            logger.LogWarning("配置文件第{LineNum}行账号名称为空，已跳过: {Line}", i + 1, line);
                        }
                    }
                    else
                    {
                        logger.LogWarning("配置文件第{LineNum}行格式错误，已跳过: {Line}", i + 1, line);
                    }
                }
                else
                {
                    logger.LogWarning("配置文件第{LineNum}行格式错误（缺少=），已跳过: {Line}", i + 1, line);
                }
            }

            logger.LogInformation("已加载 {Count} 个账号配置", _accountConfigs.Count);
        }
        catch (Exception e)
        {
            logger.LogError(e, "读取账号配置文件失败");
        }
    }

    /// <summary>
    /// 从配置文件或环境变量读取 HTTP 端口号
    /// </summary>
    private static int GetHttpPort(ILogger logger)
    {
        const int defaultPort = 8500;
        var configFilePath = Path.Combine(_appDir, "app_config.txt");

        // 1. 优先从配置文件读取
        if (File.Exists(configFilePath))
        {
            try
            {
                var lines = File.ReadAllLines(configFilePath);
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    // 跳过空行和注释行
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#'))
                        continue;

                    // 解析 "port=端口号" 格式
                    if (trimmedLine.StartsWith("port=", StringComparison.OrdinalIgnoreCase))
                    {
                        var portStr = trimmedLine.Substring(5).Trim();
                        if (int.TryParse(portStr, out var port) && port > 0 && port <= 65535)
                        {
                            logger.LogInformation("从配置文件读取端口号: {Port}", port);
                            return port;
                        }
                        else
                        {
                            logger.LogWarning("配置文件中的端口号格式错误: {PortStr}，使用默认端口 {DefaultPort}", portStr, defaultPort);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "读取配置文件失败，使用默认端口 {DefaultPort}", defaultPort);
            }
        }

        // 2. 从环境变量读取
        var envPort = Environment.GetEnvironmentVariable("HTTP_PORT");
        if (!string.IsNullOrEmpty(envPort))
        {
            if (int.TryParse(envPort, out var port) && port > 0 && port <= 65535)
            {
                logger.LogInformation("从环境变量读取端口号: {Port}", port);
                return port;
            }
            else
            {
                logger.LogWarning("环境变量中的端口号格式错误: {EnvPort}，使用默认端口 {DefaultPort}", envPort, defaultPort);
            }
        }

        // 3. 使用默认端口
        logger.LogInformation("使用默认端口号: {Port}", defaultPort);
        return defaultPort;
    }

    private static async Task WebhookListener(HttpContext context, ILogger<Program> logger)
    {
        // 启用请求体缓冲，允许在返回响应后继续读取
        context.Request.EnableBuffering();
        
        // 先读取请求体（请求体只能读取一次）
        string jsonText;
        try
        {
            // 重置流位置，确保从头读取
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            jsonText = await reader.ReadToEndAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "❌ 读取请求体失败");
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            var errorResponse = JsonConvert.SerializeObject(new { status = "error", message = "Failed to read request body" });
            await context.Response.WriteAsync(errorResponse);
            return;
        }

        // 立即返回成功响应，不等待处理完成
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        var successResponse = JsonConvert.SerializeObject(new { status = "success", message = "Signal received" });
        await context.Response.WriteAsync(successResponse);

        // 在后台异步处理信号逻辑（不阻塞响应）
        _ = Task.Run(async () =>
        {
            try
            {
                // 记录原始数据
                logger.LogInformation(new string('=', 50));
                logger.LogInformation("【收到新信号】: {Data}", jsonText);

                var data = JObject.Parse(jsonText);

                // 解析字段
                var ticker = data["ticker"]?.ToString() ?? "未知品种";
                var action = data["action"]?.ToString() ?? "无动作";
                var priceToken = data["price"];
                var intervalToken = data["interval"];

                // 处理价格
                double? priceValue = null;
                if (priceToken != null && priceToken.Type != JTokenType.Null)
                {
                    var priceStr = priceToken.ToString();
                    if (priceStr != "未知价格" && double.TryParse(priceStr, out var price))
                    {
                        priceValue = price;
                    }
                }

                // 处理周期
                int? intervalValue = null;
                if (intervalToken != null && intervalToken.Type != JTokenType.Null)
                {
                    var intervalStr = intervalToken.ToString();
                    if (int.TryParse(intervalStr, out var interval))
                    {
                        intervalValue = interval;
                    }
                }

                // 应用品种映射
                var mappingInfo = MapTicker(ticker, logger);
                var mappedTicker = mappingInfo?.TargetTicker ?? ticker;
                var quantity = mappingInfo?.Quantity ?? 1;

                var signalData = new
                {
                    Ticker = mappedTicker,
                    Action = action,
                    Price = priceValue,
                    Interval = intervalValue
                };

                // 记录原始逻辑
                if (action == "buy" || action == "sell")
                {
                    logger.LogInformation("{Emoji} 触发{ActionName}逻辑 -> 品种={Ticker} -> {MappedTicker}, 手数={Quantity}, 周期={Interval}分钟, 动作={Action}, 价格: {Price}",
                        action == "buy" ? "🚀" : "🔻",
                        action == "buy" ? "买入" : "卖出",
                        ticker, mappedTicker, quantity, intervalValue?.ToString() ?? "未知", action, priceValue?.ToString() ?? "未知");
                }
                else
                {
                    logger.LogWarning("⚠️ 收到未知动作: {Action}", action);
                }

                // NinjaTrader 下单任务
                if (_orderService != null && (action == "buy" || action == "sell"))
                {
                    try
                    {
                        _orderService.ProcessSignal(mappedTicker, action, priceValue, quantity);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "发送订单失败");
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "❌ 异步处理信号时发生错误");
            }
        });
    }

    /// <summary>
    /// 接收 NT8 Tick 数据的 Webhook 接口
    /// </summary>
    private static async Task TickWebhookListener(HttpContext context, ILogger<Program> logger)
    {
        // 启用请求体缓冲，允许在返回响应后继续读取
        context.Request.EnableBuffering();
        
        // 先读取请求体（请求体只能读取一次）
        string jsonText;
        try
        {
            // 重置流位置，确保从头读取
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            jsonText = await reader.ReadToEndAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "❌ 读取 Tick 请求体失败");
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            var errorResponse = JsonConvert.SerializeObject(new { status = "error", message = "Failed to read request body" });
            await context.Response.WriteAsync(errorResponse);
            return;
        }

        // 立即返回成功响应，不等待处理完成
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        var successResponse = JsonConvert.SerializeObject(new { status = "success", message = "Tick received" });
        await context.Response.WriteAsync(successResponse);

        // 在后台异步处理 Tick 数据逻辑（不阻塞响应）
        _ = Task.Run(async () =>
        {
            try
            {
                var data = JObject.Parse(jsonText);

                // 解析 tick 数据
                var instrument = data["Instrument"]?.ToString() ?? "未知";
                var price = data["Price"]?.ToObject<double?>();
                var volume = data["Volume"]?.ToObject<int?>();
                var time = data["Time"]?.ToString() ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var marketDataType = data["MarketDataType"]?.ToString() ?? "未知";
                var direction = data["Direction"]?.ToString() ?? "未知";

                logger.LogInformation("收到 Tick 数据: 品种={Instrument}, 价格={Price}, 成交量={Volume}, 时间={Time}, 类型={MarketDataType}, 方向={Direction}",
                    instrument, price, volume, time, marketDataType, direction);

                // 可以在这里添加 tick 数据的处理逻辑
                // 例如：存储到数据库、触发其他逻辑等
            }
            catch (Exception e)
            {
                logger.LogError(e, "❌ 异步处理 Tick 数据时发生错误");
            }
        });
    }

    private static void InitializeOrderService(ILogger logger)
    {
        if (_accountConfigs.Count > 0)
        {
            _orderService = new NinjaTraderOrderService(logger, _accountConfigs);
            _orderService.Initialize();
            _orderService.Connect();
        }
    }

    private static void StartConfigFileWatcher(ILogger logger)
    {
        try
        {
            // 监控品种映射配置文件
            if (File.Exists(_configFilePath))
            {
                _configFileWatcher = new FileSystemWatcher(Path.GetDirectoryName(_configFilePath)!, Path.GetFileName(_configFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _configFileWatcher.Changed += (sender, e) =>
                {
                    // 防抖：避免短时间内多次触发
                    var now = DateTime.Now;
                    if ((now - _lastTickerConfigChange).TotalSeconds < 2)
                        return;
                    _lastTickerConfigChange = now;

                    // 延迟一下，确保文件写入完成
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        try
                        {
                            if (_globalLogger != null && File.Exists(_configFilePath))
                            {
                                _globalLogger.LogInformation("检测到品种映射配置文件变化，重新加载配置...");
                                LoadTickerMapping(_globalLogger);
                            }
                        }
                        catch (Exception ex)
                        {
                            _globalLogger?.LogError(ex, "重新加载品种映射配置失败");
                        }
                    });
                };

                logger.LogInformation("已启动品种映射配置文件监控: {ConfigPath}", _configFilePath);
            }

            // 监控账号配置文件
            if (File.Exists(_accountConfigFilePath))
            {
                _accountConfigFileWatcher = new FileSystemWatcher(Path.GetDirectoryName(_accountConfigFilePath)!, Path.GetFileName(_accountConfigFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _accountConfigFileWatcher.Changed += (sender, e) =>
                {
                    // 防抖：避免短时间内多次触发
                    var now = DateTime.Now;
                    if ((now - _lastAccountConfigChange).TotalSeconds < 2)
                        return;
                    _lastAccountConfigChange = now;

                    // 延迟一下，确保文件写入完成
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        try
                        {
                            if (_globalLogger != null && File.Exists(_accountConfigFilePath))
                            {
                                _globalLogger.LogInformation("检测到账号配置文件变化，重新加载配置...");
                                LoadAccountConfig(_globalLogger);
                                
                                // 更新订单服务的账号配置
                                if (_orderService != null)
                                {
                                    _orderService.UpdateAccounts(_accountConfigs);
                                }
                                else if (_accountConfigs.Count > 0)
                                {
                                    // 如果订单服务不存在但配置了账号，则初始化
                                    InitializeOrderService(_globalLogger);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _globalLogger?.LogError(ex, "重新加载账号配置失败");
                        }
                    });
                };

                logger.LogInformation("已启动账号配置文件监控: {ConfigPath}", _accountConfigFilePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动配置文件监控失败");
        }
    }
}

