namespace TypeWhisper.Core.Models;

public sealed record TermPack(string Id, string Name, string Icon, string[] Terms)
{
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
        new("legal", "Jura", "\u2696\uFE0F",
        [
            "Klausel", "Haftung", "Vertragsrecht", "DSGVO", "Compliance", "Insolvenz", "Urheberrecht",
            "Markenrecht", "Patentrecht", "Arbeitsrecht", "Strafrecht", "Zivilrecht", "Schiedsverfahren",
            "Datenschutz", "Gewährleistung"
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
}
