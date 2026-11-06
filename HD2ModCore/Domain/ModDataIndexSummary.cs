namespace HD2ModCore.Domain;

// 作用：详情页"跨 Mod 索引"那一行的只读投影。
// <para>
// 这两个类型原本和 <c>IModDataIndex</c>（一份按节点落盘的 JSON 副本）放在一起；
// 那份副本删掉之后，取值改为直接从 referenceGraph 的 SQL 查询索引现算。
// 保留类型名是为了不动界面文案与既有调用点的语义。
// </para>
public enum ModDataIndexStatus
{
	Unavailable,
	Partial,
	Ready
}

public sealed record ModDataIndexSummary(ModDataIndexStatus Status, int ProviderCount, int ConsumerCount);
