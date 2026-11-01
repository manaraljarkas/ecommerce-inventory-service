using Xunit;
using InventoryService.Persistence;
using Microsoft.Extensions.Configuration;

namespace InventoryService.Tests;

/// Binding coverage for the development seed options against the file the service ships.

/// The seed list is reached only through configuration binding, so a mismatch between the
/// JSON keys and the options type compiles cleanly and then binds to nothing: the seeder
/// finds an empty list and returns without a word. Nothing else in this suite touches the
/// seeder, so these are the only tests that would notice.
public class InventorySeedOptionsTests
{
    /// The service's own appsettings.Development.json, copied next to the test assembly by
    /// the test project so the bound values are the shipped ones, not a copy that can drift.
    private const string DevelopmentSettingsFile = "service.appsettings.Development.json";

    private static InventorySeedOptions BindDevelopmentSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(DevelopmentSettingsFile, optional: false)
            .Build();

        var options = new InventorySeedOptions();
        configuration.GetSection(InventorySeedOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void The_shipped_development_settings_bind_to_the_seed_options()
    {
        var options = BindDevelopmentSettings();

        // Every entry arriving with usable values is what proves the section name and the
        // property names still line up with the JSON.
        Assert.NotEmpty(options.Items);
        Assert.All(options.Items, item =>
        {
            Assert.True(item.ProductId > 0);
            Assert.True(item.QuantityOnHand >= 0);
        });
    }

    [Fact]
    public void Seeding_is_committed_switched_off()
    {
        // The placeholder product ids in that file are not Laravel's, so a commit that leaves
        // seeding enabled would insert rows nobody asked for on the next development run.
        Assert.False(BindDevelopmentSettings().Enabled);
    }

    [Fact]
    public void An_absent_section_leaves_seeding_off_and_empty()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = new InventorySeedOptions();
        configuration.GetSection(InventorySeedOptions.SectionName).Bind(options);

        Assert.False(options.Enabled);
        Assert.Empty(options.Items);
    }
}
