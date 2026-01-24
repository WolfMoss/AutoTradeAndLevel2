from flask import Flask, request, jsonify
import logging
import os
import sys
from datetime import datetime
import asyncio
import websockets
import json
import threading
import socket

# 初始化 Flask 应用
app = Flask(__name__)

# WebSocket配置
WS_HOST = os.getenv('WS_HOST', '0.0.0.0')
WS_PORT = int(os.getenv('WS_PORT', 9528))
WS_ENABLED = os.getenv('WS_ENABLED', 'true').lower() == 'true'

# 品种转发映射配置
# 配置文件路径：ticker_mapping.txt（与app.py同目录）
# 配置文件格式：每行一个映射，格式为 "源品种=目标品种"，例如：
# GC=MGC
# CL=MCL
# ES=MES
# 支持注释（以#开头的行为注释）
_default_ticker_mapping = {
    'GC': 'MGC',  # 黄金：GC -> MGC
    # 可以在这里添加更多默认映射
}

_ticker_mapping = _default_ticker_mapping.copy()

# 获取应用根目录（支持PyInstaller打包后的EXE）
def get_app_dir():
    """获取应用程序所在目录（支持打包后的EXE）"""
    if getattr(sys, 'frozen', False):
        # 打包成EXE后，使用EXE所在目录
        return os.path.dirname(sys.executable)
    else:
        # 开发环境，使用脚本所在目录
        return os.path.dirname(os.path.abspath(__file__))

_app_dir = get_app_dir()
_config_file_path = os.path.join(_app_dir, 'ticker_mapping.txt')

# 配置日志记录到文件
log_dir = os.path.join(_app_dir, 'logs')
os.makedirs(log_dir, exist_ok=True)

# 日志文件名包含日期
log_filename = os.path.join(log_dir, f'webhook_{datetime.now().strftime("%Y%m%d")}.log')

# 配置日志格式
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    datefmt='%Y-%m-%d %H:%M:%S',
    handlers=[
        logging.FileHandler(log_filename, encoding='utf-8'),
        logging.StreamHandler()  # 同时输出到控制台
    ]
)

logger = logging.getLogger(__name__)

def load_ticker_mapping():
    """
    从配置文件加载品种映射配置
    优先从 ticker_mapping.txt 文件加载，如果文件不存在则使用默认配置
    也支持环境变量 TICKER_MAPPING（JSON格式）作为备用
    """
    global _ticker_mapping
    
    # 先使用默认配置
    _ticker_mapping = _default_ticker_mapping.copy()
    
    # 尝试从配置文件加载
    file_mapping = {}
    if os.path.exists(_config_file_path):
        try:
            with open(_config_file_path, 'r', encoding='utf-8') as f:
                for line_num, line in enumerate(f, 1):
                    line = line.strip()
                    # 跳过空行和注释行
                    if not line or line.startswith('#'):
                        continue
                    
                    # 解析 "源品种=目标品种" 格式
                    if '=' in line:
                        parts = line.split('=', 1)
                        if len(parts) == 2:
                            source = parts[0].strip().upper()
                            target = parts[1].strip()
                            if source and target:
                                file_mapping[source] = target
                            else:
                                logger.warning(f"配置文件第{line_num}行格式错误，已跳过: {line}")
                        else:
                            logger.warning(f"配置文件第{line_num}行格式错误，已跳过: {line}")
                    else:
                        logger.warning(f"配置文件第{line_num}行格式错误（缺少=），已跳过: {line}")
            
            if file_mapping:
                _ticker_mapping.update(file_mapping)  # 文件配置覆盖默认配置
                logger.info(f"已从配置文件加载品种映射: {file_mapping}")
            else:
                logger.info(f"配置文件 {_config_file_path} 存在但为空，使用默认配置")
        except Exception as e:
            logger.error(f"读取配置文件失败: {e}，使用默认配置")
    else:
        logger.info(f"配置文件 {_config_file_path} 不存在，使用默认配置")
    
    # 环境变量作为备用（如果设置了环境变量，会覆盖文件配置）
    env_mapping_str = os.getenv('TICKER_MAPPING', '')
    if env_mapping_str:
        try:
            env_mapping = json.loads(env_mapping_str)
            _ticker_mapping.update(env_mapping)  # 环境变量覆盖文件配置
            logger.info(f"已从环境变量加载品种映射: {env_mapping}")
        except json.JSONDecodeError as e:
            logger.error(f"解析环境变量品种映射配置失败: {e}，忽略环境变量配置")
    
    logger.info(f"最终品种映射配置: {_ticker_mapping}")

def map_ticker(ticker):
    """
    根据配置映射品种名称
    如果ticker在映射表中，返回映射后的名称；否则返回原名称
    """
    if not ticker or ticker == '未知品种':
        return ticker
    
    # 转换为大写进行匹配（不区分大小写）
    ticker_upper = ticker.upper()
    mapped_ticker = _ticker_mapping.get(ticker_upper, ticker)
    
    if mapped_ticker != ticker:
        logger.info(f"品种映射: {ticker} -> {mapped_ticker}")
    
    return mapped_ticker

# WebSocket服务器连接池（存储所有连接的ATAS客户端）
_ws_clients = set()
_ws_lock = threading.Lock()
_ws_server = None
_ws_server_started = False  # 防止重复启动
_broadcast_loop = None  # 复用的事件循环，避免每次创建
_broadcast_loop_thread = None

async def register_client(websocket):
    """
    注册WebSocket客户端连接
    兼容新版本 websockets 库（不需要 path 参数）
    """
    global _ws_clients
    logger.info(f"新的WebSocket客户端已连接: {websocket.remote_address}")
    with _ws_lock:
        _ws_clients.add(websocket)
    
    try:
        # 保持连接，等待客户端断开
        await websocket.wait_closed()
    except Exception as e:
        logger.error(f"WebSocket客户端连接错误: {e}")
    finally:
        with _ws_lock:
            _ws_clients.discard(websocket)
        logger.info("WebSocket客户端已断开")

async def broadcast_signal(signal_data):
    """
    向所有连接的WebSocket客户端广播信号
    """
    global _ws_clients
    if not WS_ENABLED:
        return
    
    message = json.dumps(signal_data, ensure_ascii=False)
    disconnected = set()
    
    # 复制客户端列表，减少锁持有时间
    clients_copy = None
    with _ws_lock:
        if len(_ws_clients) == 0:
            return
        clients_copy = list(_ws_clients)
    
    # 在锁外进行网络IO操作，提高并发性能
    for client in clients_copy:
        try:
            await client.send(message)
        except Exception as e:
            logger.warning(f"向客户端发送失败，将移除连接: {e}")
            disconnected.add(client)
    
    # 移除断开的连接
    if disconnected:
        with _ws_lock:
            _ws_clients -= disconnected

def _init_broadcast_loop():
    """
    初始化并启动广播事件循环（在独立线程中运行）
    """
    global _broadcast_loop, _broadcast_loop_thread
    
    def run_loop():
        global _broadcast_loop
        _broadcast_loop = asyncio.new_event_loop()
        asyncio.set_event_loop(_broadcast_loop)
        _broadcast_loop.run_forever()
    
    if _broadcast_loop_thread is None or not _broadcast_loop_thread.is_alive():
        _broadcast_loop_thread = threading.Thread(target=run_loop, daemon=True)
        _broadcast_loop_thread.start()
        # 等待事件循环初始化
        import time
        time.sleep(0.1)

def broadcast_signal_async(signal_data):
    """
    异步广播信号的包装函数
    使用复用的事件循环，避免每次创建新循环的开销
    """
    global _broadcast_loop
    
    if not WS_ENABLED:
        return
    
    try:
        # 确保事件循环已初始化
        if _broadcast_loop is None:
            _init_broadcast_loop()
        
        # 使用复用的事件循环提交任务
        if _broadcast_loop is not None and _broadcast_loop.is_running():
            asyncio.run_coroutine_threadsafe(broadcast_signal(signal_data), _broadcast_loop)
        else:
            # 降级方案：如果事件循环不可用，使用临时循环
            loop = asyncio.new_event_loop()
            asyncio.set_event_loop(loop)
            loop.run_until_complete(broadcast_signal(signal_data))
            loop.close()
    except Exception as e:
        logger.error(f"广播信号时出错: {e}")

def start_websocket_server():
    """
    启动WebSocket服务器
    防止在Flask重载器主进程中重复启动（重载器会启动子进程，只在子进程中启动一次）
    """
    global _ws_server, _ws_server_started
    
    # 防止重复启动
    if _ws_server_started:
        logger.warning("WebSocket服务器已经启动，跳过重复启动")
        return
    
    if not WS_ENABLED:
        logger.info("WebSocket功能已禁用")
        return
    
    async def server():
        try:
            async with websockets.serve(register_client, WS_HOST, WS_PORT):
                logger.info(f"WebSocket服务器已启动，监听 {WS_HOST}:{WS_PORT}")
                await asyncio.Future()  # 永久运行
        except OSError as e:
            if e.errno == 10048:  # Windows: 端口已被占用
                logger.error(f"端口 {WS_PORT} 已被占用，请检查是否有其他程序在使用该端口，或修改 WS_PORT 环境变量")
            else:
                logger.error(f"启动WebSocket服务器失败: {e}")
        except Exception as e:
            logger.error(f"WebSocket服务器启动错误: {e}", exc_info=True)
    
    def run_server():
        try:
            asyncio.run(server())
        except Exception as e:
            logger.error(f"WebSocket服务器线程错误: {e}", exc_info=True)
    
    _ws_server_started = True
    server_thread = threading.Thread(target=run_server, daemon=True)
    server_thread.start()
    logger.info("WebSocket服务器线程已启动")

@app.route('/webhook', methods=['POST'])
def webhook_listener():
    """
    这是接收 TradingView 信号的核心函数
    """
    try:
        # 1. 获取 JSON 数据
        # TradingView 发送的是 content-type: application/json
        data = request.json
        
        # 记录原始数据
        logger.info("=" * 50)
        logger.info(f"【收到新信号】: {data}")

        # 2. 解析你在 TradingView 里定义的字段
        # 注意：这里的键名 (Key) 必须和你截图里 JSON 的键名完全一致
        ticker = data.get('ticker', '未知品种')
        action = data.get('action', '无动作')
        price = data.get('price', '未知价格') 
        interval = data.get('interval')  # 这里会收到 "10"
        
        # 3. 准备发送到ATAS的信号数据
        # 处理价格：如果是数字字符串或数字，转换为float；否则为None
        price_value = None
        if price and price != '未知价格':
            try:
                if isinstance(price, (int, float)):
                    price_value = float(price)
                elif isinstance(price, str):
                    price_value = float(price)
            except (ValueError, TypeError):
                price_value = None
        
        # 处理周期：如果是数字字符串或数字，转换为int；否则为None
        interval_value = None
        if interval:
            try:
                if isinstance(interval, (int, float)):
                    interval_value = int(interval)
                elif isinstance(interval, str):
                    interval_value = int(interval)
            except (ValueError, TypeError):
                interval_value = None
        
        # 应用品种映射
        mapped_ticker = map_ticker(ticker)
        
        signal_data = {
            'Ticker': mapped_ticker,
            'Action': action,
            'Price': price_value,
            'Interval': interval_value
        }
        
        # 4. 通过WebSocket广播信号到所有连接的ATAS客户端
        if WS_ENABLED:
            # 直接调用异步广播函数（非阻塞）
            broadcast_signal_async(signal_data)
            # 减少日志输出频率，只在DEBUG模式下记录
            logger.debug(f"信号已排队广播: {signal_data}")
        else:
            logger.debug("WebSocket功能已禁用，信号未广播")
        
        # 5. 记录原始逻辑（保留原有日志）
        if action == 'buy':
            logger.info(f"🚀 触发买入逻辑 -> 品种={ticker}, 周期={interval}分钟, 动作={action}, 价格: {price}")
            
        elif action == 'sell':
            logger.info(f"🔻 触发卖出逻辑 -> 品种={ticker}, 周期={interval}分钟, 动作={action}, 价格: {price}")
            
        else:
            logger.warning(f"⚠️ 收到未知动作: {action}")

        # 4. 必须返回 200 状态码，告诉 TradingView "我收到了"
        return jsonify({"status": "success", "message": "Signal received"}), 200

    except Exception as e:
        logger.error(f"❌ 处理信号时发生错误: {e}", exc_info=True)
        return jsonify({"status": "error", "message": str(e)}), 400

def get_local_ip():
    """获取本机IP地址"""
    try:
        # 连接到一个远程地址来获取本机IP（不会实际发送数据）
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(('8.8.8.8', 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return 'localhost'

if __name__ == '__main__':
    # 加载品种映射配置
    load_ticker_mapping()
    
    # 初始化广播事件循环
    _init_broadcast_loop()
    
    # 启动WebSocket服务器
    start_websocket_server()
    
    # 获取服务器配置
    http_port = 80
    local_ip = get_local_ip()
    
    # 显示webhook接口地址
    print("\n" + "=" * 60)
    print("🚀 信号转发服务已启动")
    print("=" * 60)
    print(f"📡 Webhook接口地址:")
    print(f"   http://{local_ip}:{http_port}/webhook")
    print(f"   http://localhost:{http_port}/webhook")
    print(f"   http://127.0.0.1:{http_port}/webhook")
    print(f"\n🔌 WebSocket服务器:")
    print(f"   ws://{local_ip}:{WS_PORT}")
    print(f"   ws://localhost:{WS_PORT}")
    print(f"\n📁 配置文件路径: {_config_file_path}")
    print(f"📝 日志文件路径: {log_dir}")
    print("=" * 60 + "\n")
    
    logger.info("=" * 60)
    logger.info("信号转发服务已启动")
    logger.info(f"Webhook接口: http://{local_ip}:{http_port}/webhook")
    logger.info(f"WebSocket服务器: ws://{local_ip}:{WS_PORT}")
    logger.info(f"配置文件路径: {_config_file_path}")
    logger.info("=" * 60)
    
    # 启动Flask HTTP服务器
    # debug=True 允许你修改代码后自动重启，方便调试
    # use_reloader=False 防止重载器导致WebSocket服务器重复启动（重载器会启动子进程导致端口冲突）
    logger.info(f"Flask HTTP服务器启动在端口 {http_port}")
    app.run(host='0.0.0.0', port=http_port, debug=True, use_reloader=False)