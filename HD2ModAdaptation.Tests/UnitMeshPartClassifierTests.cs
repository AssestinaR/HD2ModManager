using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Verifies SDK mesh names become conservative armor-part facts for later transfer planning.
public sealed class UnitMeshPartClassifierTests
{
	[Theory]
	[InlineData("Torso_Armor_Slim_lod0", UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor)]
	[InlineData("g_legs_hips_undergarment_male", UnitMeshPartKind.Hips, UnitMeshPartLayer.Undergarment)]
	[InlineData("RightLeg_Undergarment_Any_lod0", UnitMeshPartKind.RightLeg, UnitMeshPartLayer.Undergarment)]
	[InlineData("g_leg_undergarment_r", UnitMeshPartKind.RightLeg, UnitMeshPartLayer.Undergarment)]
	[InlineData("g_leg_undergarment_l", UnitMeshPartKind.LeftLeg, UnitMeshPartLayer.Undergarment)]
	[InlineData("g_l_shoulder_female", UnitMeshPartKind.LeftShoulder, UnitMeshPartLayer.Accessory)]
	public void Classify_RecognizesSdkArmorPartNames(string name, UnitMeshPartKind expectedKind, UnitMeshPartLayer expectedLayer)
	{
		var semantic = new UnitMeshSemanticInfo(name, string.Empty, string.Empty, string.Empty, string.Empty, 0, 7, false, false, false);
		var mesh = new UnitMeshInfo(7, 0, 42, 0, 0, 0, 0, 0, 0, 0, semantic, [], []);
		var model = new UnitMeshModel(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, UnitCustomizationInfo.Empty, [], [], [mesh], [], [], []);

		var part = Assert.Single(new UnitMeshPartClassifier().Classify(new AssetKey(0xe0a48d0be9a7453f, 1), model));

		Assert.Equal(expectedKind, part.PartKind);
		Assert.Equal(expectedLayer, part.Layer);
		Assert.True(part.IsVisualMesh);
		Assert.Equal(100, part.Confidence);
	}

	[Fact]
	public void Classify_GlobalSdkLod0Name_RemainsDefaultImported()
	{
		var semantic = UnitMeshSemanticInfo.Empty(0, 3) with { Name = "anonymous_lod0" };
		var mesh = new UnitMeshInfo(3, 0, 42, 0, 0, 0, 0, 0, 0, 0, semantic, [], []);
		var model = new UnitMeshModel(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, UnitCustomizationInfo.Empty, [], [], [mesh], [], [], []);

		var part = Assert.Single(new UnitMeshPartClassifier().Classify(
			new AssetKey(0xe0a48d0be9a7453f, 1), model, new Dictionary<uint, string> { [42] = "g_torso_undergarment_slim_lod0" }));

		Assert.False(part.IsLod);
	}

	[Fact]
	public void Classify_CullingMesh_IsNotVisualTransferSource()
	{
		var semantic = new UnitMeshSemanticInfo("hips_culling", string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, true, false, false);
		var mesh = new UnitMeshInfo(0, 0, 42, 0, 0, 0, 0, 0, 0, 0, semantic, [], []);
		var model = new UnitMeshModel(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, UnitCustomizationInfo.Empty, [], [], [mesh], [], [], []);

		var part = Assert.Single(new UnitMeshPartClassifier().Classify(new AssetKey(0xe0a48d0be9a7453f, 1), model));

		Assert.Equal(UnitMeshPartKind.Hips, part.PartKind);
		Assert.Equal(UnitMeshPartLayer.Culling, part.Layer);
		Assert.False(part.IsVisualMesh);
	}

	[Fact]
	public void Classify_GlobalSdkBoneName_OverridesAnonymousUnitMeshId()
	{
		var semantic = UnitMeshSemanticInfo.Empty(0, 4) with { Name = "123_lod0" };
		var mesh = new UnitMeshInfo(4, 0, 42, 0, 0, 0, 0, 0, 0, 0, semantic, [], []);
		var model = new UnitMeshModel(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, UnitCustomizationInfo.Empty, [], [], [mesh], [], [], []);

		var part = Assert.Single(new UnitMeshPartClassifier().Classify(
			new AssetKey(0xe0a48d0be9a7453f, 1),
			model,
			new Dictionary<uint, string> { [42] = "g_torso_undergarment_male" }));

		Assert.Equal("g_torso_undergarment_male", part.SemanticName);
		Assert.Equal(UnitMeshPartKind.Torso, part.PartKind);
		Assert.Equal(UnitMeshPartLayer.Undergarment, part.Layer);
		Assert.Equal(UnitMeshBodyVariant.Stocky, part.BodyVariant);
	}

	[Theory]
	[InlineData("g_torso_female", UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Slim)]
	[InlineData("g_torso_male", UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Stocky)]
	[InlineData("g_torso_undergarment_female", UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Slim)]
	[InlineData("g_arm_l", UnitMeshPartKind.LeftArm, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any)]
	[InlineData("g_leg_r", UnitMeshPartKind.RightLeg, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any)]
	public void Classify_InfersSdkNameVariantAndDefaultLayer(string name, UnitMeshPartKind expectedKind, UnitMeshPartLayer expectedLayer, UnitMeshBodyVariant expectedVariant)
	{
		var semantic = new UnitMeshSemanticInfo(name, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, false, false, false);
		var mesh = new UnitMeshInfo(0, 0, 42, 0, 0, 0, 0, 0, 0, 0, semantic, [], []);
		var model = new UnitMeshModel(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, UnitCustomizationInfo.Empty, [], [], [mesh], [], [], []);

		var part = Assert.Single(new UnitMeshPartClassifier().Classify(new AssetKey(0xe0a48d0be9a7453f, 1), model));

		Assert.Equal(expectedKind, part.PartKind);
		Assert.Equal(expectedLayer, part.Layer);
		Assert.Equal(expectedVariant, part.BodyVariant);
	}

	[Theory]
	[InlineData("Slim", UnitMeshBodyVariant.Slim)]
	[InlineData("Stocky", UnitMeshBodyVariant.Stocky)]
	[InlineData("Any", UnitMeshBodyVariant.Any)]
	public void Classify_CustomizationBodyType_ProjectsValidatedArmorVariant(string bodyType, UnitMeshBodyVariant expectedVariant)
	{
		var semantic = new UnitMeshSemanticInfo("Torso_Undergarment_" + bodyType + "_lod0", "Torso", "Undergarment", bodyType, string.Empty, 0, 0, false, false, false);
		var mesh = new UnitMeshInfo(0, 0, 42, 0, 0, 0, 0, 0, 0, 0, semantic, [], []);
		var model = new UnitMeshModel(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new UnitCustomizationInfo(bodyType, "Torso", string.Empty, "Undergarment"), [], [], [mesh], [], [], []);

		var part = Assert.Single(new UnitMeshPartClassifier().Classify(new AssetKey(0xe0a48d0be9a7453f, 1), model));

		Assert.Equal(UnitMeshPartKind.Torso, part.PartKind);
		Assert.Equal(UnitMeshPartLayer.Undergarment, part.Layer);
		Assert.Equal(expectedVariant, part.BodyVariant);
	}
}