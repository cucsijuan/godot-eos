using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GodotEos;

/// <summary>
/// A product's keys, from Epic's developer portal. They belong to the game, not to this library, and should stay out of
/// public repositories: read them from a local file or the environment, or have the build write them into the game.
/// </summary>
/// <param name="EncryptionKey">64 hex characters the player's cloud files are encrypted with (empty: none). It must never
/// change once players have saved with it.</param>
public sealed record EosKeys(string ProductId, string SandboxId, string DeploymentId, string ClientId, string ClientSecret,
    string EncryptionKey = "")
{
    /// <summary>Every key the platform needs is there.</summary>
    public bool IsComplete => new[] { ProductId, SandboxId, DeploymentId, ClientId, ClientSecret }.All(k => !string.IsNullOrEmpty(k));

    /// <summary>A JSON file with productId, sandboxId, deploymentId, clientId, clientSecret and (optional) encryptionKey.</summary>
    public static EosKeys FromJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        string Get(string name) => doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        return new EosKeys(Get("productId"), Get("sandboxId"), Get("deploymentId"), Get("clientId"), Get("clientSecret"),
            Get("encryptionKey"));
    }

    /// <summary>EOS_PRODUCT_ID, EOS_SANDBOX_ID, EOS_DEPLOYMENT_ID, EOS_CLIENT_ID, EOS_CLIENT_SECRET, EOS_ENCRYPTION_KEY.</summary>
    public static EosKeys FromEnvironment()
    {
        static string Get(string name) => Environment.GetEnvironmentVariable(name) ?? "";
        return new EosKeys(Get("EOS_PRODUCT_ID"), Get("EOS_SANDBOX_ID"), Get("EOS_DEPLOYMENT_ID"), Get("EOS_CLIENT_ID"),
            Get("EOS_CLIENT_SECRET"), Get("EOS_ENCRYPTION_KEY"));
    }
}
