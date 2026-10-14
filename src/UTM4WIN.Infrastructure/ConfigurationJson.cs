using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UTM4WIN.Application;
using UTM4WIN.Domain;

namespace UTM4WIN.Infrastructure;

public static class ConfigurationJson
{
    public const int CurrentSchemaVersion = 1;
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private sealed record Document
    {
        public required int SchemaVersion { get; init; }
        public required VmConfiguration Configuration { get; init; }
    }

    public static byte[] Serialize(VmConfiguration configuration)
    {
        Validate(configuration);
        return JsonSerializer.SerializeToUtf8Bytes(new Document { SchemaVersion = CurrentSchemaVersion, Configuration = configuration }, Options);
    }

    public static VmSnapshot Deserialize(byte[] bytes)
    {
        try
        {
            using var header = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (!header.RootElement.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out int schema))
                throw new ConfigurationException("The configuration has no valid schema version.");
            RequireSchema(schema);
            var document = JsonSerializer.Deserialize<Document>(bytes, Options) ?? throw new ConfigurationException("The configuration is empty.");
            Validate(document.Configuration);
            return new(document.Configuration, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (JsonException exception) { throw new ConfigurationException($"Invalid configuration JSON: {exception.Message}"); }
        catch (InvalidOperationException) { throw new ConfigurationException("Invalid configuration document."); }
    }

    internal static void RequireSchema(int version)
    {
        if (version != CurrentSchemaVersion)
            throw new ConfigurationException(version > CurrentSchemaVersion
                ? $"Schema {version} requires a newer UTM4WIN version. The original file was preserved."
                : $"Schema {version} has no supported migration. The original file was preserved.");
    }

    internal static void Validate(VmConfiguration configuration)
    {
        var issues = ConfigurationValidator.Validate(configuration);
        if (issues.Count != 0) throw new ConfigurationException(string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Field}: {issue.Message}")));
    }
}
