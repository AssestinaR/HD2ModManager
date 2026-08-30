namespace HD2ModCli;

// Compatibility façade for the original `agent skill --output` command.
// New callers should use `agent skills list/export` to request only a needed capability.
public static class AgentSkillTemplate
{
	public static string Create() => CliSkillCatalog.GetRouterContent();
}
