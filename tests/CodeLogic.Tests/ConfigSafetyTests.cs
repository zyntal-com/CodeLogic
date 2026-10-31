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

    // ── Non-string secrets ───────────────────────────────────────────────────

    [Fact]
    public async Task Non_string_secrets_are_masked_whatever_their_type()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<TypedSecretConfig>("typed");
        await config.LoadAllAsync();

        var schema = config.GetSchema("typed");

        foreach (var path in new[] { "pin", "token", "mode", "nested.port", "servers.0.port" })
        {
            var field = Assert.Single(schema.Fields, f => f.Path == path);
            Assert.True(field.Secret, path);
            Assert.Equal(ConfigField.SecretMask, field.CurrentValue);
            Assert.Equal(ConfigField.SecretMask, field.DefaultValue);
        }
        var codes = Assert.Single(schema.Fields, f => f.Path == "codes");
        Assert.Equal([ConfigField.SecretMask, ConfigField.SecretMask], ((IEnumerable)codes.CurrentValue!).Cast<object>());
        Assert.Null(Assert.Single(schema.Fields, f => f.Path == "optionalPin").CurrentValue); // unset stays visible
        Assert.Null(Assert.Single(schema.Fields, f => f.Path == "mode").AllowedValues);

        var revealed = config.GetSchema("typed", includeSecrets: true);
        Assert.Equal(1234, revealed.Fields.Single(f => f.Path == "pin").CurrentValue);
        Assert.Equal(TypedSecretConfig.DefaultToken, revealed.Fields.Single(f => f.Path == "token").CurrentValue);
    }

    [Fact]
    public async Task Masked_non_string_secrets_echoed_back_keep_their_stored_values()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<TypedSecretConfig>("typed");
        await config.LoadAllAsync();
        var mask = ConfigField.SecretMask;

        var json = $$"""
            {
              "pin": "{{mask}}", "token": "{{mask}}", "mode": "{{mask}}", "codes": ["{{mask}}", 99],
              "nested": { "port": "{{mask}}" },
              "servers": [ { "name": "b", "port": 2 }, { "name": "a", "port": "{{mask}}" } ],
              "label": "changed"
            }
            """;
        var result = await config.UpdateSectionAsync("typed", json);

        Assert.True(result.IsValid, result.ToString());
        var saved = config.Get<TypedSecretConfig>();
        Assert.Equal(1234, saved.Pin);
        Assert.Equal(TypedSecretConfig.DefaultToken, saved.Token);
        Assert.Equal(SecretMode.Hidden, saved.Mode);
        Assert.Equal([7, 99], saved.Codes);
        Assert.Equal(5432, saved.Nested.Port);
        Assert.Equal(1111, saved.Servers.Single(s => s.Name == "a").Port); // paired by Name, not position
        Assert.Equal(2, saved.Servers.Single(s => s.Name == "b").Port);
        Assert.Equal("changed", saved.Label);
        Assert.DoesNotContain(mask, await File.ReadAllTextAsync(Path.Combine(_dir, "config.typed.json")));
    }

    [Fact]
    public async Task Masked_non_string_secret_without_a_stored_counterpart_is_rejected()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<TypedSecretConfig>("typed");
        await config.LoadAllAsync();
        var mask = ConfigField.SecretMask;

        var newServer = await config.UpdateSectionAsync("typed",
            $$"""{ "servers": [ { "name": "new", "port": "{{mask}}" } ] }""");
        var resized = await config.UpdateSectionAsync("typed", $$"""{ "codes": ["{{mask}}"] }""");

        Assert.False(newServer.IsValid);
        Assert.False(resized.IsValid);
        Assert.Equal(1111, config.Get<TypedSecretConfig>().Servers.Single(s => s.Name == "a").Port);
    }

    private enum SecretMode { Visible, Hidden }

    private sealed class TypedSecretConfig : ConfigModelBase
    {
        public static readonly Guid DefaultToken = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

        [ConfigField(Secret = true)] public int Pin { get; set; } = 1234;
        [ConfigField(Secret = true)] public int? OptionalPin { get; set; }
        [ConfigField(Secret = true)] public Guid Token { get; set; } = DefaultToken;
        [ConfigField(Secret = true)] public SecretMode Mode { get; set; } = SecretMode.Hidden;
        [ConfigField(Secret = true)] public List<int> Codes { get; set; } = [7, 8];
        public string Label { get; set; } = "label";
        public NestedSecret Nested { get; set; } = new();
        public List<ServerEntry> Servers { get; set; } = [new() { Name = "a", Port = 1111 }, new() { Name = "b", Port = 2222 }];
    }

    private sealed class NestedSecret
    {
        [ConfigField(Secret = true)] public int Port { get; set; } = 5432;
    }

    private sealed class ServerEntry
    {
        public string Name { get; set; } = "";
        [ConfigField(Secret = true)] public int Port { get; set; }
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
