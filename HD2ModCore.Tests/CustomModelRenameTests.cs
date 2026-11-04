using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 改名要落到"持有这具素体的那份 <c>custom-model.json</c>"。
/// <para>
/// 用户报的症状是"编辑名称过一会又被还原"。根因是名字只写进了注册表（工作区），
/// 而那份文件才是归属凭证：重构建拿它当输入、产物重新登记时又用它的名字，
/// 于是下一次后台重构建就把名字盖回 VPK 文件名。
/// </para>
/// </summary>
public sealed class CustomModelRenameTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "hd2-custom-model-rename-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
			// 清理失败不该让测试结果变红。
		}
	}

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

	private string WriteOwner(string directoryName, string key, string name, params RuntimeArmorRegistryModelPiece[] pieces)
	{
		var directory = Path.Combine(root, directoryName, "vpk2patch");
		Directory.CreateDirectory(directory);
		var model = new RuntimeArmorRegistryModel(
			key,
			null,
			pieces.Length == 0 ? [new(3, "0123456789abcdef", 2, 0)] : pieces,
			name);
		var path = Path.Combine(directory, VpkCustomModelFile.FileName);
		File.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions));
		return path;
	}

	/// <summary>改名只动 <c>name</c>，key 与件表原样保留。</summary>
	[Fact]
	public void RenamingOnlyTouchesTheName()
	{
		var path = WriteOwner("MourningVeil_Bill", "custom-a", "mourningveil_bill",
			new RuntimeArmorRegistryModelPiece(0, "0000000000000001", 2, 0),
			new RuntimeArmorRegistryModelPiece(1, "0000000000000002", 2, 0));

		Assert.True(VpkCustomModelFile.TryRenameOwned(root, "custom-a", "丧钟 veil（我的名字）"));

		var saved = JsonSerializer.Deserialize<RuntimeArmorRegistryModel>(File.ReadAllText(path), JsonOptions)!;
		Assert.Equal("丧钟 veil（我的名字）", saved.Name);
		Assert.Equal("custom-a", saved.Key);
		Assert.Equal(2, saved.Pieces!.Count);
		Assert.Equal("0000000000000002", saved.Pieces[1].Unit);
	}

	/// <summary>
	/// 素体被嵌在 Mod 目录的任意深度（实测在 <c>vpk2patch/</c> 下），必须扫得到。
	/// </summary>
	[Fact]
	public void TheOwnerFileIsFoundAtAnyDepth()
	{
		var path = WriteOwner("Mod 名字", "custom-b", "旧名字");

		Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(VpkCustomModelFile.TryFindOwnerFile(root, "custom-b")!));
		Assert.True(VpkCustomModelFile.TryRenameOwned(root, "custom-b", "新名字"));
		Assert.Equal("新名字", VpkCustomModelFile.TryReadName(Path.GetDirectoryName(path)!));
	}

	/// <summary>库里没有这个 key ⇒ 什么都不写，也不抛（改名流程不该因为没主而失败）。</summary>
	[Fact]
	public void AnUnknownKeyChangesNothing()
	{
		WriteOwner("some-mod", "custom-a", "原名");

		Assert.False(VpkCustomModelFile.TryRenameOwned(root, "custom-未知", "新名字"));
		Assert.Null(VpkCustomModelFile.TryFindOwnerFile(root, "custom-未知"));
		Assert.False(VpkCustomModelFile.TryRenameOwned(Path.Combine(root, "不存在的目录"), "custom-a", "新名字"));
	}

	/// <summary>文件内容坏了就原样留着：宁可这次没改成，也不要把归属文件写废。</summary>
	[Fact]
	public void ACorruptFileIsLeftUntouched()
	{
		var directory = Path.Combine(root, "broken", "vpk2patch");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, VpkCustomModelFile.FileName);
		File.WriteAllText(path, "{ \"key\": \"custom-c\", \"name\": ");
		var before = File.ReadAllText(path);

		Assert.False(VpkCustomModelFile.TryRenameOwned(root, "custom-c", "新名字"));
		Assert.Equal(before, File.ReadAllText(path));
	}
}
