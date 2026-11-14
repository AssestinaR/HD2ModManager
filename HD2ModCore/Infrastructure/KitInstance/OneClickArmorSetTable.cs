using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// OneClickArmorSet（<c>mods/alomare/one_click_armor_set</c>，MIT License）套装配对表（头盔 <c>'h'</c> 与护甲 <c>'b'</c>，共 278 条）。
/// <para>
/// 数据来源：<c>OneClickArmorSet</c> (<c>ocas.lua</c>, MIT License, Copyright (c) OneClickArmorSet authors)，
/// 仅提取 <c>item_id → 规范套装名</c> 用于护甲/头盔套装配对；是否属于玩家可穿戴装备仍由
/// <see cref="Sectional.SectionalExclusivePlanner.IsNamed"/> 与 <see cref="KitNonPlayerArmors"/> 决定。
/// 带编号变体后缀（如 <c>B-01 Tactical (1)</c>..<c>(9)</c>）已折叠为规范套装名。
/// </para>
/// </summary>
public static partial class OneClickArmorSetTable
{
	[GeneratedRegex(@"\s*\(\d+\)$", RegexOptions.CultureInvariant)]
	private static partial Regex VariantSuffixRegex();

	/// <summary>已知头盔/护甲记录 <c>item_id</c> 到规范套装名的映射。</summary>
	public static IReadOnlyDictionary<uint, string> CanonicalSetsByItemId { get; } = new Dictionary<uint, string>
	{
		[0x01c2a674u] = "AD-26 Bleeding Edge", // b
		[0x020a57a8u] = "SC-30 Trailblazer Scout", // h
		[0x0542e6f1u] = "SC-34 Infiltrator", // h
		[0x056848e9u] = "FS-55 Devastator", // h
		[0x05720b46u] = "UF-84 Doubt Killer", // h
		[0x05e56ee5u] = "SR-24 Street Scout", // b
		[0x085838c1u] = "DP-53 Savior of the Free", // h
		[0x09b08ec4u] = "BP-32 Jackboot", // b
		[0x0a18c237u] = "AD-49 Apollonian", // b
		[0x0ade6719u] = "DS-191 Scorpion", // b
		[0x0b5cd386u] = "CW-36 Winter Warrior", // b
		[0x0b66fc55u] = "TG-8 Sharpshooter", // h
		[0x0c0af708u] = "FS-05 Marksman", // h
		[0x0e3b10bfu] = "UF-50 Bloodhound", // h
		[0x0f41a67eu] = "FS-05 Marksman", // h
		[0x100a5dcdu] = "AF-91 Field Chemist", // b
		[0x10707d29u] = "B-01 Tactical", // h
		[0x126f9cb6u] = "SC-34 Infiltrator", // h
		[0x1369b95bu] = "B-01 Tactical", // b
		[0x14209e3au] = "SC-37 Legionnaire", // b
		[0x16e1b9d3u] = "A-9 Helljumper", // b
		[0x16eeb860u] = "GS-17 Frontier Marshal", // b
		[0x1805a245u] = "BP-77 Grand Juror", // h
		[0x1b2fde48u] = "PH-9 Predator", // h
		[0x1b8329abu] = "KDM-73 Dragoon", // b
		[0x1c089a28u] = "TR-40 Gold Eagle", // b
		[0x1c6c05fau] = "CM-09 Bonesnapper", // b
		[0x1d0e3431u] = "B-01 Tactical", // b
		[0x1d20d6eeu] = "B-08 Light Gunner", // b
		[0x1d60fc90u] = "SC-30 Trailblazer Scout", // b
		[0x1e28f118u] = "KDM-763 Pacesetter", // b
		[0x1e9444f9u] = "FS-23 Battle Master", // h
		[0x1f9bfa78u] = "FS-37 Ravager", // b
		[0x203f720cu] = "CM-14 Physician", // h
		[0x20a85cc1u] = "FS-38 Eradicator", // b
		[0x212df664u] = "EX-16 Prototype 16", // h
		[0x2180eb8du] = "B-01 Tactical", // h
		[0x223a3b4bu] = "I-44 Salamander", // b
		[0x23d0e15cu] = "PH-56 Jaguar", // b
		[0x242702a5u] = "CPH-26 Commandant", // b
		[0x24e3d6bau] = "EX-03 Prototype 3", // b
		[0x24e765bau] = "AD-11 Livewire", // h
		[0x24fdb2c0u] = "GS-66 Lawmaker", // h
		[0x250fd6c1u] = "B-01 Tactical", // b
		[0x2521d401u] = "UF-84 Doubt Killer", // b
		[0x261c4a52u] = "B-01 Tactical", // h
		[0x26bf31feu] = "CE-74 Breaker", // b
		[0x26d572a4u] = "FS-23 Battle Master", // b
		[0x289884f4u] = "AC-2 Obedient", // h
		[0x2d1dd830u] = "I-09 Heatseeker", // b
		[0x2d61f0f9u] = "BFM-16 Tanker", // h
		[0x2e15716bu] = "FS-61 Dreadnought", // h
		[0x2e706db8u] = "O-2 Heavy Operator", // b
		[0x2f51d8dcu] = "TR-117 Alpha Commander", // h
		[0x2f748b84u] = "AF-02 Haz-Master", // h
		[0x30e54b57u] = "RE-2310 Honorary Guard", // b
		[0x3313a0ecu] = "TR-62 Knight", // h
		[0x3412f5e4u] = "BFM-77 Reformer", // b
		[0x35e8934fu] = "TR-7 Ambassador of the Brand", // h
		[0x36456057u] = "FS-05 Marksman", // h
		[0x37442029u] = "CPH-26 Commandant", // h
		[0x37c61642u] = "BFM-220 Ironclad", // h
		[0x38aa207du] = "CM-14 Physician", // b
		[0x38b05b83u] = "FS-34 Exterminator", // h
		[0x3b5af7c7u] = "BP-20 Correct Officer", // h
		[0x3c3ebaf0u] = "SA-32 Dynamo", // b
		[0x3f1bec1au] = "DP-00 Tactical", // b
		[0x40a7d694u] = "B-01 Tactical", // h
		[0x40c6635cu] = "BP-20 Correct Officer", // b
		[0x41211c7fu] = "B-24 Enforcer", // h
		[0x41dd184du] = "O-44 Bonded Pilot", // h
		[0x4452e375u] = "FS-05 Marksman", // h
		[0x44d93620u] = "Unnamed rule:d3385747670", // h
		[0x45d80a38u] = "B-01 Tactical", // h
		[0x47bcec20u] = "RS-100 Sanctioner", // h
		[0x48226e8fu] = "B-08 Light Gunner", // h
		[0x4db09ca4u] = "CM-09 Bonesnapper", // h
		[0x4dd749c6u] = "RS-100 Sanctioner", // b
		[0x4e0785aeu] = "CE-64 Grenadier", // h
		[0x4e8f9f4fu] = "SA-04 Combat Technician", // h
		[0x4f6ccd6cu] = "B-01 Tactical", // h
		[0x4f7fb2bdu] = "B-01 Tactical", // b
		[0x5096bce9u] = "SR-64 Cinderblock", // b
		[0x52045a9bu] = "SC-30 Trailblazer Scout", // h
		[0x52791e64u] = "DS-10 Big Game Hunter", // h
		[0x529efe65u] = "FS-05 Marksman", // h
		[0x537e8e45u] = "FS-61 Dreadnought", // b
		[0x542c752eu] = "IE-3 Martyr", // h
		[0x55fd699bu] = "AC-2 Obedient", // b
		[0x5642fa1au] = "FS-11 Executioner", // h
		[0x5662c173u] = "I-102 Draconaught", // b
		[0x57a7f31du] = "PH-202 Twigsnapper", // h
		[0x584ab37bu] = "DP-00 Tactical", // h
		[0x58716e11u] = "RS-67 Null Cipher", // h
		[0x58dbdacdu] = "FS-05 Marksman", // b
		[0x5a17d6d6u] = "CE-07 Demolition Specialist", // b
		[0x5aaf5d66u] = "SC-30 Trailblazer Scout", // h
		[0x5bb4bbb0u] = "SC-34 Infiltrator", // b
		[0x5c1a87d2u] = "SC-34 Infiltrator", // h
		[0x5c3087d2u] = "DP-40 Hero of the Federation", // h
		[0x5cc09255u] = "AF-50 Noxious Ranger", // b
		[0x5d0d8002u] = "TG-8 Sharpshooter", // b
		[0x5f075dc5u] = "CPG-48 Sapper", // b
		[0x5facabb9u] = "SC-37 Legionnaire", // h
		[0x61b31723u] = "B-01 Tactical", // b
		[0x6351a9aau] = "B-01 Tactical", // b
		[0x63aa42c5u] = "CW-4 Arctic Ranger", // h
		[0x65c07df2u] = "CE-07 Demolition Specialist", // h
		[0x674671a0u] = "CE-64 Grenadier", // b
		[0x677bee02u] = "RE-1861 Parade Commander", // h
		[0x6809ad22u] = "RE-2310 Honorary Guard", // h
		[0x68aa2d85u] = "SC-34 Infiltrator", // h
		[0x69b2ec39u] = "B-01 Tactical", // h
		[0x69bc2d4au] = "CW-22 Kodiak", // b
		[0x6d30f386u] = "B-01 Tactical", // b
		[0x708451e9u] = "B-01 Tactical", // b
		[0x71e406e4u] = "KDM-763 Pacesetter", // h
		[0x72b472a7u] = "PH-9 Predator", // b
		[0x732693a1u] = "Unnamed rule:d2011781480", // b
		[0x7346edc6u] = "TR-62 Knight", // b
		[0x735a0c0bu] = "SR-64 Cinderblock", // h
		[0x73893816u] = "I-102 Draconaught", // h
		[0x756159e4u] = "SC-30 Trailblazer Scout", // b
		[0x77947addu] = "FS-11 Executioner", // b
		[0x782087b2u] = "SC-30 Trailblazer Scout", // h
		[0x790c1b61u] = "SA-8 Ram", // h
		[0x7934ed8bu] = "FS-37 Ravager", // h
		[0x7a07031fu] = "CM-10 Clinician", // b
		[0x7b6406dau] = "FS-05 Marksman", // h
		[0x7c59469bu] = "CW-9 White Wolf", // h
		[0x7d96d0c6u] = "CM-09 Bonesnapper", // b
		[0x7edf6505u] = "CE-27 Ground Breaker", // b
		[0x7fa29b61u] = "SC-30 Trailblazer Scout", // h
		[0x7fa97f66u] = "BP-32 Jackboot", // h
		[0x803276ceu] = "B-24 Enforcer", // b
		[0x8166c1ccu] = "SC-15 Drone Master", // h
		[0x843df174u] = "B-01 Tactical", // h
		[0x8507bb35u] = "SC-30 Trailblazer Scout", // h
		[0x89b1b9e3u] = "CE-101 Guerilla Gorilla", // h
		[0x8a957897u] = "TR-7 Ambassador of the Brand", // b
		[0x8abccc54u] = "EX-03 Prototype 3", // h
		[0x8af14536u] = "IE-12 Righteous", // h
		[0x8b9ed77du] = "B-24 Enforcer", // b
		[0x8bd85dd2u] = "FS-34 Exterminator", // b
		[0x8c6ae37cu] = "BFM-16 Tanker", // b
		[0x8c83c789u] = "I-92 Fire Fighter", // b
		[0x8cd3973bu] = "SA-12 Servo-Assisted", // h
		[0x8ce73daeu] = "B-01 Tactical", // h
		[0x8d06ea5au] = "O-3 Free Spirit", // h
		[0x91d773c8u] = "CW-22 Kodiak", // h
		[0x930dc02fu] = "IE-57 Hell-Bent", // h
		[0x93ad3833u] = "SR-24 Street Scout", // h
		[0x940716d6u] = "TR-9 Cavalier of Democracy", // b
		[0x95903adfu] = "CM-17 Butcher", // b
		[0x95ac1cfdu] = "B-16 Battle-Scarred Veteran", // h
		[0x97cf0a8au] = "CE-81 Juggernaut", // b
		[0x97d7668fu] = "CE-67 Titan", // h
		[0x98e86082u] = "RS-6 Fiend Destroyer", // b
		[0x9ad6358eu] = "AF-52 Lockdown", // h
		[0x9b5bc520u] = "SC-30 Trailblazer Scout", // h
		[0x9bc153c0u] = "Unnamed rule:d3385747670", // b
		[0x9c0bbe91u] = "CPR-80 Bulwark", // b
		[0x9e987d5eu] = "SA-7 Headfirst", // h
		[0x9f212504u] = "Unnamed rule:d251087110", // b
		[0x9f3f8a17u] = "A-9 Helljumper", // h
		[0x9f73133eu] = "TR-117 Alpha Commander", // b
		[0x9fa96490u] = "IE-12 Righteous", // b
		[0xa2f4f328u] = "SC-34 Infiltrator", // h
		[0xa3a7e93du] = "SA-25 Steel Trooper", // h
		[0xa45385cdu] = "TG-122 Demo-Trooper", // b
		[0xa5574ac2u] = "CPR-80 Bulwark", // h
		[0xa57e78bau] = "Unnamed rule:d251087110", // h
		[0xa67010e7u] = "CM-17 Butcher", // h
		[0xa8bf0ccbu] = "RE-824 Bearer of the Standard", // h
		[0xa9a71fe7u] = "DP-8 Mountain-Scaled", // b
		[0xaa32b384u] = "EX-16 Prototype 16", // b
		[0xac0adecdu] = "B-24 Enforcer", // b
		[0xac4235b3u] = "AC-1 Dutiful", // b
		[0xac5d49f9u] = "CE-27 Ground Breaker", // h
		[0xae5978b1u] = "KDM-500 Outrider", // b
		[0xae8f7b4cu] = "AC-1 Dutiful", // h
		[0xaed67d10u] = "RS-89 Shadow Paragon", // b
		[0xaffb955bu] = "CE-35 Trench Engineer", // h
		[0xb028b970u] = "IE-3 Martyr", // b
		[0xb08e0939u] = "B-16 Battle-Scarred Veteran", // b
		[0xb18b6c66u] = "CM-10 Clinician", // h
		[0xb19d6c0au] = "DP-8 Mountain-Scaled", // h
		[0xb1d021ffu] = "I-09 Heatseeker", // h
		[0xb3550d20u] = "UF-50 Bloodhound", // b
		[0xb3deb5d9u] = "CW-9 White Wolf", // b
		[0xb4027b70u] = "B-01 Tactical", // h
		[0xb407147eu] = "A-35 Recon", // h
		[0xb482b460u] = "GS-66 Lawmaker", // b
		[0xb513fd54u] = "DP-40 Hero of the Federation", // b
		[0xb638f3c4u] = "B-01 Tactical", // h
		[0xb661572au] = "UF-16 Inspector", // b
		[0xb69df10du] = "SA-8 Ram", // b
		[0xb6a2f46au] = "SC-34 Infiltrator", // h
		[0xb92e1781u] = "CW-4 Arctic Ranger", // b
		[0xbc5cf836u] = "O-44 Bonded Pilot", // b
		[0xbd177f77u] = "RS-6 Fiend Destroyer", // h
		[0xbd1e5e84u] = "CM-21 Trench Paramedic", // h
		[0xc1611ac9u] = "TG-122 Demo-Trooper", // h
		[0xc25abb74u] = "CM-09 Bonesnapper", // h
		[0xc2a2375fu] = "Unnamed rule:d3923064864", // b
		[0xc60c706du] = "DS-10 Big Game Hunter", // b
		[0xc71dbba4u] = "RE-1861 Parade Commander", // b
		[0xc7a5782fu] = "SA-7 Headfirst", // b
		[0xc9d2ad39u] = "SA-04 Combat Technician", // b
		[0xca092176u] = "RS-67 Null Cipher", // b
		[0xcb7e9736u] = "B-22 Model Citizen", // b
		[0xcd3d20dcu] = "CW-36 Winter Warrior", // h
		[0xd05754cdu] = "RS-89 Shadow Paragon", // h
		[0xd0ce5bc7u] = "I-44 Salamander", // h
		[0xd11da85bu] = "B-27 Fortified Commando", // h
		[0xd1a64ac3u] = "EX-00 Prototype X", // h
		[0xd270fcccu] = "UF-16 Inspector", // h
		[0xd2a83ca7u] = "KDM-500 Outrider", // h
		[0xd2c578e1u] = "B-27 Fortified Commando", // b
		[0xd3461392u] = "FS-55 Devastator", // b
		[0xd52bb413u] = "CM-21 Trench Paramedic", // b
		[0xd59566d5u] = "B-01 Tactical", // b
		[0xd63ffdd3u] = "SR-18 Roadblock", // b
		[0xd7b2fd5du] = "EX-00 Prototype X", // b
		[0xd879973au] = "A-35 Recon", // b
		[0xd915a671u] = "FS-05 Marksman", // h
		[0xda64ee0du] = "FS-38 Eradicator", // h
		[0xda8bbeaau] = "SC-34 Infiltrator", // h
		[0xdae4a744u] = "SA-25 Steel Trooper", // b
		[0xdb7db6aau] = "SC-30 Trailblazer Scout", // h
		[0xdbc21a99u] = "RS-40 Beast of Prey", // b
		[0xdc7304b9u] = "SR-18 Roadblock", // h
		[0xdcce533fu] = "AD-26 Bleeding Edge", // h
		[0xde575ea9u] = "AF-50 Noxious Ranger", // h
		[0xde631a28u] = "PH-202 Twigsnapper", // b
		[0xdee800a8u] = "CE-101 Guerilla Gorilla", // b
		[0xdf5f7ccbu] = "AF-91 Field Chemist", // h
		[0xdf8e4adau] = "B-01 Tactical", // h
		[0xe1ca6837u] = "O-2 Heavy Operator", // h
		[0xe1d53693u] = "IE-57 Hell-Bent", // b
		[0xe207c84au] = "B-01 Tactical", // h
		[0xe3aaa79du] = "DP-11 Champion of the People", // b
		[0xe406ced0u] = "Unnamed rule:d3923064864", // h
		[0xe417ae7bu] = "FS-05 Marksman", // h
		[0xe4e2b61bu] = "RS-20 Constrictor", // h
		[0xe9add047u] = "AF-02 Haz-Master", // b
		[0xec4638afu] = "RS-20 Constrictor", // b
		[0xec49526fu] = "B-22 Model Citizen", // h
		[0xecabadbfu] = "FS-05 Marksman", // b
		[0xed2350cau] = "RS-40 Beast of Prey", // h
		[0xede9d6b7u] = "BFM-77 Reformer", // h
		[0xee2e8ce0u] = "CE-74 Breaker", // h
		[0xee7019edu] = "B-01 Tactical", // h
		[0xeeb4c2deu] = "TR-9 Cavalier of Democracy", // h
		[0xef6d2ad3u] = "DP-11 Champion of the People", // h
		[0xf06ad8d7u] = "PH-56 Jaguar", // h
		[0xf2b7d3f5u] = "SA-12 Servo-Assisted", // b
		[0xf2dcc254u] = "DS-191 Scorpion", // h
		[0xf34064acu] = "CE-67 Titan", // b
		[0xf3bcf18eu] = "SA-32 Dynamo", // h
		[0xf4598295u] = "BP-77 Grand Juror", // b
		[0xf57d61ceu] = "SC-15 Drone Master", // b
		[0xf5cb7633u] = "KDM-73 Dragoon", // h
		[0xf6303500u] = "AF-52 Lockdown", // b
		[0xf646f083u] = "GS-17 Frontier Marshal", // h
		[0xf6ae9439u] = "Unnamed rule:d2011781480", // h
		[0xf7e6f127u] = "O-3 Free Spirit", // b
		[0xf8b8cf9bu] = "BFM-220 Ironclad", // b
		[0xf8c6437fu] = "AD-49 Apollonian", // h
		[0xf8fadb6cu] = "AD-11 Livewire", // b
		[0xf9d912a7u] = "TR-40 Gold Eagle", // h
		[0xfb4b1254u] = "CE-35 Trench Engineer", // b
		[0xfcde237au] = "CE-81 Juggernaut", // h
		[0xfd428984u] = "I-92 Fire Fighter", // h
		[0xfd456de0u] = "DP-53 Savior of the Free", // b
		[0xfdb93622u] = "CPG-48 Sapper", // h
		[0xfdbf4741u] = "B-01 Tactical", // h
		[0xfde8f657u] = "RE-824 Bearer of the Standard", // b
	};

	/// <summary>按 OneClickArmorSet 表查询规范套装名。</summary>
	public static bool TryGetCanonicalSetName(uint itemId, [NotNullWhen(true)] out string? setName)
		=> CanonicalSetsByItemId.TryGetValue(itemId, out setName);

	/// <summary>移除套装名末尾的 <c> (N)</c> 变体编号后缀。</summary>
	public static string NormalizeSetName(string rawSetName)
	{
		if (string.IsNullOrWhiteSpace(rawSetName)) return string.Empty;
		return VariantSuffixRegex().Replace(rawSetName.Trim(), string.Empty);
	}
}
