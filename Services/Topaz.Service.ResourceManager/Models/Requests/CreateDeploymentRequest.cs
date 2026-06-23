using System.Text.Json;
using Azure.Deployments.Core.Definitions.Schema;
using Azure.Deployments.Templates.Engines;
using JetBrains.Annotations;
using Topaz.Service.Storage;
using Topaz.Shared;

namespace Topaz.Service.ResourceManager.Models.Requests;

internal record CreateDeploymentRequest
{
    public string? Location { get; init; }
    public DeploymentProperties? Properties { get; init; }

    internal record DeploymentProperties
    {
        public string? Mode { get; init; }
        public object? Template { get; set; }

        /// <summary>
        /// Optional link to the deployment template when it is not inlined in
        /// <see cref="Template"/>. ARM clients may hand the
        /// template by URI rather than inline it; <see cref="ResolveTemplateLinkIfNeeded"/>
        /// reads it back into <see cref="Template"/>.
        /// </summary>
        public TemplateLink? TemplateLink { get; set; }

        /// <summary>
        /// Raw JSON for the deployment parameters — either inline format
        /// <c>{"param1":{"value":"..."}}</c> or parameter-file format
        /// <c>{"$schema":"...","parameters":{"param1":{"value":"..."}}}</c>.
        /// Use <see cref="GetParameterValues"/> to extract the flat dictionary.
        /// </summary>
        public JsonElement? Parameters { get; set; }

        public Dictionary<string, ParameterValue>? GetParameterValues()
        {
            if (Parameters == null || Parameters.Value.ValueKind != JsonValueKind.Object)
                return null;

            var element = Parameters.Value;

            // Parameter-file format: {"$schema":"...","contentVersion":"...","parameters":{...}}
            if (element.TryGetProperty("parameters", out var nestedParameters) &&
                nestedParameters.ValueKind == JsonValueKind.Object)
            {
                return nestedParameters.Deserialize<Dictionary<string, ParameterValue>>(GlobalSettings.JsonOptions);
            }

            // Inline format: {"param1":{"value":"..."}, ...}
            return element.Deserialize<Dictionary<string, ParameterValue>>(GlobalSettings.JsonOptions);
        }
    }

    internal record DeploymentParameters
    {
        [UsedImplicitly] public string? Schema { get; set; }
        [UsedImplicitly] public string? ContentVersion { get; set; }
        public Dictionary<string, ParameterValue>? Parameters { get; set; }
    }

    internal record ParameterValue
    {
        public object? Value { get; set; }

        public override string ToString() => Value == null ? "null" : System.Text.Json.JsonSerializer.Serialize(Value);
    }

    internal record TemplateLink
    {
        public string? Uri { get; set; }
    }

    /// <summary>
    /// When the template is supplied by link rather than inlined, reads the linked
    /// blob (served by this same emulator) into <see cref="DeploymentProperties.Template"/>
    /// so the rest of the deployment pipeline sees a normal inline template. No-op when a
    /// template is already present, no link is given, or the link cannot be resolved.
    /// </summary>
    public void ResolveTemplateLinkIfNeeded(ITopazLogger logger)
    {
        if (Properties?.Template != null)
        {
            return;
        }

        var link = Properties?.TemplateLink?.Uri;
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return;
        }

        var content = BlobArtifactReader.TryReadBlobText(uri, logger);
        if (!string.IsNullOrWhiteSpace(content) && Properties != null)
        {
            // Store the linked template as a JSON object (JsonElement), matching an
            // inlined template. A raw string would be double-encoded by
            // JsonSerializer.Serialize on the create path and fail to bind to the
            // typed template downstream; an object round-trips correctly and
            // ToTemplate() still reads it via ToString(). Fall back to the raw text
            // if it is not parseable JSON so ToTemplate() can surface a clear error.
            try
            {
                using var doc = JsonDocument.Parse(content!);
                Properties.Template = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                Properties.Template = content;
            }
        }
    }

    public Template ToTemplate()
    {
        var templateJson = Properties?.Template == null
            ? throw new InvalidOperationException("Deployment template is missing.")
            : Properties.Template.ToString();

        return string.IsNullOrWhiteSpace(templateJson)
            ? throw new InvalidOperationException("Deployment template is empty.")
            : TemplateParsingEngine.ParseTemplate(templateJson);
    }
}