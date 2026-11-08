using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using FileDecryptor;
using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;
using AdaptationPatchTocScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Reads the game's explicit Armor/Helmet/Cape customization map. The
// encrypted .dl_bin is the update-authoritative source.  On every explicit
// asset-index rebuild we decode it first and atomically refresh the adjacent
// plaintext cache used by the armor-set reader.  This prevents a game update
// from silently leaving an older generated_customization_armor_sets file in
// place and poisoning an otherwise newly-built SQLite index.
public sealed class GameDataAuthorityProvider : IGameDataAuthorityProvider
{
	private const string GeneratedLibraryName = "generated_customization_armor_sets";
	private const string EncryptedLibraryName = "generated_customization_armor_sets.dl_bin";
	private const string GeneratedWeaponCustomizationSettingsName = "generated_weapon_customization_settings.dl_bin";
	private const string GeneratedUnitCustomizationSettingsName = "generated_unit_customization_settings.dl_bin";
	private const string GeneratedEntitiesName = "generated_entities.dl_bin";
	private const string StringsArchiveName = "9ba626afa44a3aa3";
	private const string LanguageSettingsName = "generated_language_settings.dl_bin";
	private const ulong HashLookupTypeId = 0xe3f2851035957af5;
	private const ulong HashLookupFileId = 0x7056bc19c69f0f07;
	private const string LocalizedNameSource = "GameData.generated_customization_armor_sets+strings";
	private const string WeaponNameSource = "GameData.generated_weapon_customization_settings+strings";
	private const string UnitNameSource = "GameData.generated_unit_customization_settings+strings";
	private const string EntityNameSource = "GameData.generated_entities+strings";
	private static readonly ConcurrentDictionary<string, LocalizedNameCacheEntry> LocalizedNameCache = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentDictionary<string, GeneratedEntityCacheEntry> GeneratedEntityCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly string _preferredLocale;

	/// <summary>
	/// 护甲注册表备份（"唯一可信原件"）所在的位置。为 null 时按默认位置解析；
	/// 建索引时必须显式传入，否则和护甲映射服务用的根不一致时会静默读到被改过的文件。
	/// </summary>
	private readonly string? _registryRoot;

	public GameDataAuthorityProvider(string? preferredLocale = null, string? registryRoot = null)
	{
		_preferredLocale = NormalizeLocale(preferredLocale)
			?? NormalizeLocale(CultureInfo.CurrentUICulture.Name)
			?? "en-US";
		_registryRoot = registryRoot;
	}

	public async ValueTask<GameDataAuthoritySnapshot> LoadAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken = default,
		bool refreshPlaintextCache = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		var dataRoot = Path.GetFullPath(gameDataDirectory);
		var gameRoot = Path.Combine(dataRoot, "game");
		var plaintextPath = Path.Combine(gameRoot, GeneratedLibraryName);
		var encryptedPath = Path.Combine(gameRoot, EncryptedLibraryName);
		// **权威源 = 原件副本，不是当前文件。**
		//
		// 为什么不能读当前文件：真映射会重写它，而真映射的产物只保留"被映射的那几个来源模型"
		// 的 unit。实测 2026-11-06：读当前文件时不同 unit 从 2006 个塌成 66 个、件从 3527
		// 掉到 2080，于是高级装备索引（unit 部件 / 流布局）一个都建不出来——数据库描述的是
		// 我们自己的输出，而不是游戏原版。
		//
		// 副本只在两种情况下刷新：
		//   ① 还没有副本（第一次建库）；
		//   ② 游戏更新过（Steam manifest 的 LastUpdated 晚于副本时间），且当前文件看起来没被
		//      我们改过（件数不少于副本）。
		// 其余情况一律读副本。判断依据全是外部事实（Steam 的 manifest、文件的件数），
		// 不需要任何"我们写过它没有"的状态文件。
		var entitiesPath = Path.Combine(gameRoot, GeneratedEntitiesName);
		var issues = new List<CoreIssue>();
		var authorityPath = await ResolveAuthoritySourceAsync(encryptedPath, dataRoot, issues, cancellationToken).ConfigureAwait(false);

		GameItemAssetCatalog? catalog = null;
		byte[]? authoritativeBytes = null;
		GameDataAuthoritySourceKind sourceKind = GameDataAuthoritySourceKind.Unavailable;
		string? sourcePath = null;

		// Do not trust a pre-existing plaintext cache before the current .dl_bin.
		// Steam updates the latter, but knows nothing about our locally generated
		// cache.  Decoding is inexpensive compared to scanning every GameData TOC,
		// and also gives a rebuild one single authoritative update boundary.
		if (File.Exists(authorityPath))
		{
			var encrypted = await TryReadEncryptedDataLibraryAsync(authorityPath, cancellationToken).ConfigureAwait(false);
			if (encrypted.Error is null && encrypted.Bytes is not null)
			{
				try
				{
					catalog = CustomizationArmorSetsReader.Read(encrypted.Bytes);
					if (catalog.Items.Count == 0)
						throw new InvalidDataException("解密后的装备关系表不包含任何可用装备条目。");
					authoritativeBytes = encrypted.Bytes;
					sourceKind = GameDataAuthoritySourceKind.DecryptedDataLibrary;
					sourcePath = authorityPath;

					if (refreshPlaintextCache)
					{
						var cacheWriteError = await TryRefreshPlaintextCacheAsync(plaintextPath, encrypted.Bytes, cancellationToken).ConfigureAwait(false);
						if (cacheWriteError is not null)
						{
							issues.Add(new CoreIssue(
								CoreIssueSeverity.Warning,
								"GameDataAuthorityPlaintextRefreshFailed",
								$"已从当前 .dl_bin 读取装备关系表，但无法刷新本地明文缓存：{cacheWriteError.Message}",
								plaintextPath,
								ExceptionMessage: cacheWriteError.ToString()));
						}
					}
				}
				catch (Exception exception) when (exception is InvalidDataException
					or OverflowException
					or ArgumentOutOfRangeException
					or IndexOutOfRangeException)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Error,
						"GameDataAuthorityDecryptedFormatInvalid",
						$"解密后的装备关系表格式无效：{exception.Message}",
						authorityPath,
						ExceptionMessage: exception.ToString()));
				}
			}
			else if (encrypted.Error is not null)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"GameDataAuthorityEncryptedRefreshFailed",
					$"无法从权威 .dl_bin 重建装备关系明文缓存，将尝试使用现有明文：{encrypted.Error.Message}",
					authorityPath,
					ExceptionMessage: encrypted.Error.ToString()));
			}
		}

		if (catalog is null && File.Exists(plaintextPath))
		{
			try
			{
				var plaintext = await ReadAllBytesSharedAsync(plaintextPath, cancellationToken).ConfigureAwait(false);
				catalog = CustomizationArmorSetsReader.Read(plaintext);
				if (catalog.Items.Count == 0)
					throw new InvalidDataException("明文装备关系表不包含任何可用装备条目。");
				authoritativeBytes = plaintext;
				sourceKind = GameDataAuthoritySourceKind.PlaintextDataLibrary;
				sourcePath = plaintextPath;
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException
				or OverflowException
				or ArgumentOutOfRangeException
				or IndexOutOfRangeException)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"GameDataAuthorityPlaintextUnreadable",
					$"无法读取本地明文装备关系表：{exception.Message}",
					plaintextPath,
					ExceptionMessage: exception.ToString()));
			}
		}

		if (catalog is null)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"GameDataAuthorityArmorCatalogUnavailable",
				"未找到可验证的游戏装备关系表；护甲/头盔关系将暂时不可用，但仍会继续读取实体百科以解析武器等通用 Unit 名称。",
				File.Exists(plaintextPath) ? plaintextPath : encryptedPath));
		}

		var localized = await LoadLocalizedArchiveNamesAsync(dataRoot, catalog, issues, cancellationToken).ConfigureAwait(false);
		var supplemental = await LoadSupplementalArchiveNamesAsync(dataRoot, localized, issues, cancellationToken).ConfigureAwait(false);
		var entities = await LoadGeneratedEntityFactsAsync(dataRoot, entitiesPath, localized.Tables, issues, cancellationToken).ConfigureAwait(false);
		var allNames = localized.Names.Concat(supplemental.Names)
			.GroupBy(name => (name.ArchiveId, name.ItemId, name.Locale, name.DisplayName, name.Source), FullNameComparer.Instance)
			.Select(group => group.OrderByDescending(name => name.Confidence).First())
			.OrderBy(name => name.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(name => name.ItemId)
			.ThenBy(name => name.Locale, StringComparer.OrdinalIgnoreCase)
			.ThenBy(name => name.DisplayName, StringComparer.Ordinal)
			.ToArray();
		var kits = catalog is null
			? Array.Empty<GameDataAuthorityKit>()
			: Project(catalog, issues, allNames);
		if (catalog is null && entities.Facts.Count == 0)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"GameDataAuthorityUnavailable",
				"未找到可验证的游戏装备关系表或实体百科；原生装备将暂时回退到 Unit 结构事实和社区显示资料。",
				File.Exists(entitiesPath) ? entitiesPath : (File.Exists(plaintextPath) ? plaintextPath : encryptedPath)));
		}

		// 索引指纹**包含原件副本的身份**，但**不包含当前文件**。
		//
		// 区别在于：真映射每应用一次都会改写当前文件，拿它做判据会让索引每次都误报过期；
		// 而原件副本只在"游戏更新过"时才换一份，所以它的 SHA256 既稳定（我们自己的写入
		// 不会让它变），又能在游戏更新后失效——这正是索引该在什么时候重建。
		// 上次这里传了空字节，等于把注册表整个排除在指纹之外，于是"权威源换了"也不会
		// 让索引过期，数据库会带着旧内容继续自称 current（2026-11-06 实测踩到）。
		return new GameDataAuthoritySnapshot(
			sourceKind,
			sourcePath,
			ComputeFingerprint(
				sourceKind,
				ArmorDataFileIdentity.IdentityBytes(
					ArmorDataFileIdentity.GetUnmodifiedSealedSha(encryptedPath, _registryRoot)),
				localized.SourceFingerprint,
				supplemental.SourceFingerprint,
				entities.SourceFingerprint),
			DateTimeOffset.UtcNow,
			kits,
			issues)
		{
			PreferredLocale = _preferredLocale,
			ArchiveNames = allNames,
			EntityFacts = entities.Facts
		};
	}

	private static async ValueTask<(byte[]? Bytes, Exception? Error)> TryReadEncryptedDataLibraryAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			var encrypted = await ReadAllBytesSharedAsync(path, cancellationToken).ConfigureAwait(false);
#pragma warning disable CS0618
			var decryptor = Decryptor.CreateWithEmbeddedKeys();
#pragma warning restore CS0618
			return (decryptor.DecodeData(encrypted), null);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or CryptographicException
			or InvalidOperationException
			or TypeInitializationException
			or DllNotFoundException
			or BadImageFormatException)
		{
			return (null, exception);
		}
	}

	/// <summary>
	/// 决定这次拿哪一份文件当权威源，必要时把当前文件采纳为新的原件副本。
	/// <para>
	/// 判据全是外部事实，不维护任何"我们写过它没有"的状态：
	/// <list type="bullet">
	/// <item>没有副本 → 采纳当前文件（第一次建库）。</item>
	/// <item>游戏更新过（Steam manifest 的 LastUpdated 晚于副本写入时间）→ 当前文件是 Steam
	/// 覆盖过的官方版本，采纳它；但如果它带着真映射的签名（内容哈希等于副本、字节却不同），
	/// 说明它其实没被 Steam 覆盖，<b>不采纳</b>，继续读副本并给出一条可执行的提示。</item>
	/// <item>其余 → 读副本。</item>
	/// </list>
	/// </para>
	/// </summary>
	private async ValueTask<string> ResolveAuthoritySourceAsync(
		string livePath,
		string dataRoot,
		List<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(_registryRoot)) return livePath;
		var copyPath = ArmorDataFileIdentity.ResolveBackupPath(_registryRoot);
		if (!File.Exists(copyPath))
		{
			await TryAdoptAsOriginalCopyAsync(livePath, copyPath, issues, cancellationToken).ConfigureAwait(false);
			return File.Exists(copyPath) ? copyPath : livePath;
		}

		if (!GameUpdatedSince(copyPath, dataRoot)) return copyPath;

		var livePieces = await TryCountPiecesAsync(livePath, cancellationToken).ConfigureAwait(false);
		var copyPieces = await TryCountPiecesAsync(copyPath, cancellationToken).ConfigureAwait(false);
		if (livePieces is not null && copyPieces is not null && livePieces >= copyPieces
			&& !await LooksLikeOurCompileAsync(livePath, copyPath, cancellationToken).ConfigureAwait(false))
		{
			await TryAdoptAsOriginalCopyAsync(livePath, copyPath, issues, cancellationToken).ConfigureAwait(false);
			return copyPath;
		}

		issues.Add(new CoreIssue(
			CoreIssueSeverity.Warning,
			"ArmorOriginalCopyKeptAfterGameUpdate",
			"游戏更新过，但当前 .dl_bin 看起来仍带着真映射的改动，所以没有覆盖原件副本。"
			+ "请先在管理器里关掉真映射（应用一次空映射就会把文件还原），再重建 gamedata 资产。",
			livePath));
		return copyPath;
	}

	/// <summary>
	/// 当前这份 dl_bin 是不是<b>我们编译出来的</b>。
	/// <para>
	/// <b>为什么不能再只看件数。</b>件数判据的前提是"真映射会替换件表、件数会变少"。现在的编译
	/// 只改件记录的 unit、件数一个都不动（见 <see cref="ArmorRegistryDataMapper"/>），
	/// 所以"件数不比副本少"不再能证明文件没被改过。
	/// </para>
	/// <para>
	/// <b>真映射的产物有唯一签名：内容哈希等于原件、字节却不相同。</b>伪造那一步就是为了让
	/// 内容哈希回到原值，所以"哈希相同但逐字节不同"只可能出自我们。官方版本的内容变了，
	/// 哈希一定跟着变；逐字节相同则本来就是同一份原件。
	/// </para>
	/// </summary>
	private static async ValueTask<bool> LooksLikeOurCompileAsync(
		string livePath,
		string copyPath,
		CancellationToken cancellationToken)
	{
		var live = await TryReadEncryptedDataLibraryAsync(livePath, cancellationToken).ConfigureAwait(false);
		var copy = await TryReadEncryptedDataLibraryAsync(copyPath, cancellationToken).ConfigureAwait(false);
		if (live.Bytes is null || copy.Bytes is null) return false;
		if (live.Bytes.AsSpan().SequenceEqual(copy.Bytes)) return false;
		return ArmorRegistryDataMapper.HashContent(live.Bytes)
			== ArmorRegistryDataMapper.HashContent(copy.Bytes);
	}

	/// <summary>Steam 记录的"最后更新时间"晚于副本的写入时间 ⇒ 副本早于这次游戏更新。</summary>
	private static bool GameUpdatedSince(string copyPath, string dataRoot)
	{
		var manifest = SteamAppManifest.TryRead(SteamAppManifest.ResolveManifestPath(dataRoot));
		if (manifest is null || !manifest.IsFullyInstalled) return false;
		try
		{
			var updated = DateTimeOffset.FromUnixTimeSeconds(manifest.LastUpdatedUnix);
			return File.GetLastWriteTimeUtc(copyPath) < updated.UtcDateTime;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or ArgumentOutOfRangeException)
		{
			return false;
		}
	}

	/// <summary>解出这份 dl_bin 里的件数；解不开返回 <c>null</c>——判据缺失时不猜。</summary>
	private static async ValueTask<int?> TryCountPiecesAsync(string path, CancellationToken cancellationToken)
	{
		if (!File.Exists(path)) return null;
		var decoded = await TryReadEncryptedDataLibraryAsync(path, cancellationToken).ConfigureAwait(false);
		if (decoded.Bytes is null) return null;
		try
		{
			return CustomizationArmorSetsReader.Read(decoded.Bytes).Items.Sum(item => item.Pieces.Count);
		}
		catch (Exception exception) when (exception is InvalidDataException
			or OverflowException
			or ArgumentOutOfRangeException
			or IndexOutOfRangeException)
		{
			return null;
		}
	}

	/// <summary>
	/// 把一份 dl_bin 记成「原件副本」——真映射的编译基底。
	/// <para>
	/// 只在两个时机调用：还没有副本，或游戏确实更新过。写失败只记一条警告：
	/// 这次读到的数据仍然有效，下次重建还会再试。
	/// </para>
	/// </summary>
	private static async ValueTask TryAdoptAsOriginalCopyAsync(
		string sourcePath,
		string backupPath,
		List<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		try
		{
			var current = await ReadAllBytesSharedAsync(sourcePath, cancellationToken).ConfigureAwait(false);
			if (File.Exists(backupPath))
			{
				var existing = await ReadAllBytesSharedAsync(backupPath, cancellationToken).ConfigureAwait(false);
				if (existing.AsSpan().SequenceEqual(current)) return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
			var temp = backupPath + ".tmp";
			await File.WriteAllBytesAsync(temp, current, cancellationToken).ConfigureAwait(false);
			File.Move(temp, backupPath, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"ArmorOriginalCopyRefreshFailed",
				$"已读到装备关系表，但无法刷新原件副本（真映射的编译基底）：{exception.Message}",
				backupPath,
				ExceptionMessage: exception.ToString()));
		}
	}

	private static async ValueTask<Exception?> TryRefreshPlaintextCacheAsync(
		string plaintextPath,
		byte[] decodedBytes,
		CancellationToken cancellationToken)
	{
		try
		{
			if (File.Exists(plaintextPath))
			{
				var existing = await ReadAllBytesSharedAsync(plaintextPath, cancellationToken).ConfigureAwait(false);
				if (existing.AsSpan().SequenceEqual(decodedBytes))
				{
					return null;
				}
			}

			var temporaryPath = plaintextPath + ".refresh-" + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				await File.WriteAllBytesAsync(temporaryPath, decodedBytes, cancellationToken).ConfigureAwait(false);
				File.Move(temporaryPath, plaintextPath, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
			}

			return null;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or NotSupportedException)
		{
			return exception;
		}
	}

	private static async ValueTask<(byte[]? Bytes, Exception? Error)> TryReadGeneratedEntitiesAsync(
		string path,
		CancellationToken cancellationToken)
	{
		try
		{
			var raw = await ReadAllBytesSharedAsync(path, cancellationToken).ConfigureAwait(false);
			if (LooksLikePlaintextGeneratedEntities(raw))
			{
				return (raw, null);
			}

#pragma warning disable CS0618
			var decryptor = Decryptor.CreateWithEmbeddedKeys();
#pragma warning restore CS0618
			return decryptor.TryDecodeData(raw, out var decoded)
				? (decoded, null)
				: (null, new CryptographicException("generated_entities.dl_bin is not valid plaintext and could not be decrypted with the installed GameData key."));
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or CryptographicException
			or InvalidOperationException
			or TypeInitializationException
			or DllNotFoundException
			or BadImageFormatException)
		{
			return (null, exception);
		}
	}

	private static bool LooksLikePlaintextGeneratedEntities(ReadOnlySpan<byte> data)
		=> data.Length >= 20
			&& BitConverter.ToUInt32(data[..4]) == GeneratedEntityTypeIds.EntitySettingsHashmap
			&& data.Slice(4, 4).SequenceEqual("LDLD"u8)
			&& BitConverter.ToUInt32(data.Slice(12, 4)) == GeneratedEntityTypeIds.EntitySettingsHashmap;

	private static async ValueTask<byte[]> ReadAllBytesSharedAsync(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length > int.MaxValue) throw new InvalidDataException($"装备关系表过大：{path}");
		var bytes = new byte[checked((int)stream.Length)];
		await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
		return bytes;
	}

	private IReadOnlyList<GameDataAuthorityKit> Project(
		GameItemAssetCatalog catalog,
		ICollection<CoreIssue> issues,
		IReadOnlyList<GameDataAuthorityArchiveName> localizedNames)
	{
		var kits = new List<GameDataAuthorityKit>(catalog.Items.Count);
		foreach (var item in catalog.Items)
		{
			var kind = ToKitKind(item.KitType);
			var archiveId = item.ArchiveHash.ToString("x16");
			var displayName = PickName(
				localizedNames.Where(name => name.ArchiveId.Equals(archiveId, StringComparison.OrdinalIgnoreCase)
					&& name.ItemId == item.Id),
				_preferredLocale,
				item.Name);
			var pieces = new List<GameDataAuthorityPiece>(item.Pieces.Count);
			foreach (var piece in item.Pieces)
			{
				if (piece.UnitAsset.TypeHash != PatchUnitMeshReader.UnitTypeId)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"GameDataAuthorityUnexpectedUnitType",
						$"装备 0x{item.Id:x8} 的 Unit 类型为 0x{piece.UnitAsset.TypeHash:x16}，与当前 Unit 类型常量不一致。",
						ExceptionMessage: $"Archive={archiveId}; Unit=0x{piece.UnitAsset.NameHash:x16}"));
				}
				pieces.Add(new GameDataAuthorityPiece(
					piece.BodyType,
					ToBodyVariant(piece.BodyType),
					piece.Slot,
					ToPartKind(piece.Slot),
					piece.PieceType,
					piece.Weight,
					piece.ToneVariations,
					new AssetKey(piece.UnitAsset.TypeHash, piece.UnitAsset.NameHash),
					piece.Resources.Select(resource => new GameDataAuthorityResourceReference(resource.Role, resource.Hash)).ToArray()));
			}
			kits.Add(new GameDataAuthorityKit(item.Id, item.DlcId, item.SetId, kind, archiveId, displayName, pieces));
		}
		return kits;
	}

	private static async ValueTask<LocalizedNameLoadResult> LoadLocalizedArchiveNamesAsync(
		string gameDataDirectory,
		GameItemAssetCatalog? catalog,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		var sourceFingerprint = await ComputeLocalizationSourceFingerprintAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
		if (LocalizedNameCache.TryGetValue(gameDataDirectory, out var cached)
			&& string.Equals(cached.SourceFingerprint, sourceFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			return new LocalizedNameLoadResult(cached.SourceFingerprint, cached.Names, cached.Tables, cached.ArchiveByPath);
		}

		var requiredIds = (catalog?.Items ?? Array.Empty<GameItemAssetRecord>())
			.SelectMany(item => new[] { item.NameCasedStringId, item.NameUpperStringId })
			.Where(id => id != 0)
			.ToHashSet();
		var names = new List<GameDataAuthorityArchiveName>();
		var tables = new List<GameDataStringTable>();
		IReadOnlyDictionary<ulong, ulong> archiveByPath = new Dictionary<ulong, ulong>();
		try
		{
			var resolver = new GameDataPackageResolver(gameDataDirectory);
			var package = await resolver.GetPackageTocAsync(StringsArchiveName, cancellationToken).ConfigureAwait(false);
			if (package is null)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"GameDataAuthorityStringsUnavailable",
					$"未找到游戏内 strings 资源包 {StringsArchiveName}；装备名称将保留为字符串 ID。",
					Path.Combine(gameDataDirectory, StringsArchiveName)));
				return CacheLocalizedNames(gameDataDirectory, sourceFingerprint, names, tables, archiveByPath);
			}

			var entries = new AdaptationPatchTocScanner().ScanEntries(
				package.Data,
				StringsArchiveName,
				package.UsesSlimEntryOffset);
			var hashLookupEntry = entries.FirstOrDefault(entry => entry.AssetKey.TypeId == HashLookupTypeId
				&& entry.AssetKey.FileId == HashLookupFileId);
			if (hashLookupEntry is null)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"GameDataAuthorityHashLookupUnavailable",
					"未找到已验证的 hash_lookup Archive 映射；武器和载具定制名称将暂不写入。",
					StringsArchiveName));
			}
			else
			{
				var hashLookupPayload = await resolver.GetPackageResourceAsync(
					StringsArchiveName,
					hashLookupEntry.TocDataOffset,
					hashLookupEntry.TocDataSize,
					cancellationToken).ConfigureAwait(false);
				if (hashLookupPayload is null)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"GameDataAuthorityHashLookupPayloadMissing",
						"无法读取 hash_lookup 的 TOC 数据；武器和载具定制名称将暂不写入。",
						StringsArchiveName));
				}
				else
				{
					// 读不出来只是"武器和载具定制名称暂不写入"，用返回值表达就够了。
					// 这里以前包着 try/catch，而读取器内部靠抛异常试探载荷起点，
					// 于是一次刷新就会抛几千个异常——调试器下每个都会打印，界面直接卡死。
					if (GameDataHashLookupReader.TryReadArchiveByPath(hashLookupPayload, out var resolved, out var hashLookupError))
					{
						archiveByPath = resolved;
					}
					else
					{
						issues.Add(new CoreIssue(
							CoreIssueSeverity.Warning,
							"GameDataAuthorityHashLookupInvalid",
							$"无法验证 hash_lookup Archive 映射：{hashLookupError}",
							StringsArchiveName));
					}
				}
			}

			var stringEntries = entries.Where(entry => entry.AssetKey.TypeId == GameDataStringsReader.AssetTypeId).ToArray();
			foreach (var entry in stringEntries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var payload = await resolver.GetPackageResourceAsync(
					StringsArchiveName,
					entry.TocDataOffset,
					entry.TocDataSize,
					cancellationToken).ConfigureAwait(false);
				if (payload is null)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"GameDataAuthorityStringsPayloadMissing",
						$"无法读取 strings Asset 0x{entry.AssetKey.FileId:x16} 的 TOC 数据。",
						StringsArchiveName));
					continue;
				}
				// The package currently contains one legacy/non-table entry sharing
				// the strings type id. It is not a malformed localized string table,
				// so do not surface it as a GameData warning or let it poison an
				// otherwise valid multi-language snapshot.
				if (payload.Length < 16 || BitConverter.ToUInt32(payload, 0) != GameDataStringsReader.Magic)
				{
					continue;
				}

				GameDataStringTable table;
				try
				{
					table = GameDataStringsReader.Read(payload, entry.AssetKey.FileId);
				}
				catch (InvalidDataException exception)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"GameDataAuthorityStringsInvalid",
						$"无法解析 strings Asset 0x{entry.AssetKey.FileId:x16}：{exception.Message}",
						StringsArchiveName,
						ExceptionMessage: exception.ToString()));
					continue;
				}
				tables.Add(table);

				if (table.Issues.Count != 0)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"GameDataAuthorityStringsPartial",
						$"strings Asset 0x{entry.AssetKey.FileId:x16} 有 {table.Issues.Count} 个条目无法读取；可用名称仍会继续使用。",
						StringsArchiveName));
				}

				if (catalog is null)
				{
					continue;
				}

				foreach (var item in catalog.Items)
				{
					var value = ResolveLocalizedName(table, item, requiredIds);
					if (!IsReadableName(value)) continue;
					names.Add(new GameDataAuthorityArchiveName(
						item.ArchiveHash.ToString("x16"),
						item.Id,
						table.Locale,
						value!,
						LocalizedNameSource,
						100));
				}
			}
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or OverflowException
			or EndOfStreamException
			or KeyNotFoundException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"GameDataAuthorityStringsReadFailed",
				$"读取游戏内多语言名称失败，将继续使用其他名称来源：{exception.Message}",
				StringsArchiveName,
				ExceptionMessage: exception.ToString()));
		}

		var distinct = names
			.GroupBy(name => (name.ArchiveId, name.ItemId, name.Locale), StringTupleComparer.Instance)
			.Select(group => group.OrderByDescending(name => name.Confidence).ThenBy(name => name.DisplayName, StringComparer.Ordinal).First())
			.OrderBy(name => name.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(name => name.ItemId)
			.ThenBy(name => name.Locale, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return CacheLocalizedNames(gameDataDirectory, sourceFingerprint, distinct, tables, archiveByPath);
	}

	private static LocalizedNameLoadResult CacheLocalizedNames(
		string gameDataDirectory,
		string sourceFingerprint,
		IReadOnlyList<GameDataAuthorityArchiveName> names,
		IReadOnlyList<GameDataStringTable> tables,
		IReadOnlyDictionary<ulong, ulong> archiveByPath)
	{
		var cache = new LocalizedNameCacheEntry(
			sourceFingerprint,
			names.ToArray(),
			tables.ToArray(),
			new Dictionary<ulong, ulong>(archiveByPath));
		LocalizedNameCache[gameDataDirectory] = cache;
		return new LocalizedNameLoadResult(cache.SourceFingerprint, cache.Names, cache.Tables, cache.ArchiveByPath);
	}

	private static async ValueTask<GeneratedEntityLoadResult> LoadGeneratedEntityFactsAsync(
		string gameDataDirectory,
		string path,
		IReadOnlyList<GameDataStringTable> tables,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendFingerprintValue(hash, GeneratedEntityEncyclopediaReader.ParserVersion);
		AppendFileFingerprint(hash, path);
		var sourceFingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

		if (!File.Exists(path))
		{
			return new GeneratedEntityLoadResult(sourceFingerprint, Array.Empty<GameDataAuthorityEntityFact>());
		}

		GeneratedEntityEncyclopediaCatalog catalog;
		if (GeneratedEntityCache.TryGetValue(path, out var cached)
			&& string.Equals(cached.SourceFingerprint, sourceFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			catalog = cached.Catalog;
		}
		else
		{
			var decoded = await TryReadGeneratedEntitiesAsync(path, cancellationToken).ConfigureAwait(false);
			if (decoded.Error is not null || decoded.Bytes is null)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"GameDataAuthorityGeneratedEntitiesUnreadable",
					$"无法读取游戏实体百科数据，武器本体名称将继续回退到其他名称来源：{decoded.Error?.Message ?? "解密结果为空"}",
					path,
					ExceptionMessage: decoded.Error?.ToString()));
				return new GeneratedEntityLoadResult(sourceFingerprint, Array.Empty<GameDataAuthorityEntityFact>());
			}

			try
			{
				catalog = GeneratedEntityEncyclopediaReader.Read(decoded.Bytes);
				GeneratedEntityCache[path] = new GeneratedEntityCacheEntry(sourceFingerprint, catalog);
			}
			catch (Exception exception) when (exception is InvalidDataException
				or OverflowException
				or ArgumentOutOfRangeException
				or IndexOutOfRangeException)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"GameDataAuthorityGeneratedEntitiesInvalid",
					$"无法验证游戏实体百科数据布局，武器本体名称将继续回退到其他名称来源：{exception.Message}",
					path,
					ExceptionMessage: exception.ToString()));
				return new GeneratedEntityLoadResult(sourceFingerprint, Array.Empty<GameDataAuthorityEntityFact>());
			}
		}

		var facts = new List<GameDataAuthorityEntityFact>(catalog.Entries.Count);
		foreach (var entry in catalog.Entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var localizedNames = ResolveEntityLocalizedNames(entry, tables);
			var localizedDesignations = ResolveEntityFieldLocalizedValues(entry, tables, GeneratedEntityEncyclopediaEntry.DesignationFieldIndex);
			var hasEquipmentAndWieldable = entry.HasComponent(GeneratedEntityTypeIds.EquipmentComponentData)
				&& entry.HasComponent(GeneratedEntityTypeIds.WieldableComponentData);
			var isWeaponCandidate = hasEquipmentAndWieldable
				&& (entry.HasComponent(GeneratedEntityTypeIds.WeaponDataComponentData)
					|| entry.IsWeaponLikeEquipment);
			facts.Add(new GameDataAuthorityEntityFact(
				entry.EntityId,
				new AssetKey(StingrayAssetId.UnitTypeHash, entry.UnitPath),
				entry.IsExactUnitMatch,
				isWeaponCandidate,
				entry.ComponentTypeIds,
				localizedNames,
				entry.EquipmentType,
				localizedDesignations));
		}

		return new GeneratedEntityLoadResult(sourceFingerprint, facts);
	}

	private static IReadOnlyDictionary<string, string> ResolveEntityLocalizedNames(
		GeneratedEntityEncyclopediaEntry entry,
		IReadOnlyList<GameDataStringTable> tables)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		// LocName is the canonical display label. Upper/short variants are only
		// fallbacks for entities whose canonical field is absent in a particular
		// language table. Plural is intentionally last because it is not suitable
		// for a singular Archive row.
		var preferredFields = new[] { 0, 2, 3, 4, 1 };
		foreach (var localeTables in tables
			.GroupBy(table => table.Locale, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
		{
			foreach (var field in preferredFields)
			{
				if (field >= entry.NameStringIds.Count) continue;
				var value = ResolveEntityFieldLocalizedValue(entry, localeTables, field);
				if (IsReadableName(value))
				{
					result[localeTables.Key] = value!;
					break;
				}
			}
		}

		return result;
	}

	private static IReadOnlyDictionary<string, string> ResolveEntityFieldLocalizedValues(
		GeneratedEntityEncyclopediaEntry entry,
		IReadOnlyList<GameDataStringTable> tables,
		int field)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var localeTables in tables
			.GroupBy(table => table.Locale, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
		{
			var value = ResolveEntityFieldLocalizedValue(entry, localeTables, field);
			if (IsReadableName(value))
			{
				result[localeTables.Key] = value!;
			}
		}

		return result;
	}

	private static string? ResolveEntityFieldLocalizedValue(
		GeneratedEntityEncyclopediaEntry entry,
		IEnumerable<GameDataStringTable> localeTables,
		int field)
	{
		if (field < 0 || field >= entry.NameStringIds.Count) return null;
		var id = entry.NameStringIds[field];
		if (id == 0) return null;
		return localeTables
			.OrderBy(table => table.FileId)
			.Select(table => table.TryGet(id, out var value) ? value : null)
			.FirstOrDefault(IsReadableName);
	}

	private static async ValueTask<SupplementalNameLoadResult> LoadSupplementalArchiveNamesAsync(
		string gameDataDirectory,
		LocalizedNameLoadResult localized,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendFingerprintValue(hash, "game-data-supplemental-archive-names-v1");
		var gameDirectory = Path.Combine(gameDataDirectory, "game");
		var weaponPath = Path.Combine(gameDirectory, GeneratedWeaponCustomizationSettingsName);
		var unitPath = Path.Combine(gameDirectory, GeneratedUnitCustomizationSettingsName);
		AppendFileFingerprint(hash, weaponPath);
		AppendFileFingerprint(hash, unitPath);

		if (localized.ArchiveByPath.Count == 0)
		{
			return new SupplementalNameLoadResult(
				Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
				Array.Empty<GameDataAuthorityArchiveName>());
		}

		var names = new List<GameDataAuthorityArchiveName>();
		await AppendWeaponCustomizationNamesAsync(weaponPath, localized, names, issues, cancellationToken).ConfigureAwait(false);
		await AppendUnitCustomizationNamesAsync(unitPath, localized, names, issues, cancellationToken).ConfigureAwait(false);
		return new SupplementalNameLoadResult(
			Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
			names);
	}

	private static async Task AppendWeaponCustomizationNamesAsync(
		string path,
		LocalizedNameLoadResult localized,
		ICollection<GameDataAuthorityArchiveName> names,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path)) return;
		var decoded = await TryReadEncryptedDataLibraryAsync(path, cancellationToken).ConfigureAwait(false);
		if (decoded.Error is not null || decoded.Bytes is null)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"GameDataAuthorityWeaponSettingsUnreadable",
				$"无法读取武器定制名称表：{decoded.Error?.Message ?? "解密结果为空"}",
				path,
				ExceptionMessage: decoded.Error?.ToString()));
			return;
		}

		try
		{
			var references = GeneratedWeaponCustomizationSettingsReader.Read(decoded.Bytes);
			if (references.Count == 0)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"GameDataAuthorityWeaponSettingsUnsupported",
					"武器定制名称表未产生可验证条目；该游戏版本可能已调整数据布局。",
					path));
				return;
			}
			foreach (var name in ResolveSupplementalNames(references, localized, WeaponNameSource, "Weapon customization"))
			{
				names.Add(name);
			}
		}
		catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"GameDataAuthorityWeaponSettingsInvalid",
				$"无法验证武器定制名称表：{exception.Message}",
				path,
				ExceptionMessage: exception.ToString()));
		}
	}

	private static async Task AppendUnitCustomizationNamesAsync(
		string path,
		LocalizedNameLoadResult localized,
		ICollection<GameDataAuthorityArchiveName> names,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path)) return;
		var decoded = await TryReadEncryptedDataLibraryAsync(path, cancellationToken).ConfigureAwait(false);
		if (decoded.Error is not null || decoded.Bytes is null)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"GameDataAuthorityUnitSettingsUnreadable",
				$"无法读取单位定制名称表：{decoded.Error?.Message ?? "解密结果为空"}",
				path,
				ExceptionMessage: decoded.Error?.ToString()));
			return;
		}

		try
		{
			var references = GeneratedUnitCustomizationSettingsReader.Read(decoded.Bytes);
			if (references.Count == 0)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"GameDataAuthorityUnitSettingsUnsupported",
					"单位定制名称表未产生可验证条目；该游戏版本可能已调整数据布局。",
					path));
				return;
			}
			foreach (var name in ResolveSupplementalNames(references, localized, UnitNameSource, "Vehicle customization"))
			{
				names.Add(name);
			}
		}
		catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"GameDataAuthorityUnitSettingsInvalid",
				$"无法验证单位定制名称表：{exception.Message}",
				path,
				ExceptionMessage: exception.ToString()));
		}
	}

	private static IEnumerable<GameDataAuthorityArchiveName> ResolveSupplementalNames(
		IReadOnlyList<GameDataCustomizationArchiveReference> references,
		LocalizedNameLoadResult localized,
		string source,
		string category)
	{
		foreach (var reference in references)
		{
			if (!localized.ArchiveByPath.TryGetValue(reference.ArchivePathHash, out var archiveId))
			{
				continue;
			}

			var resolvedAnyLocale = false;
			foreach (var table in localized.Tables)
			{
				var displayName = ResolveLocalizedName(table, reference);
				if (!IsReadableName(displayName)) continue;
				resolvedAnyLocale = true;
				yield return new GameDataAuthorityArchiveName(
					archiveId.ToString("x16"),
					reference.ItemId,
					table.Locale,
					displayName!,
					source,
					95,
					category);
			}

			if (!resolvedAnyLocale && IsReadableSupplementalDirectName(reference.FallbackDisplayName))
			{
				yield return new GameDataAuthorityArchiveName(
					archiveId.ToString("x16"),
					reference.ItemId,
					"en-US",
					reference.FallbackDisplayName!,
					$"{source}+direct",
					70,
					category);
			}
			else if (!resolvedAnyLocale && IsReadableSupplementalDebugName(reference.DebugName))
			{
				yield return new GameDataAuthorityArchiveName(
					archiveId.ToString("x16"),
					reference.ItemId,
					"en-US",
					reference.DebugName!,
					$"{source}+debug",
					20,
					category);
			}
		}
	}

	private static string? ResolveLocalizedName(
		GameDataStringTable table,
		GameItemAssetRecord item,
		IReadOnlySet<uint> requiredIds)
	{
		foreach (var id in new[] { item.NameCasedStringId, item.NameUpperStringId })
		{
			if (id != 0 && requiredIds.Contains(id) && table.TryGet(id, out var value) && IsReadableName(value))
				return value;
		}
		return null;
	}

	private static string? ResolveLocalizedName(
		GameDataStringTable table,
		GameDataCustomizationArchiveReference reference)
	{
		foreach (var id in new[] { reference.NameCasedStringId, reference.NameUpperStringId })
		{
			if (id != 0 && table.TryGet(id, out var value) && IsReadableName(value))
			{
				return value;
			}
		}
		return null;
	}

	private static async ValueTask<string> ComputeLocalizationSourceFingerprintAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendFingerprintValue(hash, "game-data-localized-names-v1");
		var languageSettings = Path.Combine(gameDataDirectory, "game", LanguageSettingsName);
		AppendFileFingerprint(hash, languageSettings);
		// 注册表文件不参与指纹（它可能被用户以任何方式改过）。其余文件照常。
		AppendFileFingerprint(hash, languageSettings);
		try
		{
			var resolver = new GameDataPackageResolver(gameDataDirectory);
			AppendFingerprintValue(hash, await resolver.GetPackageFingerprintAsync(StringsArchiveName, cancellationToken).ConfigureAwait(false));
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException)
		{
			AppendFingerprintValue(hash, $"strings-package-fingerprint-error:{exception.GetType().Name}");
		}
		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private static void AppendFileFingerprint(IncrementalHash hash, string path)
	{
		if (!File.Exists(path))
		{
			AppendFingerprintValue(hash, $"{Path.GetFileName(path)}=missing");
			return;
		}
		var file = new FileInfo(path);
		AppendFingerprintValue(hash, $"{file.Name}={file.Length}:{file.LastWriteTimeUtc.Ticks}");
	}

	private static void AppendFingerprintValue(IncrementalHash hash, string value)
	{
		hash.AppendData(System.Text.Encoding.UTF8.GetBytes(value));
		hash.AppendData(new byte[] { 0 });
	}

	private static bool IsReadableName(string? value)
		=> !string.IsNullOrWhiteSpace(value)
			&& !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			&& !value.StartsWith("hash-", StringComparison.OrdinalIgnoreCase);

	private static bool IsReadableSupplementalDebugName(string? value)
		=> value is not null
			&& IsReadableName(value)
			&& !value.StartsWith("nui_", StringComparison.OrdinalIgnoreCase)
			&& value.All(character => !char.IsControl(character))
			&& value.Any(char.IsLetter);

	private static bool IsReadableSupplementalDirectName(string? value)
		=> value is not null
			&& IsReadableName(value)
			&& !value.StartsWith("nui_", StringComparison.OrdinalIgnoreCase)
			&& value.Length <= 160
			&& value.All(character => !char.IsControl(character))
			&& value.Any(char.IsLetter);

	private static string PickName(
		IEnumerable<GameDataAuthorityArchiveName> names,
		string preferredLocale,
		string fallback)
	{
		var ordered = names
			.Where(name => IsReadableName(name.DisplayName))
			.OrderByDescending(name => name.Confidence)
			.ThenBy(name => name.ItemId)
			.ThenBy(name => name.DisplayName, StringComparer.Ordinal)
			.ToArray();
		var exact = ordered.FirstOrDefault(name => string.Equals(name.Locale, preferredLocale, StringComparison.OrdinalIgnoreCase));
		if (exact is not null) return exact.DisplayName;

		var language = preferredLocale.Split('-', 2)[0];
		var neutral = ordered.FirstOrDefault(name => name.Locale.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
		if (neutral is not null) return neutral.DisplayName;

		var english = ordered.FirstOrDefault(name => name.Locale.Equals("en-US", StringComparison.OrdinalIgnoreCase)
			|| name.Locale.Equals("en-GB", StringComparison.OrdinalIgnoreCase));
		return english?.DisplayName ?? ordered.FirstOrDefault()?.DisplayName ?? fallback;
	}

	private static string? NormalizeLocale(string? locale)
	{
		if (string.IsNullOrWhiteSpace(locale)) return null;
		try
		{
			return CultureInfo.GetCultureInfo(locale.Trim()).Name;
		}
		catch (CultureNotFoundException)
		{
			return locale.Trim();
		}
	}

	private static GameDataEquipmentKitKind ToKitKind(uint value)
		=> value switch
		{
			CustomizationKitType.Armor => GameDataEquipmentKitKind.Armor,
			CustomizationKitType.Helmet => GameDataEquipmentKitKind.Helmet,
			CustomizationKitType.Cape => GameDataEquipmentKitKind.Cape,
			_ => GameDataEquipmentKitKind.Unknown,
		};

	// 身形/部位的唯一换算在 ArmorStructure 里，这里只做转发——不要再写第二份 switch。
	private static UnitMeshBodyVariant ToBodyVariant(uint rawBodyType)
		=> ArmorStructure.ToBodyVariant(rawBodyType);

	private static UnitMeshPartKind ToPartKind(uint slot)
		=> ArmorStructure.ToPartKind(slot);

	private static string ComputeFingerprint(
		GameDataAuthoritySourceKind sourceKind,
		ReadOnlySpan<byte> data,
		string? localizedSourceFingerprint = null,
		string? supplementalSourceFingerprint = null,
		string? generatedEntitySourceFingerprint = null)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"game-data-authority-v3:{sourceKind}:"));
		hash.AppendData(data);
		if (!string.IsNullOrWhiteSpace(localizedSourceFingerprint))
		{
			hash.AppendData(System.Text.Encoding.UTF8.GetBytes("\0localized:"));
			hash.AppendData(System.Text.Encoding.UTF8.GetBytes(localizedSourceFingerprint));
		}
		if (!string.IsNullOrWhiteSpace(supplementalSourceFingerprint))
		{
			hash.AppendData(System.Text.Encoding.UTF8.GetBytes("\0supplemental:"));
			hash.AppendData(System.Text.Encoding.UTF8.GetBytes(supplementalSourceFingerprint));
		}
		if (!string.IsNullOrWhiteSpace(generatedEntitySourceFingerprint))
		{
			hash.AppendData(System.Text.Encoding.UTF8.GetBytes("\0generated-entities:"));
			hash.AppendData(System.Text.Encoding.UTF8.GetBytes(generatedEntitySourceFingerprint));
		}
		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private sealed record LocalizedNameCacheEntry(
		string SourceFingerprint,
		IReadOnlyList<GameDataAuthorityArchiveName> Names,
		IReadOnlyList<GameDataStringTable> Tables,
		IReadOnlyDictionary<ulong, ulong> ArchiveByPath);

	private sealed record LocalizedNameLoadResult(
		string SourceFingerprint,
		IReadOnlyList<GameDataAuthorityArchiveName> Names,
		IReadOnlyList<GameDataStringTable> Tables,
		IReadOnlyDictionary<ulong, ulong> ArchiveByPath);

	private sealed record SupplementalNameLoadResult(
		string SourceFingerprint,
		IReadOnlyList<GameDataAuthorityArchiveName> Names);

	private sealed record GeneratedEntityCacheEntry(
		string SourceFingerprint,
		GeneratedEntityEncyclopediaCatalog Catalog);

	private sealed record GeneratedEntityLoadResult(
		string SourceFingerprint,
		IReadOnlyList<GameDataAuthorityEntityFact> Facts);

	private sealed class StringTupleComparer : IEqualityComparer<(string ArchiveId, uint ItemId, string Locale)>
	{
		public static StringTupleComparer Instance { get; } = new();

		public bool Equals((string ArchiveId, uint ItemId, string Locale) x, (string ArchiveId, uint ItemId, string Locale) y)
			=> x.ItemId == y.ItemId
				&& string.Equals(x.ArchiveId, y.ArchiveId, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(x.Locale, y.Locale, StringComparison.OrdinalIgnoreCase);

		public int GetHashCode((string ArchiveId, uint ItemId, string Locale) obj)
			=> HashCode.Combine(
				StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ArchiveId),
				obj.ItemId,
				StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Locale));
	}

	private sealed class FullNameComparer : IEqualityComparer<(string ArchiveId, uint ItemId, string Locale, string DisplayName, string Source)>
	{
		public static FullNameComparer Instance { get; } = new();

		public bool Equals(
			(string ArchiveId, uint ItemId, string Locale, string DisplayName, string Source) x,
			(string ArchiveId, uint ItemId, string Locale, string DisplayName, string Source) y)
			=> x.ItemId == y.ItemId
				&& string.Equals(x.ArchiveId, y.ArchiveId, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(x.Locale, y.Locale, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(x.DisplayName, y.DisplayName, StringComparison.Ordinal)
				&& string.Equals(x.Source, y.Source, StringComparison.Ordinal);

		public int GetHashCode((string ArchiveId, uint ItemId, string Locale, string DisplayName, string Source) obj)
			=> HashCode.Combine(
				StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ArchiveId),
				obj.ItemId,
				StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Locale),
				StringComparer.Ordinal.GetHashCode(obj.DisplayName),
				StringComparer.Ordinal.GetHashCode(obj.Source));
	}
}
