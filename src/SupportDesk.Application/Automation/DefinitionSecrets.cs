using System.Text.Json;
using System.Text.Json.Nodes;
using SupportDesk.Application.Platform;

namespace SupportDesk.Application.Automation;

/// <summary>
/// Encrypts secrets embedded in action parameters before a definition is stored: webhook signing secrets and sensitive
/// header values (Authorization, API keys, tokens). Stored values carry the "enc:" prefix and are only decrypted by the
/// job that sends the request; values already encrypted are kept as they are (the designer sends them back unchanged).
/// </summary>
public static class DefinitionSecrets
{
    public static void Protect(List<RuleAction> actions, ISecretProtector protector)
    {
        foreach (var action in actions.Where(a => a.Type == ActionTypes.InvokeWebhook && a.Params is { ValueKind: JsonValueKind.Object }))
        {
            var obj = JsonNode.Parse(action.Params!.Value.GetRawText())!.AsObject();
            var changed = false;
            var secretKey = obj.Select(p => p.Key).FirstOrDefault(k => k.Equals("secret", StringComparison.OrdinalIgnoreCase));
            if (secretKey is not null && obj[secretKey] is JsonValue sv && sv.TryGetValue<string>(out var secret) && secret.Length > 0 &&
                !secret.StartsWith(SecretValues.Prefix, StringComparison.Ordinal))
            {
                obj[secretKey] = SecretValues.Prefix + protector.Protect(secret);
                changed = true;
            }
            var headersKey = obj.Select(p => p.Key).FirstOrDefault(k => k.Equals("headers", StringComparison.OrdinalIgnoreCase));
            if (headersKey is not null && obj[headersKey] is JsonObject headers)
            {
                foreach (var name in headers.Select(h => h.Key).ToList())
                {
                    if (!SecretValues.IsSensitiveHeader(name) || headers[name] is not JsonValue hv || !hv.TryGetValue<string>(out var value)) continue;
                    if (value.Length == 0 || value.StartsWith(SecretValues.Prefix, StringComparison.Ordinal) || TemplateRenderer.HasPlaceholders(value)) continue;
                    headers[name] = SecretValues.Prefix + protector.Protect(value);
                    changed = true;
                }
            }
            if (changed) action.Params = JsonSerializer.SerializeToElement(obj);
        }
    }
}
