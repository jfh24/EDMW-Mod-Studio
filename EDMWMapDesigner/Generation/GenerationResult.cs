using EDMWMapDesigner.Analysis;

namespace EDMWMapDesigner.Generation;

public sealed record GenerationResult(string Script, MapAnalysis Analysis, IReadOnlyList<string> Notes);
