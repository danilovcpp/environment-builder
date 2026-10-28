namespace EnvironmentBuilder.App.Components;

public sealed record ServicePresentation(string Name, string Category, string Source, string Icon, string Color, bool Enabled = false, bool Build = false)
{
    public static IReadOnlyList<ServicePresentation> Examples { get; } =
    [
        new("ApprovalWorkflow", "Backend", "registry.local/eoffice/approval:latest", "Settings", "#FF8500", true),
        new("Auth", "Backend", "registry.local/eoffice/auth:latest", "Database", "#1B659A"),
        new("FileSystem", "Backend", "registry.local/eoffice/filesystem:latest", "FileText", "#0072FF"),
        new("VideoProcessor", "Worker", "registry.local/eoffice/video-processor:dev", "Play", "#0072FF", false, true),
        new("BackAPI", "Backend", "registry.local/eoffice/backapi:latest", "Settings", "#FF8500", true),
        new("Documents", "Backend", "registry.local/eoffice/documents:latest", "FileText", "#0072FF"),
        new("FileStorage", "Infrastructure", "registry.local/eoffice/filestorage:latest", "Database", "#0072FF"),
        new("Frontend.Admin", "Frontend", @"D:\src\eoffice\services\Frontend.Admin", "Globe", "#0072FF", false, true),
        new("Processing", "Worker", @"D:\src\eoffice\services\Processing", "Code", "#832FFF", false, true),
        new("Push", "Backend", "registry.local/eoffice/push:latest", "Bell", "#0072FF"),
        new("SupportChat", "Backend", "registry.local/eoffice/supportchat:latest", "MessageCircle", "#0072FF"),
        new("UserActivity", "Backend", "registry.local/eoffice/useractivity:latest", "UserRound", "#0072FF"),
        new("WebSockets", "Backend", "registry.local/eoffice/websockets:latest", "Wifi", "#00B5B6"),
        new("Frontend", "Frontend", "ghcr.io/eoffice/frontend:dev", "Globe", "#0072FF", true),
        new("Logging", "Infrastructure", "registry.local/eoffice/logging:latest", "FileText", "#0072FF")
    ];
    public static IReadOnlyList<ServicePresentation> Infrastructure { get; } =
    [
        new("PostgreSQL", "Infrastructure", "postgres:15", "Database", "#1B659A", true),
        new("RabbitMQ", "Infrastructure", "rabbitmq:3.12-management", "Component", "#FF8500", true),
        new("Kafka", "Infrastructure", "bitnami/kafka:latest", "Network", "#20242E"),
        new("MinIO", "Infrastructure", "minio/minio:latest", "HardDrive", "#D50047")
    ];
}
