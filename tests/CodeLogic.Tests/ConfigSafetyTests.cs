using System.Collections;
using CodeLogic.Core.Configuration;
using Xunit;

namespace CodeLogic.Tests;

/// <summary>Write serialization of default generation, and secret masking in schemas.</summary>
public sealed class ConfigSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cl_cfgsafe_" + Guid.NewGuid().ToString("N"));

    public ConfigSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Default generation is serialized with saves and loads ────────────────

    [Fact]
    public async Task Concurrent_generate_save_and_load_never_collide_or_lose_the_save()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<PlainConfig>();
        var path = Path.Combine(_dir, "config.json");

        for (var i = 0; i < 40; i++)
        {
            if (File.Exists(path)) File.Delete(path);
            var saved = new PlainConfig { Name = $"saved-{i}" };

            await Task.WhenAll(
                Task.Run(() => config.LoadAsync<PlainConfig>()),
                Task.Run(() => config.GenerateDefaultAsync<PlainConfig>()),
                Task.Run(() => config.SaveAsync(saved)));

            // Load/generate either ran before the save or saw its file: the save always survives.
            Assert.Contains($"saved-{i}", await File.ReadAllTextAsync(path));
        }

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Forced_generation_and_saves_run_concurrently_without_io_errors()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<PlainConfig>();
        await config.LoadAllAsync();

        var work = Enumerable.Range(0, 50).Select(i => i % 2 == 0
            ? Task.Run(() => config.GenerateDefaultAsync<PlainConfig>(force: true))
            : Task.Run(() => config.SaveAsync(new PlainConfig { Name = $"n{i}" })));

        await Task.WhenAll(work); // a shared .tmp name made these fail with IOException
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ── Secret masking ───────────────────────────────────────────────────────

    [Fact]
    public async Task Secret_defaults_and_secret_string_lists_are_masked()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<SecretConfig>("secret");
        await config.LoadAllAsync();

        var schema = config.GetSchema("secret");

        var password = Assert.Single(schema.Fields, f => f.Path == "password");
        Assert.Equal(ConfigField.SecretMask, password.CurrentValue);
        Assert.Equal(ConfigField.SecretMask, password.DefaultValue);

        var keys = Assert.Single(schema.Fields, f => f.Path == "apiKeys");
        Assert.Equal([ConfigField.SecretMask, ConfigField.SecretMask], ((IEnumerable)keys.CurrentValue!).Cast<object>());
        Assert.Equal([ConfigField.SecretMask, ConfigField.SecretMask], ((IEnumerable)keys.DefaultValue!).Cast<object>());

        var hosts = Assert.Single(schema.Fields, f => f.Path == "hosts");
        Assert.Equal(["a", "b"], ((IEnumerable)hosts.CurrentValue!).Cast<object>());

        var revealed = config.GetSchema("secret", includeSecrets: true);
        Assert.Equal("default-password", revealed.Fields.Single(f => f.Path == "password").CurrentValue);
        Assert.Equal(["key-1", "key-2"], ((IEnumerable)revealed.Fields.Single(f => f.Path == "apiKeys").CurrentValue!).Cast<object>());
    }

    [Fact]
    public async Task Masked_secret_list_entries_are_restored_by_position()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<SecretConfig>("secret");
        await config.LoadAllAsync();

        var json = $$"""{ "password": "{{ConfigField.SecretMask}}", "apiKeys": ["{{ConfigField.SecretMask}}", "key-2b"], "hosts": ["a"] }""";
        var result = await config.UpdateSectionAsync("secret", json);

        Assert.True(result.IsValid, result.ToString());
        var saved = config.Get<SecretConfig>();
        Assert.Equal("default-password", saved.Password);
        Assert.Equal(["key-1", "key-2b"], saved.ApiKeys);
    }

    [Fact]
    public async Task Masked_secret_list_entries_with_a_changed_length_are_rejected()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<SecretConfig>("secret");
        await config.LoadAllAsync();

        var json = $$"""{ "apiKeys": ["{{ConfigField.SecretMask}}", "key-2", "key-3"] }""";
        var result = await config.UpdateSectionAsync("secret", json);

        Assert.False(result.IsValid);
        Assert.Equal(["key-1", "key-2"], config.Get<SecretConfig>().ApiKeys);
        Assert.DoesNotContain(ConfigField.SecretMask, await File.ReadAllTextAsync(Path.Combine(_dir, "config.secret.json")));
    }

    private sealed class PlainConfig : ConfigModelBase
    {
        public string Name { get; set; } = "default";
    }

    private sealed class SecretConfig : ConfigModelBase
    {
        [ConfigField(Secret = true)]
        public string Password { get; set; } = "default-password";

        [ConfigField(Secret = true)]
        public List<string> ApiKeys { get; set; } = ["key-1", "key-2"];

        public List<string> Hosts { get; set; } = ["a", "b"];
    }
}
