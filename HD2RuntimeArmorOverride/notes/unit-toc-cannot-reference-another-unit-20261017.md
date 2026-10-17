# 一个 unit 的 TOC 能不能引用另一个 unit 的网格资源：不能（2026-10-17）

## 结论

**顶点/索引字节是每个 unit 资源条目私有的，不是归档级共享的。**
一个 unit 的 TOC 无法引用另一个 unit 的网格数据。合并必然复制顶点。

## 证据

### 1. 寻址方式是"本条目 gpu blob 内的相对偏移"，不是 ID 查表

* `UnitMeshReader.cs:951-952,1010-1012` —— 解析顶点时用
  `VertexBufferOffset` 在**本条目自己的** gpu 切片里定位。
* `CanonicalUnitRebuilder.cs:149` —— 重建时顶点偏移**总是从 0 起算**，
  即每个 stream 在目标 unit 的 blob 里占自己的区间。

所以跨 unit 共享字节在结构上没有通道。

### 2. `VertexBufferId` / `IndexBufferId` 只是不透明标签，我们从不写它

全仓只有 6 处引用：声明（`UnitMeshModel.cs:90,93`）+ 读取后原样带过
（`UnitMeshReader.cs:471,474,502,505`）。两个写入器
（`CanonicalUnitRebuilder.cs:554-564`、`UnitMeshWriter.cs:649-668`）
只改 count/offset/size，**ID 那 16 个字节原封不动留在目标 TOC 里**。
它从不参与任何偏移计算，也不和任何 buffer 表校验。

**唯一未定**：引擎侧是否把它当归档全局 key 来解引用。
定论方法：dump 两个不同 vanilla unit 条目的 `StreamInfo+336` / `+376`——
每 unit 唯一 ⇒ 单元本地标签（本文结论）；跨 unit 相同 ⇒ 全局 key。
即便如此，磁盘字节仍然是 N 份。

### 3. 顶点字节在 `.gpu_resources`，TOC 里只有偏移和大小

* `PatchArchiveWriter.cs:89` 取 `edit.GpuResourceData`，按内容哈希去重
  （`:103-110`），64 对齐写入 `<patch>.gpu_resources`（`:59,106-108`）。
* `.stream` 只从源 patch 原样复制（`:71-75`），追加路径**从不写** `.stream`
  ——`PatchUnitMeshEditResult` 根本没有 StreamData 成员
  （`PatchReconstructionModels.cs:42-50`）。

### 4. 追加路径确实在复制

* `CanonicalAppendMeshAssembler.cs:50,58-59` 克隆目标顶点表，
  `:71-73` 重新索引并追加源顶点，`:76-78` 按顶点偏移重排三角形。
* `CanonicalMeshPreparation.cs:90,132` 每个顶点按**目标** stream ABI 重新编码
  （`new byte[stride]`）。
* `CanonicalUnitRebuilder.cs:105-106,129-131,137` 新建 GPU blob：
  `gpuData.AddRange(vertex.Data)` + 索引重发 + 重写 stream 偏移。
* 源 `UnitStreamInfo` 被丢弃（`CanonicalDecorationUnitAppender.cs:200`），
  `CanonicalStreamContractCompiler.cs:26,58-61` 只遍历**目标** streams。

## 对方案的影响

* "只传外壳、让 Lua 拼" **不成立**——外壳里的顶点偏移必须在目标 unit 自己的
  blob 内，跨 unit 没有共享通道。
* union unit 的代价因此是**实打实的顶点复制**：磁盘一份、显存一份，
  和"把 43 件分别装成 43 个 unit"相比，顶点数据翻倍。
* 反过来，"43 个独立 unit + 3 bank × 10 slot"的方案在显存上是**最省的**，
  它只是撞上了 30 个键的上限。

## 因此真正要权衡的是

| 方案 | 键数 | 顶点数据 | 切换要重启 | 代码段 |
|---|---|---|---|---|
| 现状 3 bank | ≤30（身体 18） | 1 份 | 否 | 不改 |
| union unit（预载全量 + 切描述表） | 7 | **2 份** | 否 | 不改 |
| 外部合并整块推入 | 7 | 1 份 | 否，但需解决 GPU 缓冲上传 | 不改 |

第二行多出的那份显存，换掉了"键不够"的问题；第三行省显存，但要解决
"新 mesh 的缓冲怎么进显存"——而第 3 节说明 `.gpu_resources` 是归档内的，
运行期无法追加。**所以第三行在现有证据下走不通。**
