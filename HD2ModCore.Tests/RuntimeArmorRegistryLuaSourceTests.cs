using System.Reflection;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class RuntimeArmorRegistryLuaSourceTests
{
	[Fact]
	public void EmbeddedRuntimeIncludesIncrementalModulesBeforeEntryPoint()
	{
		var assembly = typeof(RuntimeArmorRegistryService).Assembly;
		var type = assembly.GetType("HD2ModCore.Infrastructure.RuntimeArmorRegistryLuaSource", throwOnError: true)!;
		var source = (string)type.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
		Assert.Contains("version='0.2.0'", source);
		var transaction = source.IndexOf("local ARMOR_REGISTRY_TRANSACTION=", StringComparison.Ordinal);
		var composition = source.IndexOf("local ARMOR_REGISTRY_COMPOSITION=", StringComparison.Ordinal);
		var entry = source.IndexOf("local __hd2_runtime=", StringComparison.Ordinal);
		Assert.True(transaction >= 0 && composition > transaction && entry > composition);
		Assert.Contains("published buffer limit reached", source);
		Assert.Contains("local function publish_document", source);
		Assert.Contains("incremental applied: built=", source);
		Assert.Contains("metadata-probe.request", source);
		Assert.DoesNotContain("local function apply_document(document)", source);
	}
}
