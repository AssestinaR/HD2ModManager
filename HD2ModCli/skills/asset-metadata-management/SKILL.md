# 在线资产元数据

在需要建立 GameData 索引、分析装备或修复 Patch，而本地 `archivehashes.json` 缺失、过期或来源需要审阅时使用。

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
