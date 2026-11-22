namespace Mofumofu.Launcher.Services.Agent;

public enum AgentTaskKind
{
    InstanceCheck,
    CrashDiagnosis,
}

public sealed record AgentTaskDraft(AgentTaskKind Kind, string Prompt, string Context);

public sealed record AgentTaskAttachment(AgentTaskKind Kind, string Context, string ProviderScope);