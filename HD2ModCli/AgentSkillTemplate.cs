namespace HD2ModCli;

public static class AgentSkillTemplate
{
	public static string Create()
	{
		var assembly = typeof(AgentSkillTemplate).Assembly;
		using var stream = assembly.GetManifestResourceStream("HD2ModCli.SKILL.md")
			?? throw new InvalidOperationException("The embedded HD2Mod CLI skill document is missing.");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}
}
