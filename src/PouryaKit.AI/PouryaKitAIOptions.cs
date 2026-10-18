namespace PouryaKit.AI;

/// <summary>
/// The model a Host's runs use, the provider that serves it, and what a run may do, bound by the Host from its
/// configuration, such as the section <c>PouryaKit:AI</c>, and validated when the Host starts.
/// </summary>
public sealed class PouryaKitAIOptions
{
    /// <summary>The provider that serves <see cref="Model"/>; by default <see cref="AIProvider.OpenAI"/>.</summary>
    public AIProvider Provider { get; set; }

    /// <summary>
    /// The provider's OpenAI-compatible API, required for Azure OpenAI and Ollama; null for OpenAI's own.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The model every run uses, or for Azure OpenAI the deployment's name; required.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// The provider's API key, required for OpenAI and Azure OpenAI and unused by Ollama. Keep it out of source control,
    /// as with user secrets or an environment variable; the Kit never logs it.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// How many times a run may call the model and invoke the tools it asks for, at least 1; by default 10. The call
    /// after the last one has no tools, so the run ends with the model's answer.
    /// </summary>
    public int MaximumIterationsPerRun { get; set; } = 10;

    /// <summary>
    /// The names of the tools a run may be given: a tool a run is given that this list does not name is withheld from the
    /// model, so it never runs. Empty, the default, withholds every tool. A declaration the client never invokes, which it
    /// hands back to the run's caller, such as a handoff workflow's, passes unnamed.
    /// </summary>
    public ISet<string> Tools { get; } = new HashSet<string>(StringComparer.Ordinal);
}
