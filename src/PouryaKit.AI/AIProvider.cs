namespace PouryaKit.AI;

/// <summary>The provider a Host's chat client reaches, each through the OpenAI client and its chat completions API.</summary>
public enum AIProvider
{
    /// <summary>OpenAI, at <c>https://api.openai.com/v1</c> unless an endpoint is set; the key is required.</summary>
    OpenAI,

    /// <summary>
    /// An Azure OpenAI resource through its v1 endpoint, such as <c>https://my-resource.openai.azure.com/openai/v1/</c>;
    /// the model is a deployment's name and the key is required.
    /// </summary>
    AzureOpenAI,

    /// <summary>An Ollama server through its OpenAI-compatible API, such as <c>http://localhost:11434/v1</c>; no key.</summary>
    Ollama,
}
