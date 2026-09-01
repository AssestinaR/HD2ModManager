# 在线资产元数据

在需要更新可选的社区资产显示资料，或需要审阅 `archivehashes.json` 来源时使用。GameData 索引和 Armor / Helmet 权威事实不依赖该文件；缺失或无效时会退回安装目录中的实际 GameData。

先读取状态：

```powershell
hd2mod assets metadata inspect --manager-root <管理器目录> --json
```

更新必须固定远端内容后再提交：

```powershell
hd2mod assets metadata plan --manager-root <管理器目录> --output asset-metadata-plan.json --json
hd2mod assets metadata validate --manager-root <管理器目录> --plan asset-metadata-plan.json --json
hd2mod assets metadata apply --manager-root <管理器目录> --plan asset-metadata-plan.json --confirm --json
```

`plan` 会联网下载、格式验证，并把实际内容保存到 `asset-metadata-plan.json.assets`。`validate` 与 `apply` 不会再联网；若设置、本地缓存或暂存内容发生变化，它们会拒绝提交。桌面管理器必须关闭。可在 `plan` 后提供一个明确的 HTTPS 原始仓库地址以临时覆盖设置中的来源，但不会改写该设置。
