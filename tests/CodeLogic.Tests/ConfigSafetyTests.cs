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

    [Fact]
    public async Task Config_files_are_owner_only_on_unix()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<SecretConfig>("secret");
        await config.LoadAllAsync();
        await config.SaveAsync(new SecretConfig { Password = "changed" });

        var path = Path.Combine(_dir, "config.secret.json");
        Assert.Contains("changed", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        if (OperatingSystem.IsWindows()) return; // ACLs, not modes

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:x")]
    [InlineData("bad\nname")]
    [InlineData(" padded")]
    [InlineData("dot.")]
    public void Section_names_that_are_not_a_single_file_name_segment_are_rejected(string name)
    {
        var config = new ConfigurationManager(_dir);
        Assert.Throws<ArgumentException>(() => config.Register<PlainConfig>(name));
        Assert.Empty(config.GetSections());
    }

    [Fact]
    public void Ordinary_section_names_are_accepted()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<PlainConfig>("db-main_2.v1");
        Assert.Equal("db-main_2.v1", Assert.Single(config.GetSections()).SectionName);
    }

    [Fact]
    public async Task Concurrent_registration_and_reads_do_not_throw()
    {
        var config = new ConfigurationManager(_dir);
        var register = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) config.Register<PlainConfig>($"s{i}");
        });
        var read = Task.Run(() =>
        {
            while (!register.IsCompleted) _ = config.GetSections();
        });
        await Task.WhenAll(register, read);
        Assert.Single(config.GetSections()); // one type, last name wins
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

    // ── Masks in collection entries are never persisted ─────────────────────

    private string CollectionFile => Path.Combine(_dir, "config.coll.json");

    private async Task<ConfigurationManager> LoadCollectionsAsync()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<CollectionSecretConfig>("coll");
        await config.LoadAllAsync();
        return config;
    }

    [Fact]
    public async Task Adding_a_keyed_list_entry_keeps_existing_masked_passwords_and_rejects_a_masked_new_one()
    {
        var config = await LoadCollectionsAsync();
        var mask = ConfigField.SecretMask;

        var withClearNewEntry = await config.UpdateSectionAsync("coll", $$"""
            { "smtp": [ { "name": "a", "password": "{{mask}}" }, { "name": "b", "password": "{{mask}}" },
                        { "name": "c", "password": "pw-c" } ] }
            """);
        Assert.True(withClearNewEntry.IsValid, withClearNewEntry.ToString());
        Assert.Equal(["pw-a", "pw-b", "pw-c"], config.Get<CollectionSecretConfig>().Smtp.Select(s => s.Password));

        var withMaskedNewEntry = await config.UpdateSectionAsync("coll", $$"""
            { "smtp": [ { "name": "a", "password": "{{mask}}" }, { "name": "b", "password": "{{mask}}" },
                        { "name": "c", "password": "{{mask}}" }, { "name": "d", "password": "{{mask}}" } ] }
            """);
        Assert.False(withMaskedNewEntry.IsValid);
        Assert.Contains(withMaskedNewEntry.Errors, e => e.Contains("smtp.3.password"));
        Assert.Equal("smtp.3.password", Assert.Single(withMaskedNewEntry.FieldErrors).Path);
        Assert.Equal(3, config.Get<CollectionSecretConfig>().Smtp.Count);
        Assert.DoesNotContain(mask, await File.ReadAllTextAsync(CollectionFile));
    }

    [Fact]
    public async Task Adding_an_entry_to_an_unkeyed_list_with_masked_passwords_is_rejected()
    {
        var config = await LoadCollectionsAsync();
        var mask = ConfigField.SecretMask;

        var result = await config.UpdateSectionAsync("coll", $$"""
            { "relays": [ { "host": "h1", "password": "{{mask}}" }, { "host": "h2", "password": "{{mask}}" },
                          { "host": "h3", "password": "pw-3" } ] }
            """);

        Assert.False(result.IsValid);
        Assert.Equal(["r1", "r2"], config.Get<CollectionSecretConfig>().Relays.Select(r => r.Password));
        Assert.DoesNotContain(mask, await File.ReadAllTextAsync(CollectionFile));

        // Same length: restored by position.
        var sameLength = await config.UpdateSectionAsync("coll", $$"""
            { "relays": [ { "host": "h1b", "password": "{{mask}}" }, { "host": "h2", "password": "{{mask}}" } ] }
            """);
        Assert.True(sameLength.IsValid, sameLength.ToString());
        Assert.Equal(["r1", "r2"], config.Get<CollectionSecretConfig>().Relays.Select(r => r.Password));
    }

    [Fact]
    public async Task Dictionary_entries_restore_by_key_and_reject_masked_new_keys()
    {
        var config = await LoadCollectionsAsync();
        var mask = ConfigField.SecretMask;

        var schema = config.GetSchema("coll");
        Assert.Equal(mask, schema.Fields.Single(f => f.Path == "databases.main.password").CurrentValue);
        var tokens = Assert.Single(schema.Fields, f => f.Path == "tokens");
        Assert.Equal("dict", tokens.TypeHint);
        Assert.Equal(mask, ((IDictionary<string, object?>)tokens.CurrentValue!)["github"]);

        var ok = await config.UpdateSectionAsync("coll", $$"""
            { "databases": { "main": { "password": "{{mask}}" }, "extra": { "password": "pw-extra" } },
              "tokens": { "github": "{{mask}}", "gitlab": "tok-gitlab" } }
            """);
        Assert.True(ok.IsValid, ok.ToString());
        var saved = config.Get<CollectionSecretConfig>();
        Assert.Equal("db-main", saved.Databases["main"].Password);
        Assert.Equal("tok-github", saved.Tokens["github"]);

        var newMaskedKey = await config.UpdateSectionAsync("coll", $$"""
            { "databases": { "main": { "password": "{{mask}}" }, "other": { "password": "{{mask}}" } } }
            """);
        var newMaskedToken = await config.UpdateSectionAsync("coll", $$"""{ "tokens": { "new": "{{mask}}" } }""");
        Assert.False(newMaskedKey.IsValid);
        Assert.False(newMaskedToken.IsValid);
        Assert.DoesNotContain(mask, await File.ReadAllTextAsync(CollectionFile));
    }

    [Fact]
    public async Task Interface_typed_list_and_dictionary_properties_are_restored()
    {
        var config = await LoadCollectionsAsync();
        var mask = ConfigField.SecretMask;

        var schema = config.GetSchema("coll");
        Assert.Equal(mask, schema.Fields.Single(f => f.Path == "listed.0.password").CurrentValue);
        Assert.Equal(mask, schema.Fields.Single(f => f.Path == "mapped.x.password").CurrentValue);

        var result = await config.UpdateSectionAsync("coll", $$"""
            { "listed": [ { "id": "1", "password": "{{mask}}" } ],
              "mapped": { "x": { "password": "{{mask}}" } } }
            """);

        Assert.True(result.IsValid, result.ToString());
        var saved = config.Get<CollectionSecretConfig>();
        Assert.Equal("il-1", saved.Listed[0].Password);
        Assert.Equal("id-x", saved.Mapped["x"].Password);
        Assert.DoesNotContain(mask, await File.ReadAllTextAsync(CollectionFile));
    }

    // ── Schema shapes ────────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_handles_scalar_dictionaries_arrays_enum_lists_and_json_names()
    {
        var config = new ConfigurationManager(_dir);
        config.Register<ShapesConfig>("shapes");
        await config.LoadAllAsync();

        var schema = config.GetSchema("shapes");
        var paths = schema.Fields.Select(f => f.Path).ToList();

        Assert.DoesNotContain(paths, p => p.Contains("capacity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ignored", paths);

        var modes = Assert.Single(schema.Fields, f => f.Path == "modes");
        Assert.Equal(["Visible", "Hidden"], ((IEnumerable)modes.CurrentValue!).Cast<object>());
        Assert.Equal(["Visible", "Hidden"], modes.AllowedValues!);

        var names = Assert.Single(schema.Fields, f => f.Path == "names");
        Assert.Equal(["x", "y"], ((IEnumerable)names.CurrentValue!).Cast<object>());

        var labels = Assert.Single(schema.Fields, f => f.Path == "labels");
        Assert.Equal("v", ((IDictionary<string, object?>)labels.CurrentValue!)["k"]);

        Assert.Single(schema.Fields, f => f.Path == "custom_name");
        Assert.Single(schema.Fields, f => f.Path == "items.0.value");

        // The schema paths round-trip through UpdateSectionAsync.
        var result = await config.UpdateSectionAsync("shapes",
            """{ "modes": ["Hidden"], "names": ["z"], "labels": { "a": "b" }, "custom_name": "n", "items": [ { "value": "q" } ] }""");
        Assert.True(result.IsValid, result.ToString());
        var saved = config.Get<ShapesConfig>();
        Assert.Equal([SecretMode.Hidden], saved.Modes);
        Assert.Equal(["z"], saved.Names);
        Assert.Equal("b", saved.Labels["a"]);
        Assert.Equal("n", saved.Renamed);
    }

    private sealed class CollectionSecretConfig : ConfigModelBase
    {
        public List<SmtpEntry> Smtp { get; set; } = [new() { Name = "a", Password = "pw-a" }, new() { Name = "b", Password = "pw-b" }];
        public List<RelayEntry> Relays { get; set; } = [new() { Host = "h1", Password = "r1" }, new() { Host = "h2", Password = "r2" }];
        public Dictionary<string, DbEntry> Databases { get; set; } = new() { ["main"] = new() { Password = "db-main" } };
        [ConfigField(Secret = true)] public Dictionary<string, string> Tokens { get; set; } = new() { ["github"] = "tok-github" };
        public IList<KeyedEntry> Listed { get; set; } = [new() { Id = "1", Password = "il-1" }];
        public IDictionary<string, DbEntry> Mapped { get; set; } = new Dictionary<string, DbEntry> { ["x"] = new() { Password = "id-x" } };
    }

    private sealed class SmtpEntry
    {
        public string Name { get; set; } = "";
        [ConfigField(Secret = true)] public string Password { get; set; } = "";
    }

    private sealed class RelayEntry
    {
        public string Host { get; set; } = "";
        [ConfigField(Secret = true)] public string Password { get; set; } = "";
    }

    private sealed class KeyedEntry
    {
        public string Id { get; set; } = "";
        [ConfigField(Secret = true)] public string Password { get; set; } = "";
    }

    private sealed class DbEntry
    {
        [ConfigField(Secret = true)] public string Password { get; set; } = "";
    }

    private sealed class ShapesConfig : ConfigModelBase
    {
        public List<SecretMode> Modes { get; set; } = [SecretMode.Visible, SecretMode.Hidden];
        public string[] Names { get; set; } = ["x", "y"];
        public Dictionary<string, string> Labels { get; set; } = new() { ["k"] = "v" };
        [System.Text.Json.Serialization.JsonPropertyName("custom_name")] public string Renamed { get; set; } = "r";
        [System.Text.Json.Serialization.JsonIgnore] public string Ignored { get; set; } = "i";
        public ItemEntry[] Items { get; set; } = [new()];
    }

    private sealed class ItemEntry
    {
        public string Value { get; set; } = "v";
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
