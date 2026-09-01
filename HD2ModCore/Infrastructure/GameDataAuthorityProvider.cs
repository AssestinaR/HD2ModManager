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
// installed plaintext library is authoritative when it is valid; the encrypted
// mirror is loaded only when the plaintext file is unavailable or unreadable.
public sealed class GameDataAuthorityProvider : IGameDataAuthorityProvider
{
	private const string GeneratedLibraryName = "generated_customization_armor_sets";
	private const string EncryptedLibraryName = "generated_customization_armor_sets.dl_bin";
	private const string GeneratedWeaponCustomizationSettingsName = "generated_weapon_customization_settings.dl_bin";
	private const string GeneratedUnitCustomizationSettingsName = "generated_unit_customization_settings.dl_bin";
	private const string StringsArchiveName = "9ba626afa44a3aa3";
	private const string LanguageSettingsName = "generated_language_settings.dl_bin";
	private const ulong HashLookupTypeId = 0xe3f2851035957af5;
	private const ulong HashLookupFileId = 0x7056bc19c69f0f07;
	private const string LocalizedNameSource = "GameData.generated_customization_armor_sets+strings";
	private const string WeaponNameSource = "GameData.generated_weapon_customization_settings+strings";
	private const string UnitNameSource = "GameData.generated_unit_customization_settings+strings";
	private static readonly ConcurrentDictionary<string, LocalizedNameCacheEntry> LocalizedNameCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly string _preferredLocale;

	public GameDataAuthorityProvider(string? preferredLocale = null)
	{
		_preferredLocale = NormalizeLocale(preferredLocale)
			?? NormalizeLocale(CultureInfo.CurrentUICulture.Name)
			?? "en-US";
	}

	public async ValueTask<GameDataAuthoritySnapshot> LoadAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		var dataRoot = Path.GetFullPath(gameDataDirectory);
		var gameRoot = Path.Combine(dataRoot, "game");
		var plaintextPath = Path.Combine(gameRoot, GeneratedLibraryName);
		var encryptedPath = Path.Combine(gameRoot, EncryptedLibraryName);
		var issues = new List<CoreIssue>();

		GameItemAssetCatalog? catalog = null;
		byte[]? authoritativeBytes = null;
		GameDataAuthoritySourceKind sourceKind = GameDataAuthoritySourceKind.Unavailable;
		string? sourcePath = null;

		if (File.Exists(plaintextPath))
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
					$"无法读取游戏明文装备关系表，将尝试加密镜像：{exception.Message}",
					plaintextPath,
					ExceptionMessage: exception.ToString()));
			}
		}

		if (catalog is null && File.Exists(encryptedPath))
		{
			var fallback = await TryReadEncryptedDataLibraryAsync(encryptedPath, cancellationToken).ConfigureAwait(false);
			if (fallback.Error is null && fallback.Bytes is not null)
			{
				try
				{
					catalog = CustomizationArmorSetsReader.Read(fallback.Bytes);
					authoritativeBytes = fallback.Bytes;
					sourceKind = GameDataAuthoritySourceKind.DecryptedDataLibrary;
					sourcePath = encryptedPath;
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
						encryptedPath,
						ExceptionMessage: exception.ToString()));
				}
			}
			else if (fallback.Error is not null)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"GameDataAuthorityEncryptedFallbackFailed",
					$"无法解密游戏装备关系表：{fallback.Error.Message}",
					encryptedPath,
					ExceptionMessage: fallback.Error.ToString()));
			}
		}

		if (catalog is null || authoritativeBytes is null)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"GameDataAuthorityUnavailable",
				"未找到可验证的游戏装备关系表；原生装备将暂时回退到 Unit 结构事实和社区显示资料。",
				File.Exists(plaintextPath) ? plaintextPath : encryptedPath));
			return new GameDataAuthoritySnapshot(
				GameDataAuthoritySourceKind.Unavailable,
				null,
				ComputeFingerprint(GameDataAuthoritySourceKind.Unavailable, Array.Empty<byte>()),
				DateTimeOffset.UtcNow,
				Array.Empty<GameDataAuthorityKit>(),
				issues)
			{
				PreferredLocale = _preferredLocale
			};
		}

		var localized = await LoadLocalizedArchiveNamesAsync(dataRoot, catalog, issues, cancellationToken).ConfigureAwait(false);
		var supplemental = await LoadSupplementalArchiveNamesAsync(dataRoot, localized, issues, cancellationToken).ConfigureAwait(false);
		var allNames = localized.Names.Concat(supplemental.Names)
			.GroupBy(name => (name.ArchiveId, name.ItemId, name.Locale, name.DisplayName, name.Source), FullNameComparer.Instance)
			.Select(group => group.OrderByDescending(name => name.Confidence).First())
			.OrderBy(name => name.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(name => name.ItemId)
			.ThenBy(name => name.Locale, StringComparer.OrdinalIgnoreCase)
			.ThenBy(name => name.DisplayName, StringComparer.Ordinal)
			.ToArray();
		var kits = Project(catalog, issues, allNames);
		return new GameDataAuthoritySnapshot(
			sourceKind,
			sourcePath,
			ComputeFingerprint(sourceKind, authoritativeBytes, localized.SourceFingerprint, supplemental.SourceFingerprint),
			DateTimeOffset.UtcNow,
			kits,
			issues)
		{
			PreferredLocale = _preferredLocale,
			ArchiveNames = allNames
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
		GameItemAssetCatalog catalog,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		var sourceFingerprint = await ComputeLocalizationSourceFingerprintAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
		if (LocalizedNameCache.TryGetValue(gameDataDirectory, out var cached)
			&& string.Equals(cached.SourceFingerprint, sourceFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			return new LocalizedNameLoadResult(cached.SourceFingerprint, cached.Names, cached.Tables, cached.ArchiveByPath);
		}

		var requiredIds = catalog.Items
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
					try
					{
						archiveByPath = GameDataHashLookupReader.ReadArchiveByPath(hashLookupPayload);
					}
					catch (Exception exception) when (exception is InvalidDataException
						or OverflowException
						or ArgumentOutOfRangeException)
					{
						issues.Add(new CoreIssue(
							CoreIssueSeverity.Warning,
							"GameDataAuthorityHashLookupInvalid",
							$"无法验证 hash_lookup Archive 映射：{exception.Message}",
							StringsArchiveName,
							ExceptionMessage: exception.ToString()));
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

	// Confirmed against the installed generated library and the current Unit customization facts.
	private static UnitMeshBodyVariant ToBodyVariant(uint rawBodyType)
		=> rawBodyType switch
		{
			0 => UnitMeshBodyVariant.Stocky,
			1 => UnitMeshBodyVariant.Slim,
			3 => UnitMeshBodyVariant.Any,
			_ => UnitMeshBodyVariant.Other,
		};

	private static UnitMeshPartKind ToPartKind(uint slot)
		=> slot switch
		{
			CustomizationKitSlot.Helmet => UnitMeshPartKind.Head,
			CustomizationKitSlot.Torso => UnitMeshPartKind.Torso,
			CustomizationKitSlot.Hips => UnitMeshPartKind.Pelvis,
			CustomizationKitSlot.LeftLeg => UnitMeshPartKind.LeftLeg,
			CustomizationKitSlot.RightLeg => UnitMeshPartKind.RightLeg,
			CustomizationKitSlot.LeftArm => UnitMeshPartKind.LeftArm,
			CustomizationKitSlot.RightArm => UnitMeshPartKind.RightArm,
			CustomizationKitSlot.LeftShoulder => UnitMeshPartKind.LeftShoulder,
			CustomizationKitSlot.RightShoulder => UnitMeshPartKind.RightShoulder,
			CustomizationKitSlot.Cape => UnitMeshPartKind.Accessory,
			_ => UnitMeshPartKind.Unknown,
		};

	private static string ComputeFingerprint(
		GameDataAuthoritySourceKind sourceKind,
		ReadOnlySpan<byte> data,
		string? localizedSourceFingerprint = null,
		string? supplementalSourceFingerprint = null)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"game-data-authority-v2:{sourceKind}:"));
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
