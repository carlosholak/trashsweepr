using System.IO;
using TrashSweepr.Localization;

namespace TrashSweepr;

/// <summary>
/// O que a pasta é ("Dependências npm") e como ela volta ("npm install"). Os dois textos são montados
/// na hora de mostrar, na língua da interface; comandos ficam sempre como são digitados.
/// </summary>
/// <param name="isProject">Pertence a um projeto (faz sentido mostrar há quanto tempo ele não é mexido).</param>
public sealed class RecoverableRule(Func<string> label, Func<string> command, bool isProject = true)
{
    public string Label => label();
    public string Command => command();
    public bool IsProject { get; } = isProject;
}

/// <summary>
/// Reconhece pastas que são cache ou resultado de build. Uma pasta só conta se o arquivo de projeto
/// esperado estiver ao lado dela (ex.: "target" só com Cargo.toml) — um "bin" qualquer não é marcado.
/// </summary>
public static class Recoverables
{
    sealed record DirRule(string Name, string[] Markers, RecoverableRule Rule);

    static readonly string[] Npm = ["package.json"];
    static readonly string[] DotNet = ["*.csproj", "*.fsproj", "*.vbproj"];
    static readonly string[] Python = ["pyproject.toml", "requirements.txt", "setup.py", "Pipfile"];
    static readonly string[] Gradle = ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"];
    static readonly string[] Anywhere = [];

    // Textos resolvidos só na hora de mostrar: a regra é criada uma vez, a língua é a da interface no momento.
    static Func<string> T(string key, params object[] args) => () => Loc.Format(key, args);
    static Func<string> Cmd(string command) => () => command; // comando: igual em qualquer língua
    static Func<string> Build(string tool) => T("Disk.Rec.Build", tool);
    static Func<string> Cache(string tool) => T("Disk.Rec.Cache", tool);
    static RecoverableRule Rule(Func<string> label, Func<string> command, bool isProject = true) => new(label, command, isProject);

    static readonly DirRule[] Rules =
    [
        new("node_modules", Npm, Rule(T("Disk.Rec.NpmDeps"), Cmd("npm install"))),
        new(".next", Npm, Rule(Build("Next.js"), Cmd("npm run build"))),
        new(".nuxt", Npm, Rule(Build("Nuxt"), Cmd("npm run build"))),
        new(".svelte-kit", Npm, Rule(Build("SvelteKit"), Cmd("npm run build"))),
        new(".turbo", Npm, Rule(Cache("Turborepo"), T("Disk.Rec.Cmd.NextBuild"))),
        new(".parcel-cache", Npm, Rule(Cache("Parcel"), T("Disk.Rec.Cmd.NextBuild"))),
        new(".angular", ["angular.json"], Rule(Cache("Angular"), T("Disk.Rec.Cmd.NextBuild"))),
        new("target", ["Cargo.toml"], Rule(Build("Rust"), Cmd("cargo build"))),
        new("bin", DotNet, Rule(Build(".NET"), Cmd("dotnet build"))),
        new("obj", DotNet, Rule(T("Disk.Rec.DotNetObj"), Cmd("dotnet build"))),
        new(".venv", Python, Rule(T("Disk.Rec.PythonVenv"), T("Disk.Rec.Cmd.Venv", ".venv"))),
        new("venv", Python, Rule(T("Disk.Rec.PythonVenv"), T("Disk.Rec.Cmd.Venv", "venv"))),
        new("__pycache__", Anywhere, Rule(T("Disk.Rec.PythonBytecode"), T("Disk.Rec.Cmd.OnRun"), isProject: false)),
        new(".pytest_cache", Anywhere, Rule(Cache("pytest"), T("Disk.Rec.Cmd.OnTests"), isProject: false)),
        new(".mypy_cache", Anywhere, Rule(Cache("mypy"), T("Disk.Rec.Cmd.NextCheck"), isProject: false)),
        new(".ruff_cache", Anywhere, Rule(Cache("Ruff"), T("Disk.Rec.Cmd.NextCheck"), isProject: false)),
        new(".gradle", Gradle, Rule(T("Disk.Rec.GradleProjectCache"), Cmd("gradlew build"))),
        new("build", Gradle, Rule(Build("Gradle"), Cmd("gradlew build"))),
        new(".dart_tool", ["pubspec.yaml"], Rule(Cache("Dart/Flutter"), Cmd("flutter pub get"))),
        new("build", ["pubspec.yaml"], Rule(Build("Flutter"), Cmd("flutter build"))),
        new("build", ["CMakeLists.txt"], Rule(Build("CMake"), Cmd("cmake --build"))),
    ];

    /// <summary>Caches globais de gerenciadores de pacote: baixados de novo sob demanda.</summary>
    static readonly Dictionary<string, RecoverableRule> GlobalCaches = BuildGlobalCaches();

    static Dictionary<string, RecoverableRule> BuildGlobalCaches()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var map = new Dictionary<string, RecoverableRule>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, Func<string> label, Func<string> command) =>
            map[Path.GetFullPath(path)] = new(label, command, isProject: false);

        var onDemand = T("Disk.Rec.Cmd.OnDemand");
        Add(Path.Combine(local, "npm-cache"), Cache("npm"), onDemand);
        Add(Path.Combine(local, "Yarn", "Cache"), Cache("Yarn"), onDemand);
        Add(Path.Combine(local, "pnpm-cache"), Cache("pnpm"), onDemand);
        Add(Path.Combine(local, "pip", "Cache"), Cache("pip"), onDemand);
        Add(Path.Combine(home, ".nuget", "packages"), T("Disk.Rec.NuGetPackages"), T("Disk.Rec.Cmd.DownloadsAgain", "dotnet restore"));
        Add(Path.Combine(home, ".cargo", "registry"), T("Disk.Rec.CargoRegistry"), T("Disk.Rec.Cmd.DownloadsAgain", "cargo build"));
        Add(Path.Combine(home, ".gradle", "caches"), T("Disk.Rec.GradleGlobalCache"), T("Disk.Rec.Cmd.DownloadsAgain", "gradlew build"));
        return map;
    }

    /// <summary>Marca a árvore inteira.</summary>
    public static void Mark(DiskNode root) => Visit(root, root.FullPath, new Dictionary<DiskNode, long>());

    static void Visit(DiskNode dir, string path, Dictionary<DiskNode, long> activityCache)
    {
        dir.Recoverable = null;
        dir.ProjectActivityTicks = 0;
        if (Match(dir, path) is { } rule)
        {
            dir.Recoverable = rule;
            if (rule.IsProject && dir.Parent is { } project)
                dir.ProjectActivityTicks = Activity(project, activityCache);
            return;
        }

        var kids = dir.Children;
        if (kids is null) return;
        for (int i = 0; i < kids.Count; i++)
        {
            var c = kids[i];
            if (c.IsDirectory && !c.IsLink) Visit(c, Path.Combine(path, c.Name), activityCache);
        }
    }

    static RecoverableRule? Match(DiskNode dir, string path)
    {
        if (!dir.IsDirectory || dir.IsLink) return null;
        if (GlobalCaches.TryGetValue(Path.TrimEndingDirectorySeparator(path), out var global)) return global;
        return MatchProjectRule(dir);
    }

    static RecoverableRule? MatchProjectRule(DiskNode dir)
    {
        foreach (var r in Rules)
        {
            if (!dir.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Markers.Length == 0) return r.Rule;
            if (dir.Parent?.Children is { } siblings && HasMarker(siblings, r.Markers)) return r.Rule;
        }
        return null;
    }

    static bool HasMarker(List<DiskNode> siblings, string[] markers)
    {
        for (int i = 0; i < siblings.Count; i++)
        {
            var s = siblings[i];
            if (s.IsDirectory) continue;
            foreach (var m in markers)
            {
                bool hit = m.StartsWith('*')
                    ? s.Name.EndsWith(m[1..], StringComparison.OrdinalIgnoreCase)
                    : s.Name.Equals(m, StringComparison.OrdinalIgnoreCase);
                if (hit) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Última vez que alguém mexeu num arquivo do projeto. Só arquivos contam (a data da pasta muda
    /// quando um build recria node_modules), e pastas recriáveis e .git ficam de fora.
    /// </summary>
    static long Activity(DiskNode dir, Dictionary<DiskNode, long> cache)
    {
        if (cache.TryGetValue(dir, out long cached)) return cached;
        long max = 0;
        var kids = dir.Children;
        if (kids is not null)
        {
            for (int i = 0; i < kids.Count; i++)
            {
                var c = kids[i];
                if (!c.IsDirectory) max = Math.Max(max, c.LastWriteTicks);
                else if (!c.IsLink && !c.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) && MatchProjectRule(c) is null)
                    max = Math.Max(max, Activity(c, cache));
            }
        }
        cache[dir] = max;
        return max;
    }
}
