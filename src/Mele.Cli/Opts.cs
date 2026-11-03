namespace Mele.Cli;

/// <summary>Tiny argument parser: positionals, "--name value", and bare "--flag"s from a known set.</summary>
internal sealed class Opts
{
    private static readonly HashSet<string> Flags = ["dry-run", "keep-archives", "help", "skip-textures", "yes", "delete-unknown", "replace"];
    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public Opts(IEnumerable<string> args)
    {
        var it = args.GetEnumerator();
        while (it.MoveNext())
        {
            var a = it.Current;
            if (!a.StartsWith("--")) { Positional.Add(a); continue; }
            var name = a[2..];
            if (Flags.Contains(name)) { Add(name, "true"); continue; }
            if (!it.MoveNext()) throw new ArgumentException($"--{name} needs a value");
            Add(name, it.Current);
        }
    }

    private void Add(string k, string v) { if (!_values.TryGetValue(k, out var l)) _values[k] = l = []; l.Add(v); }
    public string? Get(string k) => _values.TryGetValue(k, out var l) ? l[^1] : null;
    public IReadOnlyList<string> All(string k) => _values.TryGetValue(k, out var l) ? l : [];
    public bool Has(string k) => _values.ContainsKey(k);
    public string Require(string k) => Get(k) ?? throw new ArgumentException($"missing --{k}");
}
