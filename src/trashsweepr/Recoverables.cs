using System.IO;

namespace TrashSweepr;

/// <param name="Label">O que a pasta é ("Dependências npm").</param>
/// <param name="Command">Como ela volta ("npm install").</param>
/// <param name="IsProject">Pertence a um projeto (faz sentido mostrar há quanto tempo ele não é mexido).</param>
public sealed record RecoverableRule(string Label, string Command, bool IsProject = true);

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

    static readonly DirRule[] Rules =
    [
        new("node_modules", Npm, new("Dependências npm", "npm install")),
        new(".next", Npm, new("Build do Next.js", "npm run build")),
        new(".nuxt", Npm, new("Build do Nuxt", "npm run build")),
        new(".svelte-kit", Npm, new("Build do SvelteKit", "npm run build")),
        new(".turbo", Npm, new("Cache do Turborepo", "recriado no próximo build")),
        new(".parcel-cache", Npm, new("Cache do Parcel", "recriado no próximo build")),
        new(".angular", ["angular.json"], new("Cache do Angular", "recriado no próximo build")),
        new("target", ["Cargo.toml"], new("Build do Rust", "cargo build")),
        new("bin", DotNet, new("Build do .NET", "dotnet build")),
        new("obj", DotNet, new("Intermediários do .NET", "dotnet build")),
        new(".venv", Python, new("Ambiente virtual Python", "python -m venv .venv e pip install")),
        new("venv", Python, new("Ambiente virtual Python", "python -m venv venv e pip install")),
        new("__pycache__", Anywhere, new("Bytecode Python", "recriado ao rodar", IsProject: false)),
        new(".pytest_cache", Anywhere, new("Cache do pytest", "recriado ao rodar os testes", IsProject: false)),
        new(".mypy_cache", Anywhere, new("Cache do mypy", "recriado na próxima checagem", IsProject: false)),
        new(".ruff_cache", Anywhere, new("Cache do Ruff", "recriado na próxima checagem", IsProject: false)),
        new(".gradle", Gradle, new("Cache do Gradle do projeto", "gradlew build")),
        new("build", Gradle, new("Build do Gradle", "gradlew build")),
        new(".dart_tool", ["pubspec.yaml"], new("Cache do Dart/Flutter", "flutter pub get")),
        new("build", ["pubspec.yaml"], new("Build do Flutter", "flutter build")),
        new("build", ["CMakeLists.txt"], new("Build do CMake", "cmake --build")),
    ];

    /// <summary>Caches globais de gerenciadores de pacote: baixados de novo sob demanda.</summary>
    static readonly Dictionary<string, RecoverableRule> GlobalCaches = BuildGlobalCaches();

    static Dictionary<string, RecoverableRule> BuildGlobalCaches()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var map = new Dictionary<string, RecoverableRule>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string label, string command) => map[Path.GetFullPath(path)] = new(label, command, IsProject: false);

        Add(Path.Combine(local, "npm-cache"), "Cache do npm", "baixado de novo quando preciso");
        Add(Path.Combine(local, "Yarn", "Cache"), "Cache do Yarn", "baixado de novo quando preciso");
        Add(Path.Combine(local, "pnpm-cache"), "Cache do pnpm", "baixado de novo quando preciso");
        Add(Path.Combine(local, "pip", "Cache"), "Cache do pip", "baixado de novo quando preciso");
        Add(Path.Combine(home, ".nuget", "packages"), "Pacotes NuGet", "dotnet restore baixa de novo");
        Add(Path.Combine(home, ".cargo", "registry"), "Registro do Cargo", "cargo build baixa de novo");
        Add(Path.Combine(home, ".gradle", "caches"), "Cache global do Gradle", "gradlew build baixa de novo");
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
