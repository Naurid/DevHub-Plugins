using DevHub.Diagrams;
using DevHub.Diagrams.Database;
using DevHub.Sdk.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.DiagramsDatabasePlugin))]

namespace DevHub.Plugins;

/// <summary>
/// Database diagrams for the Diagrams editor: the Merise MCD (which generates the MLD), table diagrams (MLD or
/// crow's-foot ERD), SQL export in four dialects and import, the data dictionary. Requires Diagrams.
/// </summary>
public sealed class DiagramsDatabasePlugin : IPlugin
{
    public const string Id = "devhub.diagrams.database";

    public PluginInfo Info { get; } = new(Id, "Database Diagrams",
        "Merise data models (MCD) that generate the MLD, SQL tables for MySQL, PostgreSQL, SQL Server and SQLite, and the data dictionary; table diagrams in crow's-foot notation; SQL import.",
        [DiagramsPlugin.Id]) { Category = "Tools", Order = 612, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IDiagramKind, MeriseKind>();
        services.AddSingleton<IDiagramKind, ErdKind>();
    }
}
