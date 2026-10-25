# UiSnapshot

离屏渲染 `HD2ModManager` 真实视图的小工具，用于 UI 审查和改动前后对比。

它**不启动主窗口、不读取 Mod 库、不接触游戏目录**：只加载真实的 `App.xaml` 资源字典，实例化真实的视图，用 `RenderTargetBitmap` 直接光栅化视觉树。因此它不依赖桌面合成，在无头 / 远程 / 画面冻结的会话里也能稳定出图。

## 用法

```powershell
dotnet build tools\UiSnapshot\UiSnapshot.csproj
tools\UiSnapshot\bin\Debug\net10.0-windows\UiSnapshot.exe <scenario> [--out <dir>] [--scale <n>] [--name <file>]
```

- `--scale`：1 = 96 DPI（真实尺寸），2 = 192 DPI（默认，便于看清 1px 细节）
- `--out`：默认 `artifacts/ui-audit/render`
- `--name`：输出文件名（不含扩展名），默认用场景名

列出场景：

```powershell
tools\UiSnapshot\bin\Debug\net10.0-windows\UiSnapshot.exe list
```

## 现有场景

| 场景 | 内容 |
|---|---|
| `modlist` | 库页行模板，12 行混合状态（类型/启用/顺序/告警/摘要/备注） |
| `modlist-hover` | 同上，第 3 行重放悬停态（背景 + 描边 + 操作药丸） |
| `modlist-selected` | 同上，第 2、5 行选中 |
| `modlist-checkbox` | 配置页形态：勾选框 + 上移/下移/移除动作 + 长标题 |
| `modlist-empty` | 空列表 |

## 加新场景

1. 在 `ScenarioCatalog.Factories` 注册名字；
2. 在对应 `*Scenario.Build` 里构造视图、返回 `Scenario`；
3. 需要注入"只能由输入驱动"的状态（悬停、焦点、拖拽中）时，写进 `Prepare`。

`Prepare` 拿到的根元素已经完成 `Show()` 和一次布局，可以直接改属性再 `UpdateLayout`。

## 已知边界

- 只能渲染**静态状态**。悬停态由 `ApplyHover` 按 DataTemplate 里的 setter 重放，不是真实输入；动画中间帧不覆盖。
- 数据是 `FakeRow` 造的假行，不是真实 `ModCardViewModel`；绑定路径与真实模板一致，但业务字段是常量。
- 缩略图是运行时生成的纯色方块，用于验证图片解码链路与 36px 视觉锚点。
- 本项目**刻意不加入 `WpfApp1.sln`**，避免拖慢常规构建与 CI；需要时单独 `dotnet build`。
