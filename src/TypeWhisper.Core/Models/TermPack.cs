namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents term pack data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="Name">Name supplied to the member.</param>
/// <param name="Icon">Icon supplied to the member.</param>
/// <param name="Terms">Terms supplied to the member.</param>
/// <param name="RequiresCommercialLicense">Requires commercial license supplied to the member.</param>
public sealed record TermPack(string Id, string Name, string Icon, string[] Terms, bool RequiresCommercialLicense = false)
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static readonly HashSet<string> IndustryPackIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "real-estate",
        "architecture",
        "legal"
    };

    /// <summary>
    /// Gets the all packs.
    /// </summary>
    public static readonly TermPack[] AllPacks =
    [
        new("web-dev", "Web Development", "\U0001F310",
        [
            "React", "Vue", "Angular", "TypeScript", "JavaScript", "Next.js", "Nuxt", "Svelte",
            "GraphQL", "REST", "WebSocket", "Webpack", "Vite", "Tailwind", "Sass", "Node.js",
            "Deno", "Bun", "Express", "Remix", "Astro", "SvelteKit", "Vercel", "Netlify", "Supabase"
        ]),
        new("dotnet", ".NET / C#", "\U0001F537",
        [
            "Blazor", "MAUI", "WPF", "ASP.NET", "Entity Framework", "EF Core", "NuGet", "Roslyn",
            "LINQ", "SignalR", "Minimal API", "gRPC", "Kestrel", "MediatR", "Dapper", "xUnit",
            "Moq", "CommunityToolkit", "Avalonia", "Orleans"
        ]),
        new("devops", "DevOps & Cloud", "\u2601\uFE0F",
        [
            "Kubernetes", "Docker", "Terraform", "Ansible", "Jenkins", "GitHub Actions", "GitLab CI",
            "Prometheus", "Grafana", "Helm", "Istio", "ArgoCD", "Pulumi", "Vault", "Consul"
        ]),
        new("data-ai", "Data & AI", "\U0001F916",
        [
            "TensorFlow", "PyTorch", "LangChain", "Hugging Face", "Embeddings", "Transformer",
            "GPT", "BERT", "Ollama", "MLflow", "Jupyter", "Pandas", "NumPy", "Scikit-learn", "RAG"
        ]),
        new("design", "Design", "\U0001F3A8",
        [
            "Figma", "Sketch", "Tailwind", "WCAG", "Wireframe", "Lottie", "Storybook", "Framer",
            "Radix", "Shadcn", "Material Design", "Accessibility", "Responsive", "Breakpoint", "Viewport"
        ]),
        new("gamedev", "Game Development", "\U0001F3AE",
        [
            "Unity", "Unreal", "Godot", "OpenGL", "Vulkan", "DirectX", "Shader", "Raytracing",
            "PhysX", "Blender", "Maya", "Sprite", "Tilemap", "NavMesh", "GameLoop"
        ]),
        new("mobile", "Mobile Development", "\U0001F4F1",
        [
            "Flutter", "React Native", "Kotlin", "Swift", "SwiftUI", "Jetpack Compose", "Expo",
            "Capacitor", "Xamarin", "CoreData", "Room", "Firebase", "TestFlight", "CocoaPods"
        ]),
        new("security", "Cybersecurity", "\U0001F512",
        [
            "OWASP", "CVE", "Pentest", "Firewall", "Zero Trust", "OAuth", "JWT", "SAML",
            "XSS", "CSRF", "SQL Injection", "SIEM", "SOC", "Ransomware", "Phishing"
        ]),
        new("databases", "Datenbanken", "\U0001F5C4\uFE0F",
        [
            "PostgreSQL", "MongoDB", "Redis", "Elasticsearch", "Cassandra", "DynamoDB", "SQLite",
            "MariaDB", "CockroachDB", "InfluxDB", "Neo4j", "Supabase", "PlanetScale", "Prisma", "Drizzle"
        ]),
        new("medical", "Medizin", "\u2695\uFE0F",
        [
            "Anamnese", "Diagnose", "Pathologie", "EKG", "MRT", "CT", "Ultraschall", "Biopsie",
            "Anästhesie", "Kardiologie", "Onkologie", "Orthopädie", "Neurologie", "Pädiatrie", "Radiologie"
        ]),
        new("finance", "Finanzen", "\U0001F4B0",
        [
            "Portfolio", "Derivat", "Bilanz", "EBITDA", "Hedging", "Cashflow", "Rendite", "Dividende",
            "Aktie", "Anleihe", "ETF", "Kryptowährung", "Blockchain", "Fintech", "Liquidität"
        ]),
        new("music", "Musik-Produktion", "\U0001F3B5",
        [
            "DAW", "MIDI", "Equalizer", "Kompressor", "VST", "Synthesizer", "Reverb", "Delay",
            "Sidechain", "Mastering", "Mixing", "Limiter", "Chorus", "Phaser", "Arpeggiator"
        ])
    ];

    /// <summary>
    /// Finds a built-in term pack by identifier using case-insensitive matching.
    /// </summary>
    public static TermPack? FindById(string id) =>
        AllPacks.FirstOrDefault(pack => pack.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns term packs visible to the current license tier.
    /// </summary>
    public static IEnumerable<TermPack> VisiblePacks(bool hasCommercialLicense) =>
        AllPacks.Where(pack => hasCommercialLicense || !pack.RequiresCommercialLicense);
}

/// <summary>
/// Represents industry preset data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="Name">Name supplied to the member.</param>
/// <param name="Description">Description supplied to the member.</param>
/// <param name="TermPackId">Term pack id supplied to the member.</param>
public sealed record IndustryPreset(string Id, string Name, string Description, string? TermPackId)
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static readonly IndustryPreset General = new(
        "general",
        "General writing",
        "Start with TypeWhisper defaults. You can add term packs later.",
        null);

    /// <summary>
    /// Gets the all.
    /// </summary>
    public static readonly IndustryPreset[] All =
    [
        General,
        new("real-estate", "Immobilien", "Prepare property, viewing, and client vocabulary.", "real-estate"),
        new("architecture", "Architektur", "Prepare planning, construction, and defect vocabulary.", "architecture"),
        new("legal", "Jura", "Prepare legal dictation vocabulary for drafts and notes.", "legal")
    ];

    /// <summary>
    /// Resolves the supplied input to a configured value.
    /// </summary>
    public static IndustryPreset Resolve(string? id) =>
        All.FirstOrDefault(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? General;
}
