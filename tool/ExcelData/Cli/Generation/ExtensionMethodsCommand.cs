﻿namespace Datask.Tool.ExcelData.Generation;

[Command("extensions", "e", ParentType = typeof(GenerateCommand))]
[CommandHelp("Generates extension methods.")]
public sealed class ExtensionMethodsCommand : BaseCommand
{
    [Option("ns")]
    public string Namespace { get; set; } = null!;

    [Option("output", "o")]
    public string FilePath { get; set; } = null!;

    [Option("flavor", "f", MultipleOccurrences = true)]
    public IList<string> Flavors { get; set; } = null!;

    protected override async Task<int> ExecuteAsync(StatusContext ctx, IParseResult parseResult)
    {
        IEnumerable<Flavor> flavors = Flavors.Select(f =>
        {
            string[] parts = f.Split('=', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new Flavor(parts[0], parts[1]);
        });
        ExtensionMethodsGeneratorOptions options = new(Namespace, FilePath, flavors.ToArray());

        ExtensionMethodsGenerator generator = new(options);
        generator.OnStatus += (_, args) =>
        {
            ctx.Status(args.Message ?? string.Empty);
            ctx.Refresh();
        };

        await generator.ExecuteAsync().ConfigureAwait(false);

        AnsiConsole.MarkupLine($"The file {options.FilePath} generated successfully.");

        return 0;
    }
}
