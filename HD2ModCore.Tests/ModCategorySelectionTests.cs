using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: 钉住标签式分类筛选的规则：点一下加、再点一下减、空集合恒真、并集命中、被删分类要摘键。
public sealed class ModCategorySelectionTests
{
	[Fact]
	public void Empty_MatchesEverything()
	{
		var selection = new ModCategorySelection();

		Assert.False(selection.IsActive);
		Assert.True(selection.Matches(Array.Empty<string>()));
		Assert.True(selection.Matches(new[] { "Armor" }));
	}

	[Fact]
	public void Toggle_AddsThenRemoves()
	{
		var selection = new ModCategorySelection();

		Assert.True(selection.Toggle("Armor"));
		Assert.Equal(new[] { "Armor" }, selection.Keys);
		Assert.True(selection.Toggle("Armor"));
		Assert.Empty(selection.Keys);
	}

	[Fact]
	public void Add_IsCaseInsensitiveAndOrderPreserving()
	{
		var selection = new ModCategorySelection();

		Assert.True(selection.Add("Armor"));
		Assert.False(selection.Add("armor"));
		Assert.True(selection.Add("  Cape  "));
		Assert.Equal(new[] { "Armor", "Cape" }, selection.Keys);
	}

	[Fact]
	public void Add_BlankKey_IsIgnored()
	{
		var selection = new ModCategorySelection();

		Assert.False(selection.Add(null));
		Assert.False(selection.Add("   "));
		Assert.False(selection.IsActive);
	}

	[Fact]
	public void Matches_IsUnion()
	{
		var selection = new ModCategorySelection();
		selection.Add("Armor");
		selection.Add("Cape");

		Assert.True(selection.Matches(new[] { "Cape" }));
		Assert.True(selection.Matches(new[] { "Armor", "Cape" }));
		Assert.False(selection.Matches(new[] { "Weapon" }));
		Assert.False(selection.Matches(Array.Empty<string>()));
	}

	[Fact]
	public void PruneKnown_DropsDeletedCustomCategory()
	{
		var selection = new ModCategorySelection();
		selection.Add("Armor");
		selection.Add("c:deadbeef");

		var changed = selection.PruneKnown(new[] { "Armor", "Cape", "None", "c:alive000" });

		Assert.True(changed);
		Assert.Equal(new[] { "Armor" }, selection.Keys);
	}

	[Fact]
	public void PruneKnown_KeepsEverythingThatStillExists()
	{
		var selection = new ModCategorySelection();
		selection.Add("c:alive000");

		Assert.False(selection.PruneKnown(new[] { "Armor", "c:alive000" }));
		Assert.True(selection.IsActive);
	}

	[Fact]
	public void Clear_ReportsWhetherAnythingChanged()
	{
		var selection = new ModCategorySelection();

		Assert.False(selection.Clear());
		selection.Add("Armor");
		Assert.True(selection.Clear());
		Assert.False(selection.IsActive);
	}
}
