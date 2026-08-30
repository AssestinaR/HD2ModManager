namespace HD2ModCli;

public sealed class CliUserException : Exception
{
	public CliUserException(string code, string message)
		: base(message)
	{
		Code = code;
	}

	public string Code { get; }
}
