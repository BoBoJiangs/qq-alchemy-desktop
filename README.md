# QQAlchemyDesktop

独立的 Windows 桌面 QQ 炼丹与药材采购程序。它不修改或调用 `D:\Java\qqbot`，不依赖 SnowLuma、NapCat、OneBot、QQ 注入插件或 QQ 内存/网络接口。

## 当前实现

- .NET 8 Windows x64 托盘程序，管理面板固定绑定 `http://127.0.0.1:62346`。
- Windows UI Automation 定位 QQ 主窗口、群标题、输入框和 @ 建议；窗口捕获使用 `Windows.Graphics.Capture`，中文识别使用本地 PP-OCRv5 模型（RapidOCRLib）。
- 校准保存 QQ 版本、窗口尺寸、DPI 及相对区域。每次发送前重新核验指纹和群标题。
- @ 操作必须同时确认显示名和 QQ 号 `3889001741`；无法确认时不会发送。
- OCR 需要连续两次规范化结果一致；PP-OCRv5 输出文字、置信度和行坐标，并使用 QQ 蓝色像素收紧药材链接的真实点击区域。
- 坊市商品保存帧哈希、页码、矩形和动作编号。点击药材链接后，必须确认输入框生成了正确的 `@机器人 坊市购买<UUID>`，再点击发送；页面变化或命令校验失败时拒绝购买。
- 采购与炼丹是独立任务。查询最多重试一次；购买点击和炼丹配方没有明确结果时不会自动重试。
- SQLite WAL 保存配置、库存快照、任务检查点和审计记录。程序/QQ 重启后进入 `PausedRecovery`，人工恢复前不继续。
- 首次导入只复制旧项目 `properties` 和账号文件到 `%LocalAppData%\QQAlchemyDesktop`，不会修改源文件。
- 默认演练模式开启，只识别不点击、不发送炼丹配方。

## 构建与发布

需要 .NET 8 SDK。开发机已验证 `dotnet build` 和 `dotnet test`。

```powershell
dotnet restore .\QQAlchemyDesktop.sln
dotnet test .\QQAlchemyDesktop.sln -c Release
dotnet publish .\src\QQAlchemyDesktop\QQAlchemyDesktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

发布目录中的 `QQAlchemyDesktop.exe` 可在没有 .NET Runtime 的 Windows 10/11 x64 机器运行。程序必须运行在已登录、未锁屏的专用 Windows 会话中，不能安装为 Windows 服务。

## 使用顺序

1. 启动官方桌面 QQ，登录并打开目标群；保持 QQ 窗口可恢复、不要锁屏。
2. 启动 `QQAlchemyDesktop.exe`，打开托盘菜单中的管理面板。
3. 填写旧项目目录和账号目录/QQ 号，执行“导入旧数据并生成配方”。
4. 填写群名、机器人显示名和 QQ 号，先执行“探测当前 QQ 窗口”，再执行验证。验证会发送一次无消耗的“药材背包”测试命令，并等待 OCR 回读成功。
5. 保持演练模式，先运行采购或炼丹任务观察状态、OCR 原文、候选商品和队列；确认校准与样本无误后，人工关闭演练模式。
6. 遇到验证码、窗口丢失、OCR 冲突、背包不一致或未知响应时，程序会暂停、截图、写审计并播放提示音；人工处理后点击“人工恢复”，程序会先重新读取背包。

## 固定接口

- `GET /api/status`
- `POST /api/tasks/purchase/start|stop`
- `POST /api/tasks/alchemy/start|stop`
- `POST /api/tasks/emergency-stop`
- `POST /api/tasks/resume`
- `POST /api/calibration/start|verify`
- `POST /api/import`
- `GET /api/settings`
- `PUT /api/settings/alchemy`
- `PUT /api/settings/purchase-rules`
- `GET /api/audit`

## 测试覆盖

测试覆盖配方阈值、性平药引、收益排序、手续费、背包解析、唯一 OCR 纠错、坊市单位换算、重复采购优先、普通价格回退、库存上限、任务购买上限、响应区域去重、蓝色链接定位、采购命令校验和 Windows.Graphics.Capture/PP-OCRv5 集成。若开发机存在当前 Java 账号数据，还会比较 Java/C# 规范化配方集合；当前账号 `3860863656` 已通过完整集合比对。

## 重要限制

桌面自动化不能承诺与官方 API 相同的稳定性或零风控。QQ 升级导致控件树或布局变化时必须重新校准；窗口最小化会先尝试恢复，恢复失败即暂停。不要在无人值守的共享桌面、锁屏会话或多 QQ 混用窗口中运行。
