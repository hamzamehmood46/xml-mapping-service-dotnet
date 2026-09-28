using System.Text;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Xsl;

namespace XmlMapping.Core;

public enum MappingStage
{
    /// <summary>The submitted document is malformed, or violates the partner's source schema.</summary>
    Input,

    /// <summary>The transformation ran but produced a document that violates the target schema.</summary>
    Output
}

public record ValidationIssue(string Message, int Line, int Position);

public record MappingResult(bool Success, string? Xml, MappingStage? FailedStage, IReadOnlyList<ValidationIssue> Issues)
{
    public static MappingResult Ok(string xml) => new(true, xml, null, []);
    public static MappingResult Fail(MappingStage stage, IReadOnlyList<ValidationIssue> issues) => new(false, null, stage, issues);
}

/// <summary>One partner integration: source schema, target schema and the XSLT between them.</summary>
public sealed class MappingDefinition
{
    public string Name { get; }
    internal XmlSchemaSet SourceSchemas { get; }
    internal XmlSchemaSet TargetSchemas { get; }
    internal XslCompiledTransform Transform { get; }

    public MappingDefinition(string name, string sourceXsd, string targetXsd, string xslt)
    {
        Name = name;
        SourceSchemas = LoadSchemas(sourceXsd);
        TargetSchemas = LoadSchemas(targetXsd);
        Transform = LoadTransform(xslt);
    }

    private static XmlSchemaSet LoadSchemas(string xsd)
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xsd), MappingEngine.SecureSettings());
        set.Add(XmlSchema.Read(reader, (_, e) => throw new InvalidOperationException($"Invalid schema: {e.Message}"))!);
        set.Compile();
        return set;
    }

    private static XslCompiledTransform LoadTransform(string xslt)
    {
        var transform = new XslCompiledTransform();
        using var reader = XmlReader.Create(new StringReader(xslt), MappingEngine.SecureSettings());
        // Default XsltSettings: document() and embedded scripts are disabled, so a mapping cannot read files or run code.
        transform.Load(reader, XsltSettings.Default, null);
        return transform;
    }
}

/// <summary>
/// Validate the partner's XML, transform it, then validate the result. Failing fast on both sides means a
/// bad partner payload - or a bad mapping change - is caught before it reaches downstream systems.
/// </summary>
public static class MappingEngine
{
    private const int MaxIssues = 50;

    public static MappingResult Execute(MappingDefinition mapping, string xml)
    {
        var inputIssues = Validate(xml, mapping.SourceSchemas);
        if (inputIssues.Count > 0) return MappingResult.Fail(MappingStage.Input, inputIssues);

        var output = new StringBuilder();
        using (var reader = XmlReader.Create(new StringReader(xml), SecureSettings()))
        using (var writer = XmlWriter.Create(output, mapping.Transform.OutputSettings))
        {
            mapping.Transform.Transform(reader, null, writer);
        }

        var result = output.ToString();
        var outputIssues = Validate(result, mapping.TargetSchemas);
        return outputIssues.Count > 0
            ? MappingResult.Fail(MappingStage.Output, outputIssues)
            : MappingResult.Ok(result);
    }

    /// <summary>Validates a document against a schema set, returning every issue found (capped).</summary>
    public static IReadOnlyList<ValidationIssue> Validate(string xml, XmlSchemaSet schemas)
    {
        var issues = new List<ValidationIssue>();
        var settings = SecureSettings();
        settings.ValidationType = ValidationType.Schema;
        settings.Schemas = schemas;
        settings.ValidationEventHandler += (_, e) =>
        {
            if (issues.Count < MaxIssues)
                issues.Add(new ValidationIssue(e.Message, e.Exception?.LineNumber ?? 0, e.Exception?.LinePosition ?? 0));
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read()) { }
        }
        catch (XmlException ex)
        {
            issues.Add(new ValidationIssue(ex.Message, ex.LineNumber, ex.LinePosition));
        }

        return issues;
    }

    /// <summary>
    /// Hardened reader settings. DTDs are prohibited and no resolver is set, which closes off
    /// XXE (external entity) and billion-laughs style attacks on untrusted partner input.
    /// </summary>
    internal static XmlReaderSettings SecureSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = 5_000_000,
        IgnoreComments = true
    };
}

/// <summary>Loads mapping definitions from a folder: one sub-folder per partner (source.xsd, target.xsd, map.xslt).</summary>
public class MappingRegistry
{
    private readonly Dictionary<string, MappingDefinition> _mappings = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _mappings.Keys;

    public static MappingRegistry LoadFrom(string directory)
    {
        var registry = new MappingRegistry();
        if (!Directory.Exists(directory)) return registry;

        foreach (var dir in Directory.GetDirectories(directory))
        {
            var name = Path.GetFileName(dir);
            registry.Add(new MappingDefinition(
                name,
                File.ReadAllText(Path.Combine(dir, "source.xsd")),
                File.ReadAllText(Path.Combine(dir, "target.xsd")),
                File.ReadAllText(Path.Combine(dir, "map.xslt"))));
        }

        return registry;
    }

    public void Add(MappingDefinition mapping) => _mappings[mapping.Name] = mapping;

    public bool TryGet(string name, out MappingDefinition mapping) => _mappings.TryGetValue(name, out mapping!);
}

