using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace ShelfCheck;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer)
    : BasePlugin<BasePluginConfiguration>(paths, serializer), IHasWebPages
{
    public override string Name => "ShelfCheck";
    public override Guid Id => new("787553a2-52ba-476a-8393-8c5d2fe89ce5");
    public override string Description => "Prüft den Medienbestand und gleicht das Hinzufügedatum mit der Veröffentlichung ab.";
    public IEnumerable<PluginPageInfo> GetPages() =>
        [new() { Name = Name, EmbeddedResourcePath = "ShelfCheck.Dashboard.html", EnableInMainMenu = true, MenuIcon = "fact_check" }];
}

public sealed class Services : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host) =>
        services.AddSingleton<ReportStore>().AddSingleton<DateRunStore>().AddSingleton<DateSync>();
}
