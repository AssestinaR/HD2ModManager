namespace HD2ModCli;

public sealed record CliGlobalOptions(
	string? ManagerRoot,
	string? Library,
	string? GameData,
	string? RegistryRoot,
	string? Output,
	string? Plan,
	string? Host,
	string? AddToProfile,
	string? Keys,
	string? AddKeys,
	string? RemoveKeys,
	string? ArchiveId,
	string? Kind,
	string? Header,
	IReadOnlyList<string> Picks,
	int? Position,
	int? Limit,
	string? Cursor,
	bool SetActive,
	bool WithUnsharedOptions,
	bool WithEffective,
	bool OnlyCompetition,
	bool Overwrite,
	bool Confirm,
	bool Json,
	bool AllowUnsafeLua = false);

public sealed record CliInvocation(IReadOnlyList<string> Command, CliGlobalOptions Options);

public static class CliArgumentParser
{
	public static CliInvocation Parse(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);
		string? managerRoot = null;
		string? library = null;
		string? gameData = null;
		string? registryRoot = null;
		string? output = null;
		string? plan = null;
		string? host = null;
		string? addToProfile = null;
		string? keys = null;
		string? addKeys = null;
		string? removeKeys = null;
		string? archiveId = null;
		string? kind = null;
		string? header = null;
		var picks = new List<string>();
		int? position = null;
		int? limit = null;
		string? cursor = null;
		var setActive = false;
		var withUnsharedOptions = false;
		var withEffective = false;
		var onlyCompetition = false;
		var overwrite = false;
		var confirm = false;
		var allowUnsafeLua = false;
		var json = false;
		var command = new List<string>();

		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index];
			switch (argument)
			{
				case "--manager-root":
					managerRoot = ReadValue(args, ref index, argument);
					break;
				case "--library":
					library = ReadValue(args, ref index, argument);
					break;
				case "--game-data":
					gameData = ReadValue(args, ref index, argument);
					break;
				case "--registry-root":
					registryRoot = ReadValue(args, ref index, argument);
					break;
				case "--output":
					output = ReadValue(args, ref index, argument);
					break;
				case "--plan":
					plan = ReadValue(args, ref index, argument);
					break;
				case "--host":
					host = ReadValue(args, ref index, argument);
					break;
				case "--add-to-profile":
					addToProfile = ReadValue(args, ref index, argument);
					break;
				case "--keys":
					keys = ReadValue(args, ref index, argument);
					break;
				case "--add-key":
					addKeys = ReadValue(args, ref index, argument);
					break;
				case "--remove-key":
					removeKeys = ReadValue(args, ref index, argument);
					break;
				case "--archive-id":
					archiveId = ReadValue(args, ref index, argument);
					break;
				case "--kind":
					kind = ReadValue(args, ref index, argument);
					break;
				case "--header":
					header = ReadValue(args, ref index, argument);
					break;
				case "--pick":
					// Positional triple: <patch.toc> <0xtypeid> <0xfileid>. Repeated.
					picks.Add(ReadValue(args, ref index, argument));
					picks.Add(ReadValue(args, ref index, argument));
					picks.Add(ReadValue(args, ref index, argument));
					break;
				case "--position":
					var rawPosition = ReadValue(args, ref index, argument);
					if (!int.TryParse(rawPosition, out var parsedPosition) || parsedPosition < 1)
						throw new CliUserException("PositionInvalid", "参数 --position 必须是从 1 开始的正整数。");
					position = parsedPosition;
					break;
				case "--limit":
					var rawLimit = ReadValue(args, ref index, argument);
					if (!int.TryParse(rawLimit, out var parsedLimit) || parsedLimit < 1 || parsedLimit > 500)
						throw new CliUserException("LimitInvalid", "参数 --limit 必须是 1 到 500 之间的整数。 ");
					limit = parsedLimit;
					break;
				case "--cursor":
					cursor = ReadValue(args, ref index, argument);
					break;
				case "--set-active":
					setActive = true;
					break;
				case "--with-unshared-options":
					withUnsharedOptions = true;
					break;
				case "--with-effective":
					withEffective = true;
					break;
				case "--only-competition":
					onlyCompetition = true;
					break;
				case "--overwrite":
					overwrite = true;
					break;
				case "--confirm":
					confirm = true;
					break;
				case "--allow-unsafe-lua":
					allowUnsafeLua = true;
					break;
				case "--json":
					json = true;
					break;
				case "--help":
				case "-h":
					command.Add("help");
					break;
				case "--version":
					command.Add("version");
					break;
				default:
					if (argument.StartsWith("-", StringComparison.Ordinal))
						throw new CliUserException("UnknownOption", $"不支持的参数：{argument}");
					command.Add(argument);
					break;
			}
		}

		if (command.Count == 0) command.Add("help");
		return new CliInvocation(command, new CliGlobalOptions(managerRoot, library, gameData, registryRoot, output, plan, host, addToProfile, keys, addKeys, removeKeys, archiveId, kind, header, picks, position, limit, cursor, setActive, withUnsharedOptions, withEffective, onlyCompetition, overwrite, confirm, json, allowUnsafeLua));
	}

	private static string ReadValue(string[] args, ref int index, string option)
	{
		if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("-", StringComparison.Ordinal))
			throw new CliUserException("MissingOptionValue", $"参数 {option} 需要一个值。");
		return args[index];
	}
}
