# ATAS API 签名查看器 (atas_sig_inspector)

## 简介

这是一个**开发辅助工具**，用于通过 .NET 反射读取本机已安装的 ATAS 程序集，打印 `ChartObject` 类中与鼠标交互相关的方法签名。

它不是 ATAS 指标，不参与行情、导出或交易逻辑；仅在编写需要重写鼠标事件的自定义指标时使用。

## 用途

ATAS 为闭源平台，开发自定义指标时若要重写鼠标相关虚方法（例如 `ProcessMouseClick`、`GetCursor`），需要与基类**完全一致的参数类型**。本工具从已安装的 `ATAS.Indicators.dll` 中读取真实签名，避免凭猜测写 `override` 导致编译失败。

本仓库中可参考的用法示例：

- `C#ATAS速度指标/PriceLevelOrderFlowLadder.cs` — 重写 `ProcessMouseClick`、`ProcessMouseDown`、`GetCursor` 实现图表上的清除按钮交互

## 探测的方法

程序会列出 `ATAS.Indicators.ChartObject` 中以下 public 实例方法的完整签名：

| 方法名 | 说明 |
|--------|------|
| `ProcessMouseClick` | 鼠标单击 |
| `ProcessMouseDown` | 鼠标按下 |
| `ProcessMouseUp` | 鼠标抬起 |
| `ProcessMouseMove` | 鼠标移动 |
| `ProcessMouseDoubleClick` | 鼠标双击 |
| `GetCursor` | 鼠标悬停时的光标样式 |

## 前提条件

1. 已安装 [ATAS Platform](https://atas.net/)（默认路径：`C:\Program Files (x86)\ATAS Platform\`）
2. 已安装 [.NET SDK](https://dotnet.microsoft.com/download)（项目目标框架：`net10.0`）

若 ATAS 安装在其他目录，请修改 `Program.cs` 中的 `asmPath` 变量。

## 运行方式

在项目根目录或本目录下执行：

```bash
dotnet run --project atas_sig_inspector/atas_sig_inspector.csproj
```

或在 `atas_sig_inspector` 目录下：

```bash
dotnet run
```

## 输出示例

控制台会输出类似以下内容（具体类型名以本机 ATAS 版本为准）：

```
System.Boolean ProcessMouseClick(OFT.Rendering.Control.RenderControlMouseEventArgs e)
System.Boolean ProcessMouseDown(OFT.Rendering.Control.RenderControlMouseEventArgs e)
System.Boolean ProcessMouseUp(OFT.Rendering.Control.RenderControlMouseEventArgs e)
System.Boolean ProcessMouseMove(OFT.Rendering.Control.RenderControlMouseEventArgs e)
System.Boolean ProcessMouseDoubleClick(OFT.Rendering.Control.RenderControlMouseEventArgs e)
OFT.Rendering.StdCursor GetCursor(OFT.Rendering.Control.RenderControlMouseEventArgs e)
```

将输出中的返回类型与参数类型复制到指标项目的 `override` 方法即可。

## 常见问题

### Q: 提示 `ChartObject not found`？

A: 确认 ATAS 已正确安装，且 `Program.cs` 中的 DLL 路径指向有效的 `ATAS.Indicators.dll`。

### Q: 能否探测其他 ATAS 类型？

A: 可以。修改 `Program.cs` 中的 `GetType(...)` 目标类型名，以及 `Where` 过滤条件中的方法名即可。本工具当前只针对 `ChartObject` 的鼠标 API 做了最小实现。

### Q: 需要部署到 ATAS 指标目录吗？

A: 不需要。这是独立控制台程序，编译运行后查看输出即可，无需复制到 `%LOCALAPPDATA%\ATAS\Indicators\`。

## 许可证

遵循仓库根目录 `LICENSE` 约定。
