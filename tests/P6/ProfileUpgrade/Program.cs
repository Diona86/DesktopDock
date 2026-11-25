using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopDock;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0]) || File.Exists(args[0]))
    throw new ArgumentException("Provide a new, absolute output directory.");
string run = Directory.CreateDirectory(args[0]).FullName;
string backups = Path.Combine(run, "backups");
int passed = 0;

void Check(bool condition, string title)
{
    if (!condition)
        throw new InvalidOperationException(title);
    Console.WriteLine($"PASS {++passed:D2} {title}");
}

void Reject<T>(Action operation, string title, Func<T, bool>? check = null) where T : Exception
{
    try { operation(); }
    catch (T error)
    {
        Check(check?.Invoke(error) != false, title);
        return;
    }
    throw new InvalidOperationException($"Unexpected success: {title}");
}

string Profile(string name) => Directory.CreateDirectory(Path.Combine(run, name)).FullName;

void WriteProfile(string directory, int schema, string target, string icon)
{
    Guid id = Guid.NewGuid();
    Write(Path.Combine(directory, "dock.json"), new
    {
        schemaVersion = schema,
        options = new { showDockOnStartup = true },
        items = new[] { new { id, name = "Existing target", kind = "File", target, customIconPath = icon } },
        groups = Array.Empty<object>(),
        entries = new[] { new { id } }
    });
}

static void Write(string path, object value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));

static void WriteJournal(string profile, string phase)
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    Write(Path.Combine(profile, "desktop-icons.json"), new
    {
        version = 1,
        operation = Guid.NewGuid(),
        owner = Guid.NewGuid(),
        before = new { shell = new { processId = 1234, processStartTicks = 1, shellWindow = 21, iconView = 63 }, visible = true, windowsHidden = false },
        phase,
        started = now,
        updated = now
    });
}

static int SchemaVersion(string path)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
    return document.RootElement.GetProperty("schemaVersion").GetInt32();
}

static bool Same(string path, byte[] contents) => File.ReadAllBytes(path).SequenceEqual(contents);

string assets = Profile("external-assets");
string target = Path.Combine(assets, "user-file.txt");
string icon = Path.Combine(assets, "user-icon.png");
File.WriteAllText(target, "user target sentinel");
File.WriteAllText(icon, "user icon sentinel");
byte[] targetBytes = File.ReadAllBytes(target);
byte[] iconBytes = File.ReadAllBytes(icon);

string missing = Path.Combine(run, "missing-profile");
DockReleaseUpgrade.Validate(missing);
Check(!Directory.Exists(missing), "Read-only preflight does not create a missing profile.");
string emptyBackup = DockReleaseUpgrade.Prepare(missing, backups, "0.0.0", "1.0.0");
Check(!Directory.Exists(missing) && !Directory.EnumerateFileSystemEntries(Path.Combine(emptyBackup, "profile")).Any(),
    "A fresh install has an empty verified backup and an untouched data location.");

for (int schema = 1; schema <= 4; schema++)
{
    string profile = Profile($"schema-{schema}");
    string config = Path.Combine(profile, "dock.json");
    WriteProfile(profile, schema, target, icon);
    byte[] original = File.ReadAllBytes(config);
    string backup = DockReleaseUpgrade.Prepare(profile, backups, "0.0.0", "1.0.0");
    Check(Same(config, original) && Same(Path.Combine(backup, "profile", "dock.json"), original),
        $"Schema {schema} passes preflight without rewriting the original.");
    using DockSession dock = DockSession.Open(profile);
    Check(dock.Items[0].Target == target && dock.Items[0].CustomIconPath == icon, $"Schema {schema} preserves external references.");
    dock.UpdateOptions(dock.Options with { ShowDockOnStartup = false });
    Check(SchemaVersion(config) == 4 && (schema == 4 ||
        Directory.EnumerateFiles(profile, $"dock.v{schema}-*.bak").Any(file => Same(file, original))),
        $"Schema {schema} reaches current format on save and retains any migration source.");
}

string active = Profile("active-profile");
string activeDock = Path.Combine(active, "dock.json");
WriteProfile(active, 3, target, icon);
byte[] activeOriginal = File.ReadAllBytes(activeDock);
var store = DockThemeStore.Open(active);
store.Save(store.Current.SelectTheme("glass"));
string themes = Path.Combine(active, "themes.json");
byte[] themeOriginal = File.ReadAllBytes(themes);
string index = Path.Combine(active, "display-profiles.json");
Write(index, new { schemaVersion = 1, legacyMonitorId = "primary-1" });
string secondaryName = "display-0123456789abcdef0123456789abcdef";
string secondary = Directory.CreateDirectory(Path.Combine(active, "display-docks", secondaryName)).FullName;
string secondaryDock = Path.Combine(secondary, "dock.json");
WriteProfile(secondary, 4, target, icon);
byte[] secondaryOriginal = File.ReadAllBytes(secondaryDock);
string unrelated = Path.Combine(active, "personal.docktheme");
File.WriteAllText(unrelated, "external document sentinel");
string runtimeLog = Path.Combine(active, "runtime.log");
File.WriteAllText(runtimeLog, "diagnostic event sentinel");
string historical = Path.Combine(active, "dock.v2-0123456789abcdef0123456789abcdef.bak");
File.WriteAllText(historical, "historical app migration backup");
WriteJournal(active, "Restored");
string journal = Path.Combine(active, "desktop-icons.json");

using (DockSession.Open(active))
    Reject<IOException>(() => DockReleaseUpgrade.Prepare(active, backups, "0.0.0", "1.0.0"),
        "Upgrade refuses an open root configuration instead of closing it.");
using (DockSession.Open(secondary))
    Reject<IOException>(() => DockReleaseUpgrade.Prepare(active, backups, "0.0.0", "1.0.0"),
        "Upgrade refuses an open secondary-display configuration.");
Reject<InvalidOperationException>(() => DockReleaseUpgrade.Prepare(active, backups, "2.0.0", "1.0.0"),
    "The helper rejects a downgrade.");
Reject<ArgumentException>(() => DockReleaseUpgrade.Prepare(active, Path.Combine(active, "unsafe-backup"), "0.0.0", "1.0.0"),
    "A backup cannot be placed inside the live configuration.");
string snapshot = DockReleaseUpgrade.Prepare(active, backups, "0.0.0", "1.0.0");
string copied = Path.Combine(snapshot, "profile");
using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(snapshot, "backup.json"))))
{
    JsonElement metadata = manifest.RootElement;
    Check(metadata.GetProperty("schemaVersion").GetInt32() == 1 && metadata.GetProperty("files").GetArrayLength() == 6 &&
        metadata.GetProperty("toVersion").GetString() == "1.0.0", "The manifest records version identity and exactly six owned files.");
}
Check(Same(Path.Combine(copied, "display-docks", secondaryName, "dock.json"), secondaryOriginal) &&
    Same(Path.Combine(copied, "themes.json"), themeOriginal) && !File.Exists(Path.Combine(copied, "personal.docktheme")) &&
    !Directory.EnumerateFiles(copied, "*.png", SearchOption.AllDirectories).Any(),
    "Disconnected display state and theme settings are copied. External targets and exports remain outside the snapshot.");

File.WriteAllText(themes, """{"schemaVersion":999,"state":{}}""");
Reject<InvalidDataException>(() => DockReleaseUpgrade.Prepare(active, backups, "0.0.0", "1.0.0"),
    "Unsupported theme data blocks preparation before application files are changed.");
File.WriteAllBytes(themes, themeOriginal);
byte[] indexOriginal = File.ReadAllBytes(index);
File.WriteAllText(index, """{"schemaVersion":999,"legacyMonitorId":"primary-1"}""");
Reject<InvalidDataException>(() => DockReleaseUpgrade.Prepare(active, backups, "0.0.0", "1.0.0"),
    "Unsupported display index blocks preparation.");
File.WriteAllBytes(index, indexOriginal);
WriteProfile(active, 999, target, icon);
byte[] incompatible = File.ReadAllBytes(activeDock);
Reject<InvalidDataException>(() => DockReleaseUpgrade.Validate(active), "Unknown configuration schema is refused.");
Check(Same(activeDock, incompatible), "Compatibility failure leaves the input byte-for-byte intact.");

string changedDisplay = Directory.CreateDirectory(Path.Combine(active, "display-docks", "display-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).FullName;
WriteProfile(changedDisplay, 4, target, icon);
string localDocument = Path.Combine(changedDisplay, "keep.txt");
File.WriteAllText(localDocument, "local external document sentinel");
WriteJournal(active, "Hidden");
byte[] liveJournal = File.ReadAllBytes(journal);
string checkpoint = DockReleaseUpgrade.Restore(active, snapshot);
Check(Same(activeDock, activeOriginal) && Same(themes, themeOriginal) && Same(secondaryDock, secondaryOriginal),
    "Explicit restore reinstates the selected checked configuration even after an incompatible write.");
Check(Same(Path.Combine(checkpoint, "profile", "dock.json"), incompatible),
    "The durable pre-restore checkpoint preserves the incompatible version.");
Check(Same(journal, liveJournal) && File.Exists(localDocument) && File.Exists(unrelated) &&
    File.Exists(runtimeLog) && !File.Exists(Path.Combine(changedDisplay, "dock.json")),
    "Restore preserves current desktop recovery state, diagnostics and unrelated documents.");

string backupThemes = Path.Combine(copied, "themes.json");
File.WriteAllText(backupThemes, new string(' ', themeOriginal.Length));
Reject<InvalidDataException>(() => DockReleaseUpgrade.Restore(active, snapshot),
    "Corrupted snapshot hash blocks restore before the live profile changes.");
Check(Same(activeDock, activeOriginal) && Same(themes, themeOriginal), "A failed snapshot check leaves live bytes intact.");
File.WriteAllBytes(backupThemes, themeOriginal);

string manifestPath = Path.Combine(snapshot, "backup.json");
byte[] originalManifest = File.ReadAllBytes(manifestPath);
JsonNode forged = JsonNode.Parse(originalManifest)!;
forged["files"]![0]!["path"] = "../user-file.txt";
Write(manifestPath, forged);
Reject<InvalidDataException>(() => DockReleaseUpgrade.Restore(active, snapshot),
    "A forged backup manifest cannot name files outside the owned configuration paths.");
Check(Same(activeDock, activeOriginal) && Same(target, targetBytes), "A path traversal attempt leaves both live and external files untouched.");
File.WriteAllBytes(manifestPath, originalManifest);

using (DockSession dock = DockSession.Open(active))
    dock.UpdateOptions(dock.Options with { ShowDockOnStartup = false });
byte[] changedDock = File.ReadAllBytes(activeDock);
store = DockThemeStore.Open(active);
store.Save(store.Current.SelectTheme("tech"));
byte[] changedThemes = File.ReadAllBytes(themes);
using (new FileStream(themes, FileMode.Open, FileAccess.Read, FileShare.Read))
    Reject<IOException>(() => DockReleaseUpgrade.Restore(active, snapshot),
        "A locked destination causes the attempted restore to roll back.", error => error.Message.Contains("检查点"));
Check(Same(activeDock, changedDock) && Same(themes, changedThemes), "Partial restore rolls back earlier successful replacements.");

Reject<InvalidOperationException>(() => DockReleaseUpgrade.RemoveOwnedData(active),
    "Uninstall data removal preserves a pending desktop visibility recovery record.");
Check(Same(journal, liveJournal) && Same(activeDock, changedDock), "Refused removal retains live recovery and configuration data.");
File.WriteAllText(journal, "{broken");
Reject<JsonException>(() => DockReleaseUpgrade.RemoveOwnedData(active), "Corrupt desktop journal blocks data removal.");
WriteJournal(active, "Restored");
DockReleaseUpgrade.RemoveOwnedData(active);
Check(!File.Exists(activeDock) && !File.Exists(themes) && !File.Exists(index) && !File.Exists(secondaryDock) &&
    !File.Exists(runtimeLog) && File.Exists(unrelated) && File.Exists(localDocument) && Same(target, targetBytes) && Same(icon, iconBytes),
    "Explicit uninstall cleanup removes only recognized state files and leaves user files byte-for-byte intact.");

Console.WriteLine($"END passed={passed} failed=0");
